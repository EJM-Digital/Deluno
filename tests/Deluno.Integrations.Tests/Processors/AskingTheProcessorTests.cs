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
    public async Task A_processor_with_a_folder_per_library_lists_them_and_says_it_does()
    {
        var requested = new List<string>();
        var service = Service(request =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/capabilities")
                ? Json(HttpStatusCode.OK, """{ "capabilities": ["handoff-status", "library-folders"] }""")
                : Json(HttpStatusCode.OK, """[{"watched_folder":"/data/films","manager_library_key":"lib-1","folders_from_manager":true},{"watched_folder":"/data/other","manager_library_key":null,"folders_from_manager":false},{"manager_library_key":"lib-3"}]""");
        });

        var support = await service.GetStatusSupportAsync(Weir, CancellationToken.None);
        var folders = await service.GetLibraryFoldersAsync(Weir, CancellationToken.None);

        Assert.True(support.HasLibraryFolders);
        Assert.Contains("/api/v1/intake/library-folders", requested);
        Assert.Equal(2, folders.Count);
        Assert.Equal(new ProcessorLibraryFolder("lib-1", "/data/films", true), folders[0]);
        Assert.Null(folders[1].ManagerLibraryKey);
    }

    [Fact]
    public async Task A_processor_names_itself_in_its_capabilities_answer_or_not_at_all()
    {
        Assert.Equal("Weir", await Service(_ => Json(HttpStatusCode.OK, """{ "name": " Weir ", "version": "3.2.0" }"""))
            .GetProductNameAsync(Weir.SubmissionUrl, "X-Webhook-Secret", "s", CancellationToken.None));
        Assert.Null(await Service(_ => Json(HttpStatusCode.OK, """{ "capabilities": [] }"""))
            .GetProductNameAsync(Weir.SubmissionUrl, "X-Webhook-Secret", "s", CancellationToken.None));
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
        // Said once, without the processor repeating it in brackets.
        Assert.Equal("Set a webhook secret in Weir and the same secret on this processor connection in Deluno, so Deluno can ask about hand-offs.", support.Message);
    }

    /// <summary>
    /// Weir answers 401 for a wrong or missing secret. Read as "not
    /// supported" it would have quietly fallen back to judging the processor
    /// without asking it.
    /// </summary>
    [Fact]
    public async Task A_refused_secret_is_said_as_the_secret_to_fix_not_as_unsupported()
    {
        var support = await Service(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)).GetStatusSupportAsync(Weir, CancellationToken.None);
        var status = await Service(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Forbidden, support.Answer);
        Assert.Contains("refused the webhook secret", support.Message, StringComparison.Ordinal);
        Assert.Equal(ProcessorAnswer.Forbidden, status.Answer);
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

    /// <summary>
    /// A processor that is starting up or failing has not said what it
    /// supports. Read as "supports nothing" its files were judged as if it
    /// could not be asked, health read healthy, and outcomes were dropped (#580).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failing_capabilities_answer_is_could_not_ask_not_supports_nothing(HttpStatusCode code)
    {
        var support = await Service(_ => new HttpResponseMessage(code)).GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, support.Answer);
    }

    /// <summary>
    /// Which outcome answers are worth sending again is plain HTTP, never one
    /// processor's wording (#580).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, ProcessorAnswer.Answered)]
    [InlineData(HttpStatusCode.NotFound, ProcessorAnswer.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProcessorAnswer.Refused)]
    [InlineData(HttpStatusCode.BadRequest, ProcessorAnswer.Refused)]
    [InlineData(HttpStatusCode.Conflict, ProcessorAnswer.Busy)]
    [InlineData(HttpStatusCode.Unauthorized, ProcessorAnswer.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, ProcessorAnswer.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, ProcessorAnswer.Unreachable)]
    [InlineData(HttpStatusCode.BadGateway, ProcessorAnswer.Unreachable)]
    [InlineData(HttpStatusCode.RequestTimeout, ProcessorAnswer.Unreachable)]
    public async Task An_outcome_answer_is_read_by_its_status_code(HttpStatusCode code, ProcessorAnswer expected)
    {
        var told = await Service(_ => Json(code, """{ "released": false, "detail": "said" }"""))
            .ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(expected, told.Answer);
    }

    /// <summary>
    /// A 409 whose code says the processor keeps an answer it already has is final (#1155); any other 409 stays
    /// owed. Read from the code, not the words.
    /// </summary>
    [Theory]
    [InlineData("{ \"detail\": \"Deluno already said it will not import this file, so Weir kept that answer.\", \"code\": \"outcome_already_recorded\" }", ProcessorAnswer.Settled)]
    [InlineData("{ \"detail\": \"Weir has not finished this hand-off yet.\", \"code\": \"handoff_not_finished\" }", ProcessorAnswer.Busy)]
    [InlineData("{ \"detail\": \"This hand-off ended failed, so Weir handed back no file to import.\", \"code\": \"handoff_ended\" }", ProcessorAnswer.Refused)]
    [InlineData("{ \"detail\": \"Deluno already said it will not import this file, so Weir kept that answer.\" }", ProcessorAnswer.Busy)]
    [InlineData("not json", ProcessorAnswer.Busy)]
    public async Task A_409_is_final_only_when_its_code_says_so(string body, ProcessorAnswer expected)
    {
        var told = await Service(_ => Json(HttpStatusCode.Conflict, body))
            .ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(expected, told.Answer);
        if (expected == ProcessorAnswer.Settled)
        {
            Assert.Equal("Deluno already said it will not import this file, so Weir kept that answer.", told.Message);
        }
    }

    /// <summary>
    /// The same report sent again must be the same report: it carries when the
    /// import happened, not when it is being said (#580).
    /// </summary>
    [Fact]
    public async Task An_outcome_says_when_it_happened_not_when_it_was_sent()
    {
        string? body = null;
        var occurred = DateTimeOffset.Parse("2026-09-23T07:46:41Z");
        await Service(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{ "released": true }""");
        }).ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, occurred, CancellationToken.None);

        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal(occurred, sent.RootElement.GetProperty("occurredUtc").GetDateTimeOffset());
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
