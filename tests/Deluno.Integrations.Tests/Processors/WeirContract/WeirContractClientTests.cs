using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;
using Moq;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1239: every reply Weir recorded (tests/fixtures/recorded/weir), replayed through the real client code that talks to
/// Weir, and every request Deluno sends held against the recorded request. docs/deluno-weir-contract.md lists the same
/// rows. The guard (EveryWeirRecordingIsUsedByAContractTestTests) fails on a recording no test names.
/// </summary>
public sealed class WeirContractClientTests
{
    // ----- capabilities -----

    [Fact]
    public async Task Capabilities_with_the_secret_say_what_Weir_can_be_asked_and_told()
    {
        var replay = ReplayOf("capabilities");
        var service = Service(replay);

        var support = await service.GetStatusSupportAsync(Connection, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, support.Answer);
        Assert.True(support.CanReportStatus);
        Assert.True(support.CanCancel);
        Assert.True(support.CanBeToldTheOutcome);
        Assert.True(support.HasLibraryFolders);
        Assert.Null(support.WatchedPath);
        var asked = Assert.Single(replay.Sent);
        Assert.Equal("GET", asked.Method);
        Assert.Equal("/api/v1/intake/capabilities", asked.Path);
        Assert.Equal(Secret, asked.Headers["X-Webhook-Secret"]);
    }

    [Fact]
    public async Task Capabilities_name_Weir_its_version_and_its_machine()
    {
        var recorded = Body(Rec("capabilities").Single.Reply.Body);
        var service = Service(ReplayOf("capabilities"));

        Assert.Equal("Weir", await service.GetProductNameAsync(Connection.SubmissionUrl, Connection.AuthHeaderName, Secret, CancellationToken.None));
        Assert.Equal(recorded["version"]!.GetValue<string>(), await service.ReadVersionAsync(Connection, CancellationToken.None));
        Assert.Equal(recorded["machine_name"]!.GetValue<string>(), await service.GetMachineNameAsync(Connection.SubmissionUrl, Connection.AuthHeaderName, Secret, CancellationToken.None));
    }

    [Theory]
    [InlineData("capabilities-no-secret")]
    [InlineData("capabilities-wrong-secret")]
    public async Task A_refused_secret_on_the_capabilities_call_is_said_as_that_and_nothing_is_assumed(string state)
    {
        var support = await Service(ReplayOf(state)).GetStatusSupportAsync(Connection, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Forbidden, support.Answer);
        Assert.False(support.CanReportStatus);
        Assert.False(support.CanCancel);
        Assert.False(support.CanBeToldTheOutcome);
        Assert.Contains("refused the webhook secret", support.Message);
    }

    /// <summary>
    /// When the capabilities answer is a refusal, the version is read from the description of its API that Weir publishes without a secret (the recording
    /// openapi: the document's identity, shortened from 360 KB), and the capabilities are asked first.
    /// </summary>
    [Fact]
    public async Task When_the_capabilities_cannot_be_had_the_version_is_read_from_the_API_description_Weir_publishes()
    {
        var replay = ReplayOf("capabilities-wrong-secret", "openapi");

        var version = await Service(replay).ReadVersionAsync(Connection, CancellationToken.None);

        Assert.Equal(Body(Rec("openapi").Single.Reply.Body)["info"]!["version"]!.GetValue<string>(), version);
        Assert.Equal(["/api/v1/intake/capabilities", "/openapi.json"], replay.Sent.Select(sent => sent.Path).ToArray());
    }

    // ----- library folders -----

    [Fact]
    public async Task Library_folders_list_each_library_with_its_folders_and_minimum_size()
    {
        var replay = ReplayOf("library-folders");

        var folders = await Service(replay).GetLibraryFoldersAsync(Connection, CancellationToken.None);

        Assert.Equal("/api/v1/intake/library-folders", Assert.Single(replay.Sent).Path);
        Assert.Equal(2, folders.Count);
        var recorded = Body(Rec("library-folders").Single.Reply.Body)["libraries"]!.AsArray();
        for (var i = 0; i < folders.Count; i++)
        {
            Assert.Equal(recorded[i]!["manager_library_key"]!.GetValue<string>(), folders[i].ManagerLibraryKey);
            Assert.Equal(recorded[i]!["watched_folder"]!.GetValue<string>(), folders[i].WatchedFolder);
            Assert.Equal(recorded[i]!["output_folder"]!.GetValue<string>(), folders[i].OutputFolder);
            Assert.True(folders[i].FoldersFromManager);
            Assert.Equal(52428800L, folders[i].MinimumFileSizeBytes);
        }
    }

    // ----- hand-off submit: Weir's replies -----

    [Theory]
    [InlineData("handoff-accepted")]
    [InlineData("handoff-accepted-missing-file")]
    [InlineData("handoff-release-folder-accepted")]
    [InlineData("handoff-source-files-accepted")]
    [InlineData("handoff-resend-accepted")]
    [InlineData("resend-takeover-accepted")]
    // Weir 1.0.0-rc.18: a film that stops before its end, sent, and sent again while Weir holds the file because it cannot read it to the end
    [InlineData("handoff-accepted-cut-off-file")]
    [InlineData("handoff-accepted-again-while-on-hold")]
    public async Task An_accepted_hand_off_is_recorded_as_submitted(string state)
    {
        var recording = Rec(state);
        var result = await SubmitRecordedAsync(recording, new WeirReplay(recording));

        Assert.True(result.IsAccepted);
        Assert.Equal("submitted", result.Status);
        Assert.Equal(200, result.StatusCode);
    }

    /// <summary>
    /// Weir answers 200 with status "ignored" for a body with no source path. Deluno reads any 2xx as sent, so this
    /// would be recorded as submitted; it never happens because every hand-off Deluno sends names a source path (the
    /// request tests below pin that).
    /// </summary>
    [Fact]
    public async Task A_2xx_that_says_ignored_is_read_as_sent_because_Deluno_always_names_a_source_path()
    {
        var recording = Rec("handoff-ignored-no-source");
        var replay = new WeirReplay(recording);
        var handoff = Handoff("ignored-1", "library-1", "movie", "/downloads/y.mkv", "Ignored");
        var result = await Service(replay).SubmitAsync(Connection, handoff, CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Equal("ignored", Body(recording.Single.Reply.Body)["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(Body(replay.Sent[0].Body)["sourcePath"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_folder_no_workflow_watches_is_refused_with_Weirs_own_words()
    {
        var recording = Rec("handoff-refused-not-watched");
        var result = await SubmitRecordedAsync(recording, new WeirReplay(recording));

        Assert.False(result.IsAccepted);
        Assert.Equal("failed", result.Status);
        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("The processor refused the hand-off with HTTP 400: No Weir workflow watches the folder", result.Message);
    }

    [Fact]
    public async Task A_wrong_secret_on_a_hand_off_is_refused_with_Weirs_own_words()
    {
        var recording = Rec("handoff-refused-wrong-secret");
        var result = await SubmitRecordedAsync(recording, new WeirReplay(recording));

        Assert.False(result.IsAccepted);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("The processor refused the hand-off with HTTP 401: Invalid or missing X-Webhook-Secret header.", result.Message);
    }

    [Fact]
    public async Task A_hand_off_with_Weir_switched_off_is_not_sent_and_says_to_check_the_connection()
    {
        var recording = Rec("handoff-accepted");
        var replay = new WeirReplay(recording) { Down = true };

        var result = await SubmitRecordedAsync(recording, replay);

        Assert.False(result.IsAccepted);
        Assert.Null(result.StatusCode);
        Assert.Equal("Deluno could not submit the hand-off. Check the processor connection before retrying.", result.Message);
    }

    // ----- hand-off status -----

    /// <summary>Every status Weir was recorded giving, read the same way: the state, the place in line, the output and Weir's words.</summary>
    [Theory]
    [InlineData("status-queued", 0)]
    [InlineData("status-queued", 1)]
    [InlineData("status-working", 0)]
    [InlineData("status-completed", 0)]
    [InlineData("status-failed", 0)]
    [InlineData("status-cancelled", 0)]
    [InlineData("completed-with-output-path", 0)]
    [InlineData("completed-without-output-path", 0)]
    [InlineData("resend-status-completed", 0)]
    [InlineData("missing-file-status-working", 0)]
    [InlineData("release-folder-status-working", 0)]
    [InlineData("release-folder-status-completed", 0)]
    [InlineData("source-files-status-working", 0)]
    [InlineData("source-files-status-completed", 0)]
    [InlineData("handoff-cancelled", 1)]
    [InlineData("handoff-cancel-working", 1)]
    [InlineData("handoff-cancel-too-late", 1)]
    [InlineData("handoff-cancel-refused", 1)]
    [InlineData("outcome-superseded", 1)]
    [InlineData("outcome-superseded-newer", 0)]
    // Weir 1.0.0-rc.18, recorded for #1239 and #1262 (scripts/recorded-replies/Record-WeirReports.ps1)
    [InlineData("source-files-status-queued", 0)]
    [InlineData("resend-status-working", 0)]
    [InlineData("completed-after-resend", 0)]
    [InlineData("completed-after-resend", 1)]
    [InlineData("resend-takeover-first-status", 0)]
    [InlineData("resend-takeover-second-status-working", 0)]
    [InlineData("resend-takeover-second-status-completed", 0)]
    [InlineData("resend-takeover-second-final-status", 0)]
    [InlineData("output-collision-replace-first-status", 0)]
    [InlineData("output-collision-replace-second-status", 0)]
    [InlineData("output-collision-keep-both-first-status", 0)]
    [InlineData("output-collision-keep-both-second-status", 0)]
    [InlineData("completed-season-pack-status-working", 0)]
    [InlineData("completed-season-pack-status-completed", 0)]
    [InlineData("failed-partial-season-pack-corrupt-status-queued", 0)]
    [InlineData("failed-partial-season-pack-corrupt-status-working", 0)]
    [InlineData("failed-rejected-status-queued", 0)]
    [InlineData("failed-rejected-status-rejected", 0)]
    // Weir 1.0.0-rc.18, recorded in Docker (scripts/recorded-replies/docker/flows.mjs): a send Weir did not take because another connection's
    // pass owns the file, read with the second connection's secret
    [InlineData("status-not-taken", 0)]
    public async Task A_status_is_read_as_Weir_said_it(string state, int exchange)
    {
        var recording = Rec(state);
        var recorded = recording.Exchanges[exchange];
        var id = HandoffIdOf(recording, exchange);
        var replay = new WeirReplay(recording);

        var status = await Service(replay).GetHandoffStatusAsync(Connection, id, CancellationToken.None);

        var said = Body(recorded.Reply.Body);
        Assert.Equal(ProcessorAnswer.Answered, status.Answer);
        Assert.Equal(said["state"]!.GetValue<string>(), status.State);
        Assert.Equal(said["queuePosition"]?.GetValue<int>(), status.QueuePosition);
        Assert.Equal(said["outputPath"]?.GetValue<string>(), status.OutputPath);
        Assert.Equal(said["message"]?.GetValue<string>(), status.Message);
        Assert.Equal(DateTimeOffset.Parse(said["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture), status.LastChangedUtc);
        Assert.Null(status.ScheduledForUtc);
        var asked = replay.Sent.Last(s => s.Method == "GET");
        Assert.Equal(recorded.Request.Path, asked.Path);
        Assert.Equal(Secret, asked.Headers["X-Webhook-Secret"]);
        Assert.Null(asked.Body);
    }

    /// <summary>
    /// Weir's "scheduled" state, as Weir 1.0.0-rc.18 really gives it (status-scheduled-file-on-hold): a second hand-off for a file Weir holds because it
    /// could not read it to the end is not worked at once but scheduled for the time the hold ends, with no place in line and Weir's words for why.
    /// </summary>
    [Fact]
    public async Task A_hand_off_Weir_schedules_is_read_with_the_time_it_will_next_be_looked_at_and_Weirs_words()
    {
        var recording = Rec("status-scheduled-file-on-hold");
        var said = Body(recording.Exchanges[0].Reply.Body);

        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, HandoffIdOf(recording), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, status.Answer);
        Assert.Equal("scheduled", status.State);
        Assert.Null(status.QueuePosition);
        Assert.Equal(DateTimeOffset.Parse(said["scheduledFor"]!.GetValue<string>(), CultureInfo.InvariantCulture), status.ScheduledForUtc);
        Assert.Equal(DateTimeOffset.Parse(said["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture), status.LastChangedUtc);
        Assert.Contains("could not read this media file from start to finish", status.Message);
    }

    /// <summary>
    /// With no Deluno connection in Weir that has a secret, a hand-off sent to Deluno's source is refused 401 with Weir's own instruction (the
    /// status poll, for the same cause, is 403). Deluno shows Weir's words and does not take the refusal for a wrong secret.
    /// </summary>
    [Fact]
    public async Task A_hand_off_to_a_source_with_no_connection_that_has_a_secret_is_refused_with_Weirs_instruction()
    {
        var recording = Rec("handoff-refused-deluno-source-no-secret");
        var result = await SubmitRecordedAsync(recording, new WeirReplay(recording));

        Assert.False(result.IsAccepted);
        Assert.Equal(401, result.StatusCode);
        Assert.StartsWith("The processor refused the hand-off with HTTP 401: No Deluno is set up in Weir.", result.Message);
    }

    [Fact]
    public async Task A_queued_hand_off_says_its_place_in_line_and_a_completed_one_with_no_output_path_says_none()
    {
        var queued = await Service(ReplayOf("status-queued")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("status-queued")), CancellationToken.None);
        var resent = await Service(ReplayOf("completed-without-output-path")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("completed-without-output-path")), CancellationToken.None);
        var finished = await Service(ReplayOf("completed-with-output-path")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("completed-with-output-path")), CancellationToken.None);

        Assert.Equal((ProcessorHandoffStates.Queued, 8), (queued.State, queued.QueuePosition));
        Assert.Equal(ProcessorHandoffStates.Completed, resent.State);
        Assert.Null(resent.OutputPath);
        Assert.Equal(ProcessorHandoffStates.Completed, finished.State);
        Assert.False(string.IsNullOrWhiteSpace(finished.OutputPath));
    }

    /// <summary>
    /// Weir rc.18's own answers for a file sent again while Weir held the first send (#1262), recorded on the golden VM:
    /// the first send ends cancelled with Weir's words, the second finishes WITH its output path (rc.13 answered a resend's
    /// finished status without one), and a different file sent under a name already in the output is told apart by the
    /// collision policy: "replace" writes the same output name, "keep_both" numbers the second beside the first.
    /// </summary>
    [Fact]
    public async Task A_file_sent_again_and_a_name_that_is_taken_are_read_as_Weir_recorded_them()
    {
        async Task<ProcessorHandoffStatusResult> Read(string state, int exchange = 0)
        {
            var recording = Rec(state);
            return await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, HandoffIdOf(recording, exchange), CancellationToken.None);
        }

        var first = await Read("resend-takeover-first-status");
        var second = await Read("resend-takeover-second-final-status");
        Assert.Equal(ProcessorHandoffStates.Cancelled, first.State);
        Assert.Null(first.OutputPath);
        Assert.Contains("sent again", first.Message);
        Assert.Equal(ProcessorHandoffStates.Completed, second.State);
        Assert.EndsWith("Recorded.Weir.Takeover.mkv", second.OutputPath);

        var replaceFirst = await Read("output-collision-replace-first-status");
        var replaceSecond = await Read("output-collision-replace-second-status");
        Assert.Equal(replaceFirst.OutputPath, replaceSecond.OutputPath);

        var keepFirst = await Read("output-collision-keep-both-first-status");
        var keepSecond = await Read("output-collision-keep-both-second-status");
        Assert.NotEqual(keepFirst.OutputPath, keepSecond.OutputPath);
        Assert.EndsWith(" (2).mkv", keepSecond.OutputPath);
        Assert.Equal(keepFirst.OutputPath!.Replace(".mkv", " (2).mkv"), keepSecond.OutputPath);
    }

    [Fact]
    public async Task A_status_that_is_rejected_is_a_state_Deluno_reads_by_its_word_and_keeps_Weirs_message()
    {
        var recording = Rec("failed-rejected-status-rejected");
        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, HandoffIdOf(recording), CancellationToken.None);

        Assert.Equal(ProcessorHandoffStates.Rejected, status.State);
        Assert.Null(status.OutputPath);
        Assert.Contains("rejected", status.Message);
    }

    [Fact]
    public async Task A_hand_off_Weir_never_received_is_not_found()
    {
        var recording = Rec("no-record");
        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, HandoffIdOf(recording), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.NotFound, status.Answer);
        Assert.Equal("Weir has no record of this hand-off.", status.Message);
    }

    [Fact]
    public async Task A_status_asked_while_Weir_is_switched_off_is_unreachable_not_an_answer()
    {
        var replay = ReplayOf("status-working");
        replay.Down = true;
        var status = await Service(replay).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("status-working")), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, status.Answer);
        Assert.Equal("Deluno could not reach Weir.", status.Message);
    }

    // ----- cancel -----

    [Theory]
    [InlineData("handoff-cancelled")]
    [InlineData("handoff-cancelled-in-burst-204")]
    public async Task A_cancel_Weir_accepts_with_204_is_answered(string state)
    {
        var recording = Rec(state);
        var replay = new WeirReplay(recording);

        var result = await Service(replay).CancelHandoffAsync(Connection, HandoffIdOf(recording), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, result.Answer);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal(recording.Exchanges[0].Request.Path, sent.Path);
        Assert.Equal(Secret, sent.Headers["X-Webhook-Secret"]);
    }

    [Theory]
    [InlineData("handoff-cancel-working", "This hand-off is working, so Weir did not cancel it.")]
    [InlineData("handoff-cancel-too-late", "This hand-off is completed, so Weir did not cancel it.")]
    [InlineData("handoff-cancel-refused", "This hand-off is completed, so Weir did not cancel it.")]
    public async Task A_cancel_Weir_refuses_with_409_is_busy_and_carries_Weirs_words(string state, string weirSaid)
    {
        var recording = Rec(state);
        var result = await Service(new WeirReplay(recording)).CancelHandoffAsync(Connection, HandoffIdOf(recording), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Busy, result.Answer);
        Assert.Equal(weirSaid, result.Message);
    }

    // ----- outcome: Weir's replies -----

    [Theory]
    [InlineData("outcome-delivered", false)]
    [InlineData("outcome-delivered-without-output-path", true)]
    [InlineData("outcome-finished-in-burst-200", true)]
    [InlineData("outcome-already-recorded", false)]
    [InlineData("outcome-imported-with-reason", false)]
    [InlineData("outcome-not-imported-no-reason", false)]
    [InlineData("outcome-not-imported-default-reason", false)]
    [InlineData("outcome-not-imported-import-refused", false)]
    [InlineData("outcome-superseded", false)]
    [InlineData("outcome-superseded-newer", false)]
    public async Task An_outcome_Weir_acknowledges_with_200_is_answered_and_says_whether_it_let_go_of_its_copy(string state, bool released)
    {
        var recording = Rec(state);
        var recorded = recording.Exchanges.Last(e => e.Request.Method == "POST" && e.Request.Path.EndsWith("/outcome"));
        var result = await ReportRecordedAsync(recorded, new WeirReplay(recording));

        Assert.Equal(ProcessorAnswer.Answered, result.Answer);
        Assert.Equal(released, result.Released);
        Assert.Equal(Body(recorded.Reply.Body)["message"]!.GetValue<string>(), result.Message);
        Assert.Equal(released, Body(recorded.Reply.Body)["released"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("outcome-handoff-not-finished-queued", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-not-finished-working", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-not-finished", ProcessorAnswer.Busy)]
    [InlineData("outcome-handoff-ended", ProcessorAnswer.Refused)]
    [InlineData("outcome-handoff-ended-after-failed", ProcessorAnswer.Refused)]
    [InlineData("outcome-outcome-already-recorded", ProcessorAnswer.Settled)]
    public async Task A_409_outcome_is_read_by_its_code_never_by_its_words(string state, ProcessorAnswer expected)
    {
        var recording = Rec(state);
        var recorded = recording.Single;
        var result = await ReportRecordedAsync(recorded, new WeirReplay(recording));

        Assert.Equal(409, recorded.Reply.Status);
        Assert.Equal(expected, result.Answer);
        Assert.False(result.Released);
        Assert.Equal(Body(recorded.Reply.Body)["detail"]!.GetValue<string>(), result.Message);
    }

    [Fact]
    public async Task An_outcome_for_a_hand_off_Weir_never_received_is_not_found()
    {
        var recording = Rec("no-record");
        var recorded = ExchangeOf(recording, "POST", "/outcome");
        var result = await ReportRecordedAsync(recorded, new WeirReplay(recording));

        Assert.Equal(ProcessorAnswer.NotFound, result.Answer);
        Assert.Equal("Weir has no record of this hand-off.", result.Message);
    }

    [Fact]
    public async Task An_outcome_sent_while_Weir_is_switched_off_is_unreachable_and_so_owed_again()
    {
        var recording = Rec("outcome-delivered");
        var replay = new WeirReplay(recording) { Down = true };

        var result = await ReportRecordedAsync(recording.Single, replay);

        Assert.Equal(ProcessorAnswer.Unreachable, result.Answer);
        Assert.Equal("Deluno could not reach Weir.", result.Message);
    }

    // ----- helpers -----

    private static async Task<ProcessorSubmissionResult> SubmitRecordedAsync(Recording recording, WeirReplay replay)
    {
        var body = Body(recording.Exchanges.First(e => e.Request.Method == "POST" && e.Request.Path == "/api/v1/intake/webhook/deluno").Request.Body).AsObject();
        // Where Deluno gets the name and year: what it has stored for the title. The test supplies what the recording carried.
        var resolver = new Mock<IProcessorHandoffTitleResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<ProcessorHandoffItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(body["title"] is null ? null : new ProcessorHandoffTitle(body["title"]!.GetValue<string>(), body["year"]?.GetValue<int>()));
        var handoff = Handoff(
            body["handoffId"]!.GetValue<string>(),
            body["libraryId"]?.GetValue<string>() ?? "library-1",
            body["mediaType"]?.GetValue<string>() ?? "movie",
            body["sourcePath"]?.GetValue<string>() ?? string.Empty,
            body["releaseName"]?.GetValue<string>() ?? "Release",
            body["queueItemId"]?.GetValue<string>() ?? "queue-1",
            body["sourceFiles"]?.AsArray().Select(f => f!.GetValue<string>()).ToArray());
        return await Service(replay, resolver.Object).SubmitAsync(Connection, handoff, CancellationToken.None);
    }

    private static Task<ProcessorOutcomeResult> ReportRecordedAsync(Exchange recorded, WeirReplay replay)
    {
        var sent = Body(recorded.Request.Body).AsObject();
        var id = Regex.Match(recorded.Request.Path, @"/handoffs/deluno/([0-9a-fA-F-]{36})/outcome").Groups[1].Value;
        var imported = sent["outcome"]!.GetValue<string>() == "imported";
        return Service(replay).ReportOutcomeAsync(
            Connection,
            id,
            imported ? ProcessorHandoffOutcome.Imported : ProcessorHandoffOutcome.NotImported,
            // A not-imported report is sent without a path whatever it is offered; the test offers one to prove it.
            imported ? sent["importedPath"]?.GetValue<string>() : "/offered/x.mkv",
            sent["reason"]?.GetValue<string>(),
            DateTimeOffset.Parse(sent["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            CancellationToken.None);
    }

    internal static ProcessorHandoffItem Handoff(string id, string libraryId, string mediaType, string sourcePath, string releaseName, string queueItemId = "queue-1", IReadOnlyList<string>? sourceFiles = null)
        => new(id, libraryId, mediaType, "client-1", queueItemId, releaseName, sourcePath, "Weir", ProcessorHandoffStatuses.Waiting,
            null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SourceFiles: sourceFiles);
}
