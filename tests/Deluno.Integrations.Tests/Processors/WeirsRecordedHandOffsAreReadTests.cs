using System.Net;
using System.Text.Json;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #1237: every state of a hand-off that Weir 1.0.0-rc.13 was recorded in, replayed through the code that reads it. A hand-off is
/// accepted whether its source is a file, a release folder, a folder with the files named, or the same source sent again; Weir's
/// status says working or completed with the output it made (or none, when it changed nothing); and Weir's reply to Deluno's outcome
/// says whether it let go of its copy, in the words it gave. The replies are Weir's own; the requests are Deluno's.
/// </summary>
public sealed class WeirsRecordedHandOffsAreReadTests
{
    private static readonly ProcessorConnectionItem Weir = new(
        "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", "shared-secret", true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    // The two hand-offs of the recorded "source sent again": the older one and the newer one.
    private const string Older = "5f6fc694-8d63-4758-a7c3-bf3b55141ac7";
    private const string Newer = "8debfde8-02ce-4be2-919e-3bbee370cd20";

    private static ProcessorConnectionService Service(HttpMessageHandler handler)
        => new(new Factory(new HttpClient(handler)));

    private static ProcessorConnectionService Service(Func<HttpRequestMessage, HttpResponseMessage> send)
        => Service(new Handler(send));

    private static ProcessorHandoffItem Handoff(string source, IReadOnlyList<string>? sourceFiles = null) => new ProcessorHandoffItem(
        "handoff-1", "lib-1", "movies", "client-1", "queue-1", "Release.Name", source, "Weir", "waiting",
        null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) with { SourceFiles = sourceFiles };

    // ------------------------------------------------------------------ sending a hand-off

    /// <summary>Weir accepts a hand-off for each kind of source it was sent, and Deluno says it submitted it; the folder and the named files are told apart in what Deluno sends.</summary>
    [Theory]
    [InlineData("handoff-accepted", "single file")]
    [InlineData("handoff-accepted-missing-file", "single file")]
    [InlineData("handoff-release-folder-accepted", "release folder")]
    [InlineData("handoff-source-files-accepted", "folder and files")]
    [InlineData("handoff-resend-accepted", "single file")]
    public async Task Each_hand_off_weir_accepted_is_a_submitted_hand_off(string recording, string kind)
    {
        string? sent = null;
        var service = Service(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return RecordedReplies.Weir(recording).Response("webhook");
        });
        var handoff = kind switch
        {
            "release folder" => Handoff(@"C:\Downloads\Completed\Movies\Release.Name"),
            "folder and files" => Handoff(@"C:\Downloads\Completed\Movies\Release.Name", [@"C:\Downloads\Completed\Movies\Release.Name\Release.Name.mkv"]),
            _ => Handoff(@"C:\Downloads\Completed\Movies\Release.Name.mkv")
        };

        var result = await service.SubmitAsync(Weir, handoff, CancellationToken.None);

        Assert.True(result.IsAccepted, result.Message);
        Assert.Equal("submitted", result.Status);
        using var payload = JsonDocument.Parse(sent!);
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("sourcePath").GetString()));
        Assert.Equal(kind == "folder and files", payload.RootElement.TryGetProperty("sourceFiles", out _));
    }

    /// <summary>
    /// Weir answers 200 and "ignored" to a hand-off that names no source. Deluno never sends one (every hand-off names its source,
    /// asserted above), and reads any 200 as sent, so this is the reading that follows from Weir's answer.
    /// </summary>
    [Fact]
    public async Task A_200_ignored_is_read_as_sent_because_deluno_always_names_a_source()
    {
        var result = await Service(_ => RecordedReplies.Weir("handoff-ignored-no-source").Response("webhook"))
            .SubmitAsync(Weir, Handoff(@"C:\Downloads\Completed\Movies\Release.Name.mkv"), CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Equal(200, result.StatusCode);
    }

    // ------------------------------------------------------------------ asking about it

    /// <summary>The status Weir gives in each state it was recorded in, for each kind of source, with the output it made.</summary>
    [Theory]
    [InlineData("completed-with-output-path", "completed", true)]
    [InlineData("completed-without-output-path", "completed", false)]
    [InlineData("release-folder-status-completed", "completed", true)]
    [InlineData("release-folder-status-working", "working", false)]
    [InlineData("source-files-status-completed", "completed", true)]
    [InlineData("source-files-status-working", "working", false)]
    [InlineData("resend-status-completed", "completed", false)]
    [InlineData("missing-file-status-working", "working", false)]
    public async Task Each_recorded_status_is_read_with_its_state_and_the_output_weir_made(string recording, string state, bool hasOutput)
    {
        var answer = await Service(_ => RecordedReplies.Weir(recording).Response("handoffs/deluno")).GetHandoffStatusAsync(Weir, "h-1", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, answer.Answer);
        Assert.Equal(state, answer.State);
        Assert.Equal(hasOutput, answer.OutputPath is not null);
        Assert.NotNull(answer.LastChangedUtc);
        if (state == "completed")
        {
            Assert.False(string.IsNullOrWhiteSpace(answer.Message), "Weir says in words what it did");
        }
    }

    // ------------------------------------------------------------------ telling it the outcome

    /// <summary>What Weir answers to an outcome Deluno reports: whether it let go of its copy, and what it said.</summary>
    [Theory]
    [InlineData("outcome-delivered", ProcessorHandoffOutcome.Imported, false)]
    [InlineData("outcome-delivered-without-output-path", ProcessorHandoffOutcome.Imported, true)]
    [InlineData("outcome-finished-in-burst-200", ProcessorHandoffOutcome.Imported, true)]
    [InlineData("outcome-already-recorded", ProcessorHandoffOutcome.Imported, false)]
    [InlineData("outcome-imported-with-reason", ProcessorHandoffOutcome.Imported, false)]
    [InlineData("outcome-not-imported-no-reason", ProcessorHandoffOutcome.NotImported, false)]
    [InlineData("outcome-not-imported-default-reason", ProcessorHandoffOutcome.NotImported, false)]
    [InlineData("outcome-not-imported-import-refused", ProcessorHandoffOutcome.NotImported, false)]
    public async Task Each_recorded_outcome_answer_says_whether_weir_let_go_of_its_copy(string recording, ProcessorHandoffOutcome outcome, bool released)
    {
        var told = await Service(_ => RecordedReplies.Weir(recording).Response("outcome"))
            .ReportOutcomeAsync(Weir, "h-1", outcome, outcome == ProcessorHandoffOutcome.Imported ? @"D:\Media\Movies\Release.Name.mkv" : null,
                outcome == ProcessorHandoffOutcome.NotImported ? "Deluno will not import this file." : null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, told.Answer);
        Assert.Equal(released, told.Released);
        Assert.False(string.IsNullOrWhiteSpace(told.Message), "Weir's own words are kept");
    }

    /// <summary>A reason for not importing goes to Weir as the reason, in what Deluno sends; an import that carries a note goes as the note; neither is sent when there is none.</summary>
    [Theory]
    [InlineData(ProcessorHandoffOutcome.NotImported, null)]
    [InlineData(ProcessorHandoffOutcome.NotImported, "The import was refused: the file is not in a library folder.")]
    [InlineData(ProcessorHandoffOutcome.Imported, "Left 2 small files in the download.")]
    public async Task The_reason_deluno_gives_is_what_weir_is_sent(ProcessorHandoffOutcome outcome, string? reason)
    {
        string? sent = null;
        var recording = outcome == ProcessorHandoffOutcome.Imported ? "outcome-imported-with-reason" : reason is null ? "outcome-not-imported-no-reason" : "outcome-not-imported-import-refused";
        await Service(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return RecordedReplies.Weir(recording).Response("outcome");
        }).ReportOutcomeAsync(Weir, "h-1", outcome, outcome == ProcessorHandoffOutcome.Imported ? @"D:\Media\Movies\Release.Name.mkv" : null, reason, DateTimeOffset.UnixEpoch, CancellationToken.None);

        using var body = JsonDocument.Parse(sent!);
        Assert.Equal(outcome == ProcessorHandoffOutcome.Imported ? "imported" : "not-imported", body.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(reason, body.RootElement.TryGetProperty("reason", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null);
    }

    /// <summary>
    /// The same source sent again while the first send was still in Weir gets a new hand-off. Deluno's outcome for the older hand-off is
    /// answered like any other, and the newer one's status and outcome after it are too: replayed in the order Weir answered them.
    /// </summary>
    [Fact]
    public async Task A_source_sent_again_leaves_the_older_outcome_answered_and_the_newer_hand_off_readable()
    {
        var service = Service(RecordedReplies.Weir("outcome-superseded").Sequence());

        var second = await service.SubmitAsync(Weir, Handoff(@"C:\Downloads\Completed\Movies\Release.Name.mkv"), CancellationToken.None);
        var olderStatus = await service.GetHandoffStatusAsync(Weir, Older, CancellationToken.None);
        var olderOutcome = await service.ReportOutcomeAsync(Weir, Older, ProcessorHandoffOutcome.Imported, null, null, DateTimeOffset.UnixEpoch, CancellationToken.None);
        var newerStatus = await service.GetHandoffStatusAsync(Weir, Newer, CancellationToken.None);

        Assert.True(second.IsAccepted);
        Assert.Equal("completed", olderStatus.State);
        Assert.Equal(ProcessorAnswer.Answered, olderOutcome.Answer);
        Assert.Equal("completed", newerStatus.State);
        Assert.NotNull(newerStatus.OutputPath);
    }

    [Fact]
    public async Task The_newer_hand_off_of_a_source_sent_again_is_told_its_outcome_after_the_older_one()
    {
        var service = Service(RecordedReplies.Weir("outcome-superseded-newer").Sequence());

        var status = await service.GetHandoffStatusAsync(Weir, Newer, CancellationToken.None);
        var outcome = await service.ReportOutcomeAsync(Weir, Newer, ProcessorHandoffOutcome.Imported, null, null, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("completed", status.State);
        Assert.Equal(ProcessorAnswer.Answered, outcome.Answer);
        Assert.False(outcome.Released);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
