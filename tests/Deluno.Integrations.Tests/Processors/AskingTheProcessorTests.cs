using System.Net;
using System.Text;
using Deluno.Integrations.Processors;
using Deluno.Tests.Recorded;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #511: the questions Deluno asks a processor about a hand-off, and how each
/// answer is read. Shaped by Weir #480. Every answer of Weir is its recorded one (Weir 1.0.0-rc.13, #1237); an answer a status code alone
/// makes (a 500, a 403, a refused connection) has no body at all, and a body that is not JSON is the one deliberate malformed reply.
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
            return Recorded("capabilities", "capabilities");
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
        // Weir's recorded folder list, with its second library one Weir keeps for itself: no manager key, its own folders.
        var libraryFolders = RecordedReplies.Weir("library-folders").Body(
            "library-folders", ("libraries.1.manager_library_key", null), ("libraries.1.folders_from_manager", false));
        var service = Service(request =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/capabilities")
                ? Recorded("capabilities", "capabilities")
                : Json(HttpStatusCode.OK, libraryFolders);
        });

        var support = await service.GetStatusSupportAsync(Weir, CancellationToken.None);
        var folders = await service.GetLibraryFoldersAsync(Weir, CancellationToken.None);

        Assert.True(support.HasLibraryFolders);
        Assert.Contains("/api/v1/intake/library-folders", requested);
        Assert.Equal(2, folders.Count);
        Assert.Equal(new ProcessorLibraryFolder((string)RecordedReplies.Weir("library-folders").Single.Reply.Json()["libraries"]![0]!["manager_library_key"]!, @"C:\Downloads\Completed\Movies", true, @"C:\Weir\Ready\Movies", 52428800), folders[0]);
        Assert.Null(folders[1].ManagerLibraryKey);
    }

    [Fact]
    public async Task A_processor_names_itself_in_its_capabilities_answer_or_not_at_all()
    {
        Assert.Equal("Weir", await Service(_ => Recorded("capabilities", "capabilities"))
            .GetProductNameAsync(Weir.SubmissionUrl, "X-Webhook-Secret", "s", CancellationToken.None));
        Assert.Null(await Service(_ => Json(HttpStatusCode.OK, RecordedReplies.Weir("capabilities").Body("capabilities", ("product", null))))
            .GetProductNameAsync(Weir.SubmissionUrl, "X-Webhook-Secret", "s", CancellationToken.None));
    }

    [Fact]
    public async Task A_processor_without_the_endpoint_is_treated_as_it_always_was()
    {
        var support = await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unsupported, support.Answer);
        Assert.False(support.CanReportStatus);
    }

    /// <summary>
    /// A processor that answers 403 (Weir 1.0.0-rc.13 answers 401 for a missing and for a wrong secret, so a 403 is another processor's
    /// word; it has no body here). The message names the setting to change, once, without the processor repeating itself in brackets.
    /// </summary>
    [Fact]
    public async Task A_refused_question_is_said_as_the_setting_to_change()
    {
        var support = await Service(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))
            .GetStatusSupportAsync(Weir, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Forbidden, support.Answer);
        Assert.Equal("Set a webhook secret in Weir and the same secret on this processor connection in Deluno, so Deluno can ask about hand-offs.", support.Message);
    }

    /// <summary>
    /// Weir answers 401 for a wrong or missing secret ("Invalid or missing X-Webhook-Secret header.", recorded). Read as "not
    /// supported" it would have quietly fallen back to judging the processor without asking it.
    /// </summary>
    [Theory]
    [InlineData("capabilities-wrong-secret")]
    [InlineData("capabilities-no-secret")]
    public async Task A_refused_secret_is_said_as_the_secret_to_fix_not_as_unsupported(string recording)
    {
        var support = await Service(_ => Recorded(recording, "capabilities")).GetStatusSupportAsync(Weir, CancellationToken.None);
        // The status question is refused with the same words; Weir checks the secret the same way on every question.
        var status = await Service(_ => Recorded(recording, "capabilities")).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Forbidden, support.Answer);
        Assert.Contains("refused the webhook secret", support.Message, StringComparison.Ordinal);
        Assert.Equal(ProcessorAnswer.Forbidden, status.Answer);
    }

    [Fact]
    public async Task A_status_is_read_in_full()
    {
        // Weir's recorded status of a hand-off waiting its turn, made a scheduled one: Weir's "scheduled" state (a library with
        // processing hours) was not recorded, so this is the nearest recorded reply with the values of a scheduled hand-off set.
        var queued = RecordedReplies.Weir("status-queued").Body(
            "handoffs/deluno",
            ("state", "scheduled"), ("queuePosition", 3), ("scheduledFor", "2026-09-17T01:00:00Z"), ("lastChangedUtc", "2026-09-16T10:00:00Z"),
            ("message", "Outside this library's processing hours."));
        var answer = await Service(_ => Json(HttpStatusCode.OK, queued)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, answer.Answer);
        Assert.Equal("scheduled", answer.State);
        Assert.Equal(3, answer.QueuePosition);
        Assert.Equal(DateTimeOffset.Parse("2026-09-17T01:00:00Z"), answer.ScheduledForUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-16T10:00:00Z"), answer.LastChangedUtc);
        Assert.Equal("Outside this library's processing hours.", answer.Message);
    }

    /// <summary>Every state Weir was recorded in is read as its own state, with the place in the line, the output and the words Weir gave.</summary>
    [Theory]
    [InlineData("status-queued", "queued", 8)]
    [InlineData("status-working", "working", null)]
    [InlineData("status-cancelled", "cancelled", null)]
    [InlineData("status-failed", "failed", null)]
    [InlineData("status-completed", "completed", null)]
    public async Task Each_recorded_state_is_read_as_that_state(string recording, string state, int? queuePosition)
    {
        var answer = await Service(_ => Recorded(recording, "handoffs/deluno")).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, answer.Answer);
        Assert.Equal(state, answer.State);
        Assert.Equal(queuePosition, answer.QueuePosition);
        Assert.False(string.IsNullOrWhiteSpace(answer.Message) && state is "failed" or "cancelled" or "working", "Weir's own words are kept");
    }

    [Fact]
    public async Task Never_heard_of_it_and_could_not_ask_are_different_answers()
    {
        var notFound = await Service(_ => Recorded("no-record", "handoffs/deluno/", 0)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);
        var down = await Service(_ => throw new HttpRequestException("connection refused")).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);
        var broken = await Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.NotFound, notFound.Answer);
        Assert.Equal(ProcessorAnswer.Unreachable, down.Answer);
        Assert.Equal(ProcessorAnswer.Unreachable, broken.Answer);
    }

    /// <summary>
    /// A cancel Weir takes (204), and the ones it turns down because it has begun the hand-off or finished it (409); a processor that
    /// answers 403 has no body here. Deluno names the hand-off it cancels in the address.
    /// </summary>
    [Theory]
    [InlineData("handoff-cancelled", ProcessorAnswer.Answered)]
    [InlineData("handoff-cancelled-in-burst-204", ProcessorAnswer.Answered)]
    [InlineData("handoff-cancel-working", ProcessorAnswer.Busy)]
    [InlineData("handoff-cancel-too-late", ProcessorAnswer.Busy)]
    [InlineData("handoff-cancel-refused", ProcessorAnswer.Busy)]
    [InlineData("none-403", ProcessorAnswer.Forbidden)]
    public async Task Cancelling_reads_busy_as_busy(string recording, ProcessorAnswer expected)
    {
        HttpRequestMessage? sent = null;
        var result = await Service(request =>
        {
            sent = request;
            return recording == "none-403" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Recorded(recording, "handoffs/deluno");
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
    /// Which outcome answers are worth sending again is plain HTTP, never one processor's wording (#580). The answers Weir recorded are
    /// its own; a status it was not recorded to send has no body.
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
        var answer = code switch
        {
            HttpStatusCode.OK => Recorded("outcome-delivered", "outcome"),
            HttpStatusCode.NotFound => Recorded("no-record", "outcome"),
            HttpStatusCode.Conflict => Recorded("outcome-handoff-not-finished-working", "outcome"),
            HttpStatusCode.Unauthorized => Recorded("handoff-refused-wrong-secret", "webhook"),
            _ => new HttpResponseMessage(code)
        };
        var told = await Service(_ => answer)
            .ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(expected, told.Answer);
    }

    /// <summary>
    /// A 409 whose code says the processor keeps an answer it already has is final (#1155); any other 409 stays
    /// owed. Read from the code, not the words. Each 409 Weir recorded, and the same words without a code.
    /// </summary>
    [Theory]
    [InlineData("outcome-outcome-already-recorded", ProcessorAnswer.Settled)]
    [InlineData("outcome-handoff-not-finished-queued", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-not-finished-working", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-ended", ProcessorAnswer.Refused)]
    [InlineData("outcome-handoff-ended-after-failed", ProcessorAnswer.Refused)]
    [InlineData("outcome-outcome-already-recorded:no-code", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-ended:no-code", ProcessorAnswer.Busy)]
    public async Task A_409_is_final_only_when_its_code_says_so(string recording, ProcessorAnswer expected)
    {
        var state = recording.Split(':')[0];
        var body = recording.EndsWith(":no-code", StringComparison.Ordinal)
            ? RecordedReplies.Weir(state).Body("outcome", ("code", null))
            : RecordedReplies.Weir(state).Body("outcome");
        var told = await Service(_ => Json(HttpStatusCode.Conflict, body))
            .ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(expected, told.Answer);
        if (expected == ProcessorAnswer.Settled)
        {
            Assert.Equal("Deluno already said it imported this file, so Weir kept that answer.", told.Message);
        }
    }

    /// <summary>The one deliberate malformed reply: a 409 whose body is not JSON at all stays owed.</summary>
    [Fact]
    public async Task A_409_whose_body_is_not_json_stays_owed()
    {
        var told = await Service(_ => Json(HttpStatusCode.Conflict, "not json"))
            .ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Busy, told.Answer);
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
            return Recorded("outcome-delivered", "outcome");
        }).ReportOutcomeAsync(Weir, "h-1", ProcessorHandoffOutcome.Imported, "x", null, occurred, CancellationToken.None);

        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal(occurred, sent.RootElement.GetProperty("occurredUtc").GetDateTimeOffset());
    }

    /// <summary>One of Weir 1.0.0-rc.13's recorded replies as an HTTP response: the exchange of the recording whose request mentions the text (the nth such one).</summary>
    private static HttpResponseMessage Recorded(string recording, string requestContains, int nth = 0)
        => RecordedReplies.Weir(recording).Exchanges
            .Where(e => e.Request.Path.Contains(requestContains, StringComparison.OrdinalIgnoreCase))
            .ElementAt(nth).Reply.ToResponse();

    private static ProcessorConnectionService Service(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new StubHttpClientFactory(new HttpClient(new StubHandler(send))));

    /// <summary>A body given as text, for a recorded reply changed in one value, or the one deliberate malformed reply.</summary>
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
