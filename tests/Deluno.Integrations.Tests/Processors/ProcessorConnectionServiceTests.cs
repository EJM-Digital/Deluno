using System.Net;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

public sealed class ProcessorConnectionServiceTests
{
    [Fact]
    public async Task SubmitAsync_posts_correlated_handoff_without_exposing_unrelated_state()
    {
        HttpRequestMessage? captured = null;
        string? capturedPayload = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
        var connection = new ProcessorConnectionItem(
            "connection-1", "FileFlows", "fileflows-webhook", "https://processor.example.test/deluno",
            "Authorization", "processor-token", true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "library-1", "movies", "client-1", "queue-1", "Dune Part Two",
            "/downloads/dune.mkv", "FileFlows", "waiting", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await service.SubmitAsync(connection, handoff, CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Equal("submitted", result.Status);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.True(captured.Headers.TryGetValues("Idempotency-Key", out var idempotencyValues));
        Assert.StartsWith("handoff-1:", Assert.Single(idempotencyValues), StringComparison.Ordinal);
        Assert.Equal("Bearer processor-token", captured.Headers.Authorization!.ToString());
        var payload = capturedPayload!;
        Assert.Contains("\"handoffId\":\"handoff-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"libraryId\":\"library-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"callbackPath\":\"/api/integrations/processors/events\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("processor-token", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal keeps the processor's own words.
    ///
    /// <para>On the rig Weir refused a hand-off with a complete, actionable
    /// sentence - "Refiner's watched folder is not set for this media scope" -
    /// and Deluno recorded "Processor submission returned HTTP 400." The one
    /// thing that named the setting to change was in the response body and was
    /// thrown away, leaving an owner with a number and nowhere to go.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_handoff_keeps_what_the_processor_said()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"detail\":\"Refiner's watched folder is not set for this media scope.\"}")
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        var result = await service.SubmitAsync(Connection(), Handoff(DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Contains("Refiner's watched folder is not set", result.Message, StringComparison.Ordinal);
        Assert.Contains("400", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a processor that says nothing still reports the status it gave.
    /// </summary>
    [Fact]
    public async Task A_refusal_with_no_body_still_reports_its_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        var result = await service.SubmitAsync(Connection(), Handoff(DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Contains("503", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same hand-off, unchanged, is the same request twice.
    ///
    /// <para>Which is what an idempotency key is for: a retry after a timeout
    /// must not make the processor convert the file a second time.</para>
    /// </summary>
    [Fact]
    public async Task Submitting_the_same_unchanged_handoff_twice_carries_the_same_key()
    {
        var keys = new List<string>();
        var service = ServiceCapturing(keys);
        var handoff = Handoff(DateTimeOffset.Parse("2026-09-05T10:00:00Z"));

        await service.SubmitAsync(Connection(), handoff, CancellationToken.None);
        await service.SubmitAsync(Connection(), handoff, CancellationToken.None);

        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
    }

    /// <summary>
    /// A hand-off deliberately put back to waiting is a new request.
    ///
    /// <para>This is the half the key used to get wrong, and it took a forced
    /// re-download to expose it. The override resets the row rather than
    /// deleting it — deleting it would lose the record that stops one download
    /// being submitted twice — so under a key of the id alone the processor
    /// recognised a request it had already answered and declined to process a
    /// file the person had just explicitly asked it to process again. The owner:
    /// "Weir will also need to have that record cleared as well so it can
    /// re-process the file again if a user forces it."</para>
    /// </summary>
    [Fact]
    public async Task A_handoff_put_back_to_waiting_carries_a_new_key()
    {
        var keys = new List<string>();
        var service = ServiceCapturing(keys);

        await service.SubmitAsync(Connection(), Handoff(DateTimeOffset.Parse("2026-09-05T10:00:00Z")), CancellationToken.None);
        // What AcquisitionOverrideService does: same row, same id, status back
        // to waiting, and therefore a later updated_utc.
        await service.SubmitAsync(Connection(), Handoff(DateTimeOffset.Parse("2026-09-05T11:30:00Z")), CancellationToken.None);

        Assert.Equal(2, keys.Count);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.All(keys, key => Assert.StartsWith("handoff-1:", key, StringComparison.Ordinal));
    }

    private static ProcessorConnectionService ServiceCapturing(List<string> keys)
    {
        var handler = new StubHandler(request =>
        {
            if (request.Headers.TryGetValues("Idempotency-Key", out var values))
            {
                keys.Add(values.Single());
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });

        return new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
    }

    private static ProcessorConnectionItem Connection()
        => new(
            "connection-1", "Weir", "generic-webhook", "https://processor.example.test/deluno",
            "Authorization", "processor-token", true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ProcessorHandoffItem Handoff(DateTimeOffset updatedUtc)
        => new(
            "handoff-1", "library-1", "movies", "client-1", "queue-1", "Dune Part Two",
            "/downloads/dune.mkv", "Weir", "waiting", null, null, null,
            DateTimeOffset.Parse("2026-09-05T09:00:00Z"), updatedUtc);

    [Fact]
    public async Task TestAsync_marks_reachable_endpoint_with_bad_credentials_as_degraded()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
        var connection = new ProcessorConnectionItem(
            "connection-1", "Processor", "generic-webhook", "https://processor.example.test/health",
            "X-Api-Key", "bad-token", true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await service.TestAsync(connection, CancellationToken.None);

        Assert.False(result.IsReachable);
        Assert.Equal("degraded", result.Status);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // 405 is what the spec says a POST-only route should answer a HEAD with.
    [InlineData(HttpStatusCode.MethodNotAllowed, 405)]
    // 404 is what FastAPI actually answers, and Weir is a FastAPI app. Treating
    // this as unreachable reported a working processor as broken.
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.NotImplemented, 501)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task TestAsync_treats_any_answer_to_the_head_probe_as_reachable(HttpStatusCode status, int expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
        var connection = new ProcessorConnectionItem(
            "connection-1", "Weir", "generic-webhook", "https://processor.example.test/webhook",
            "X-Webhook-Secret", null, true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await service.TestAsync(connection, CancellationToken.None);

        Assert.True(result.IsReachable);
        Assert.Equal("degraded", result.Status);
        Assert.Equal(expected, result.StatusCode);
        // It says what was and was not checked, and how to get a real check.
        Assert.Contains("could not confirm it is working", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("health-check address", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_reports_a_processor_it_cannot_reach_at_all_as_unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
        var connection = new ProcessorConnectionItem(
            "connection-1", "Weir", "generic-webhook", "https://processor.example.test/webhook",
            "X-Webhook-Secret", null, true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await service.TestAsync(connection, CancellationToken.None);

        Assert.False(result.IsReachable);
        Assert.Equal("unreachable", result.Status);
        Assert.Null(result.StatusCode);
    }

    private static ProcessorConnectionItem WithHealthCheck(string healthUrl, string? secret = "hook-secret")
        => new(
            "connection-1", "Weir", "generic-webhook", "http://127.0.0.1:9347/api/v1/intake/webhook/deluno",
            "X-Webhook-Secret", secret, true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, healthUrl);

    /// <summary>#1055: the run-6 failure. Weir's hand-off route is POST-only; its /ready answers 200 when it is up.</summary>
    [Fact]
    public async Task TestAsync_with_a_health_check_address_asks_it_with_GET_and_a_2xx_answer_is_healthy()
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request);
            // The hand-off route would answer 405 to anything but a POST.
            return request.RequestUri!.AbsolutePath == "/ready"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        var result = await service.TestAsync(WithHealthCheck("http://127.0.0.1:9347/ready"), CancellationToken.None);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.IsReachable);
        Assert.Equal(200, result.StatusCode);
        var request = Assert.Single(seen);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/ready", request.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task TestAsync_with_a_health_check_address_says_in_plain_words_when_it_is_not_healthy(HttpStatusCode status, bool reachable)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        var result = await service.TestAsync(WithHealthCheck("http://127.0.0.1:9347/ready"), CancellationToken.None);

        Assert.Equal("degraded", result.Status);
        Assert.Equal(reachable, result.IsReachable);
        Assert.Contains(((int)status).ToString(), result.Message, StringComparison.Ordinal);
        Assert.Contains("health check", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_with_a_health_check_address_that_does_not_answer_is_unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        var result = await service.TestAsync(WithHealthCheck("http://127.0.0.1:9347/ready"), CancellationToken.None);

        Assert.Equal("unreachable", result.Status);
        Assert.False(result.IsReachable);
    }

    [Fact]
    public async Task The_credential_is_sent_to_the_processors_own_host_only()
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));

        await service.TestAsync(WithHealthCheck("http://127.0.0.1:9347/ready"), CancellationToken.None);
        await service.TestAsync(WithHealthCheck("https://status.other.example/ready"), CancellationToken.None);

        Assert.True(seen[0].Headers.Contains("X-Webhook-Secret"));
        Assert.False(seen[1].Headers.Contains("X-Webhook-Secret"));
    }

    [Fact]
    public async Task TestAsync_without_a_health_check_address_is_unchanged_a_HEAD_to_the_hand_off_address()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(new HttpClient(handler)));
        var connection = WithHealthCheck("http://unused.invalid/ready") with { HealthCheckUrl = null };

        var result = await service.TestAsync(connection, CancellationToken.None);

        Assert.Equal(HttpMethod.Head, seen!.Method);
        Assert.Equal("healthy", result.Status);
        Assert.Contains("cannot check more than this", result.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
