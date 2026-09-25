using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;

namespace Deluno.Platform.Tests.Resilience;

public sealed class IntegrationResiliencePolicyTests
{
    [Fact]
    public async Task Retries_retryable_results_until_success()
    {
        var policy = CreatePolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(
            new IntegrationResilienceRequest(
                "indexer:test",
                "indexer.search",
                InitialDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero),
            _ =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? "temporary-failure" : "ok");
            },
            value => value == "ok"
                ? IntegrationResilienceOutcome.Success
                : IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        Assert.Equal("ok", result.Value);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
        Assert.False(result.CircuitOpen);
        Assert.False(result.CircuitOpened);
    }

    [Fact]
    public async Task Allows_more_than_the_old_five_attempt_ceiling()
    {
        var policy = CreatePolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(
            new IntegrationResilienceRequest(
                "integration:wide-attempts",
                "integration.test",
                MaxAttempts: 6,
                FailureThreshold: 200,
                InitialDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero),
            _ =>
            {
                calls++;
                return Task.FromResult(calls == 6 ? "ok" : "temporary-failure");
            },
            value => value == "ok"
                ? IntegrationResilienceOutcome.Success
                : IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        Assert.Equal("ok", result.Value);
        Assert.Equal(6, result.Attempts);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Opens_circuit_after_persistent_retryable_failures()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        var policy = CreatePolicy(timeProvider);
        var calls = 0;
        var request = new IntegrationResilienceRequest(
            "download-client:test",
            "download-client.telemetry",
            MaxAttempts: 2,
            FailureThreshold: 1,
            InitialDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero,
            BreakDuration: TimeSpan.FromMinutes(5));

        var first = await policy.ExecuteAsync(
            request,
            _ =>
            {
                calls++;
                return Task.FromResult("still-down");
            },
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        var second = await policy.ExecuteAsync(
            request,
            _ =>
            {
                calls++;
                return Task.FromResult("should-not-run");
            },
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);

        Assert.True(first.CircuitOpened);
        Assert.False(first.CircuitOpen);
        Assert.True(second.CircuitOpen);
        Assert.Equal(0, second.Attempts);
        Assert.Equal(2, calls);
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(5), second.RetryAfterUtc);
    }

    [Fact]
    public async Task Escalates_the_break_on_every_consecutive_opening_up_to_the_cap()
    {
        // #732: a dead indexer or client re-opened its circuit for exactly
        // one minute forever. Each opening in an unbroken streak should now
        // climb the same ladder Radarr/Sonarr/Prowlarr use, and the streak
        // must not climb past the last rung once it is reached.
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        var policy = CreatePolicy(timeProvider);
        var request = new IntegrationResilienceRequest(
            "indexer:escalating",
            "indexer.search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            InitialDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero);

        TimeSpan[] expectedBreaks =
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(3) // one opening past the ladder: stays capped, does not grow further.
        ];

        foreach (var expectedBreak in expectedBreaks)
        {
            var openedAt = timeProvider.GetUtcNow();
            var opened = await policy.ExecuteAsync(
                request,
                _ => Task.FromResult("still-down"),
                _ => IntegrationResilienceOutcome.RetryableFailure,
                CancellationToken.None);

            Assert.True(opened.CircuitOpened);
            Assert.Equal(openedAt.Add(expectedBreak), opened.RetryAfterUtc);

            // Land exactly on the moment the break ends, so the next failure
            // opens a fresh circuit instead of being swallowed by the
            // still-open one.
            timeProvider.Advance(expectedBreak);
        }
    }

    [Fact]
    public async Task A_success_resets_the_escalation_ladder_back_to_the_first_rung()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        var policy = CreatePolicy(timeProvider);
        var request = new IntegrationResilienceRequest(
            "download-client:recovers",
            "download-client.telemetry",
            MaxAttempts: 1,
            FailureThreshold: 1,
            InitialDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero);

        // Climb to the second rung (1m, then 5m).
        var first = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(1), first.RetryAfterUtc - timeProvider.GetUtcNow());
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        var second = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("still-down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(5), second.RetryAfterUtc - timeProvider.GetUtcNow());
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // A success in between clears the circuit entirely.
        var recovered = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("ok"),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);
        Assert.True(recovered.Succeeded);

        // The next opening starts back at the first (one-minute) rung, not
        // where the ladder left off.
        var reopened = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down-again"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.True(reopened.CircuitOpened);
        Assert.Equal(TimeSpan.FromMinutes(1), reopened.RetryAfterUtc - timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task An_explicit_break_duration_still_wins_over_escalation()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        var policy = CreatePolicy(timeProvider);
        var request = new IntegrationResilienceRequest(
            "metadata:explicit-break",
            "metadata.tmdb.search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            InitialDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero,
            BreakDuration: TimeSpan.FromSeconds(30));

        var opened = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        Assert.True(opened.CircuitOpened);
        Assert.Equal(TimeSpan.FromSeconds(30), opened.RetryAfterUtc - timeProvider.GetUtcNow());
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var reopened = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("still-down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        // Escalation would have moved to the second rung (5 minutes); an
        // explicit break keeps using its own fixed duration instead.
        Assert.Equal(TimeSpan.FromSeconds(30), reopened.RetryAfterUtc - timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task Circuit_open_failure_message_says_when_the_next_try_is()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        var policy = CreatePolicy(timeProvider);
        var request = new IntegrationResilienceRequest(
            "indexer:messaging",
            "indexer.search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            InitialDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero,
            ServiceType: "indexer",
            ServiceId: "messaging-id",
            ServiceName: "Messaging Indexer");

        await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        var blocked = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("should-not-run"),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);

        Assert.True(blocked.CircuitOpen);
        var expectedTime = blocked.RetryAfterUtc!.Value.ToLocalTime().ToString("HH:mm");
        Assert.Contains(expectedTime, blocked.FailureMessage);
        Assert.Contains("try again", blocked.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Does_not_retry_or_open_circuit_for_non_retryable_failures()
    {
        var policy = CreatePolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(
            new IntegrationResilienceRequest(
                "metadata:test",
                "metadata.tmdb.search",
                FailureThreshold: 1,
                InitialDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero),
            _ =>
            {
                calls++;
                return Task.FromResult("bad-api-key");
            },
            _ => IntegrationResilienceOutcome.NonRetryableFailure,
            CancellationToken.None);

        Assert.Equal("bad-api-key", result.Value);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, calls);
        Assert.False(policy.IsCircuitOpen("metadata:test", out _));
    }

    [Fact]
    public async Task Retries_transient_http_exceptions()
    {
        var policy = CreatePolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(
            new IntegrationResilienceRequest(
                "indexer:exception",
                "indexer.health-test",
                InitialDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero),
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout);
                }

                return Task.FromResult("ok");
            },
            value => value == "ok"
                ? IntegrationResilienceOutcome.Success
                : IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);

        Assert.Equal("ok", result.Value);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Converts_non_retryable_http_exceptions_to_typed_failures_without_opening_a_circuit()
    {
        var policy = CreatePolicy();

        var result = await policy.ExecuteAsync<string>(
            new IntegrationResilienceRequest(
                "download-client:auth-failure",
                "download-client.telemetry",
                MaxAttempts: 3,
                FailureThreshold: 1,
                InitialDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero),
            _ => throw new HttpRequestException(
                "The client rejected the API key.",
                null,
                HttpStatusCode.Unauthorized),
            _ => IntegrationResilienceOutcome.Success,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(1, result.Attempts);
        Assert.False(result.CircuitOpened);
        Assert.Equal(IntegrationFailureKind.Authentication, result.Failure!.Kind);
        Assert.Equal(IntegrationRetryState.ManualAction, result.Failure.RetryState);
        Assert.Equal(401, result.Failure.HttpStatus);
        Assert.Equal("The client rejected the API key.", result.Failure.Message);
        Assert.False(policy.IsCircuitOpen("download-client:auth-failure", out _));
    }

    [Fact]
    public async Task Rethrows_caller_cancellation_instead_of_recording_it_as_an_integration_failure()
    {
        var policy = CreatePolicy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => policy.ExecuteAsync(
            new IntegrationResilienceRequest("integration:cancel", "integration.test"),
            _ => Task.FromResult("never"),
            _ => IntegrationResilienceOutcome.Success,
            cancellation.Token));
    }

    private static IntegrationResiliencePolicy CreatePolicy(TimeProvider? timeProvider = null)
    {
        var mockFactory = new Mock<IDelunoDatabaseConnectionFactory>();
        return new(
            timeProvider ?? TimeProvider.System,
            mockFactory.Object,
            NullLogger<IntegrationResiliencePolicy>.Instance);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
