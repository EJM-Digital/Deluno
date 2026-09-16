using System.Net;
using System.Text;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #511: the questions Deluno asks a processor about a hand-off, and how each
/// answer is read. Shaped by Weir #480.
/// </summary>
public sealed class AskingTheProcessorTests
{
    private static readonly ProcessorConnectionItem Weir = new(
        "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", "shared-secret", true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("http://weir:8788/api/v1/intake/webhook/deluno", "http://weir:8788/api/v1/intake/capabilities", "http://weir:8788/api/v1/intake/handoffs/deluno/h-1")]
    [InlineData("https://host/weir/api/v1/intake/webhook/deluno-main/", "https://host/weir/api/v1/intake/capabilities", "https://host/weir/api/v1/intake/handoffs/deluno-main/h-1")]
    public void The_question_addresses_come_from_the_hand_off_address(string submission, string capabilities, string handoff)
    {
        Assert.True(ProcessorStatusEndpoints.TryDerive(submission, out var endpoints));
        Assert.Equal(capabilities, endpoints.Capabilities.ToString());
        Assert.Equal(handoff, endpoints.Handoff("h-1").ToString());
    }

    [Theory]
    [InlineData("https://fileflows.example.test/api/webhook/deluno")]
    [InlineData("not a url")]
    public void A_processor_of_another_shape_cannot_be_asked(string submission)
        => Assert.False(ProcessorStatusEndpoints.TryDerive(submission, out _));

    [Fact]
    public async Task Capabilities_are_read_with_the_shared_secret()
    {
        HttpRequestMessage? sent = null;
        var service = Service(request =>
        {
            sent = request;
            return Json(HttpStatusCode.OK, """{ "capabilities": ["handoff-status", "handoff-cancel"] }""");
        });

        var support = await service.GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.True(support.CanReportStatus);
        Assert.True(support.CanCancel);
        Assert.Equal("shared-secret", Assert.Single(sent!.Headers.GetValues("X-Webhook-Secret")));
    }

    [Fact]
    public async Task A_processor_without_the_endpoint_is_treated_as_it_always_was()
    {
        var support = await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unsupported, support.Answer);
        Assert.False(support.CanReportStatus);
    }

    [Fact]
    public async Task A_refused_question_is_said_as_the_setting_to_change()
    {
        var support = await Service(_ => Json(HttpStatusCode.Forbidden, """{ "detail": "A webhook secret is required." }"""))
            .GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Forbidden, support.Answer);
        Assert.Contains("Set a webhook secret in Weir", support.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_is_read_in_full()
    {
        var answer = await Service(_ => Json(HttpStatusCode.OK, """
            {
              "handoffId": "h-1",
              "state": "Scheduled",
              "queuePosition": 3,
              "scheduledFor": "2026-09-17T01:00:00Z",
              "lastChangedUtc": "2026-09-16T10:00:00Z",
              "outputPath": null,
              "message": "Outside this library's processing hours."
            }
            """)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, answer.Answer);
        Assert.Equal("scheduled", answer.State);
        Assert.Equal(3, answer.QueuePosition);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T01:00:00Z"), answer.ScheduledForUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-16T10:00:00Z"), answer.LastChangedUtc);
        Assert.Equal("Outside this library's processing hours.", answer.Message);
    }

    [Fact]
    public async Task Never_heard_of_it_and_could_not_ask_are_different_answers()
    {
        var notFound = await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);
        var down = await Service(_ => throw new HttpRequestException("connection refused")).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);
        var broken = await Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.NotFound, notFound.Answer);
        Assert.Equal(ProcessorAnswer.Unreachable, down.Answer);
        Assert.Equal(ProcessorAnswer.Unreachable, broken.Answer);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, ProcessorAnswer.Answered)]
    [InlineData(HttpStatusCode.Conflict, ProcessorAnswer.Busy)]
    [InlineData(HttpStatusCode.Forbidden, ProcessorAnswer.Forbidden)]
    public async Task Cancelling_reads_busy_as_busy(HttpStatusCode code, ProcessorAnswer expected)
    {
        HttpRequestMessage? sent = null;
        var result = await Service(request =>
        {
            sent = request;
            return new HttpResponseMessage(code);
        }).CancelHandoffAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(expected, result.Answer);
        Assert.Equal(HttpMethod.Delete, sent!.Method);
        Assert.Equal("http://weir:8788/api/v1/intake/handoffs/deluno/h-1", sent.RequestUri!.ToString());
    }

    private static ProcessorConnectionService Service(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new StubHttpClientFactory(new HttpClient(new StubHandler(send))));

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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
