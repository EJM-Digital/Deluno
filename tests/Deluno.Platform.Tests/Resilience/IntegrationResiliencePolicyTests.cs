using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
