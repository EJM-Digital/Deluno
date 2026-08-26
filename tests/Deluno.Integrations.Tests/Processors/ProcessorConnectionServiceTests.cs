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
        Assert.Equal("handoff-1", Assert.Single(idempotencyValues));
        Assert.Equal("Bearer processor-token", captured.Headers.Authorization!.ToString());
        var payload = capturedPayload!;
        Assert.Contains("\"handoffId\":\"handoff-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"libraryId\":\"library-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"callbackPath\":\"/api/integrations/processors/events\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("processor-token", payload, StringComparison.Ordinal);
    }

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
        Assert.Contains("reachable", result.Message, StringComparison.OrdinalIgnoreCase);
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
