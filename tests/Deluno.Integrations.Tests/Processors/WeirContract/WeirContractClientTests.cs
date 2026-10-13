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

    // ----- hand-off submit: Deluno's request -----

    /// <summary>
    /// The request body Deluno builds for the same hand-off is the body Weir was recorded receiving: the same members,
    /// the same order, the same values. A renamed, added or dropped member fails here before Weir sees it.
    /// </summary>
    [Theory]
    [InlineData("handoff-accepted")]
    [InlineData("handoff-accepted-missing-file")]
    [InlineData("handoff-release-folder-accepted")]
    [InlineData("handoff-source-files-accepted")]
    [InlineData("handoff-resend-accepted")]
    [InlineData("handoff-refused-not-watched")]
    [InlineData("handoff-refused-wrong-secret")]
    [InlineData("outcome-superseded")]
    public async Task The_hand_off_Deluno_sends_is_the_hand_off_Weir_was_recorded_receiving(string state)
    {
        var recording = Rec(state);
        var recorded = recording.Exchanges.First(e => e.Request.Method == "POST" && e.Request.Path == "/api/v1/intake/webhook/deluno");
        var replay = new WeirReplay(recording);

        await SubmitRecordedAsync(recording, replay);

        var sent = Assert.Single(replay.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        Assert.Equal(
            Body(recorded.Request.Body).AsObject().Select(p => p.Key),
            Body(sent.Body).AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(Body(recorded.Request.Body), Body(sent.Body)), $"Deluno sent {sent.Body}, Weir was recorded receiving {recorded.Request.Body}.");
    }

    [Fact]
    public async Task A_hand_off_carries_the_secret_and_a_key_that_changes_when_the_row_is_put_back_to_waiting()
    {
        var recording = Rec("handoff-accepted");
        var replay = new WeirReplay(recording);
        await SubmitRecordedAsync(recording, replay);

        var sent = Assert.Single(replay.Sent);
        Assert.Equal(Secret, sent.Headers["X-Webhook-Secret"]);
        Assert.Matches(@"^[0-9a-f-]{36}:\d+$", sent.Headers["Idempotency-Key"]);
        Assert.StartsWith(HandoffIdOf(recording) + ":", sent.Headers["Idempotency-Key"]);
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

    [Fact]
    public async Task A_queued_hand_off_says_its_place_in_line_and_a_completed_one_with_no_output_path_says_none()
    {
        var queued = await Service(ReplayOf("status-queued")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("status-queued")), CancellationToken.None);
        var resent = await Service(ReplayOf("completed-without-output-path")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("completed-without-output-path")), CancellationToken.None);
        var finished = await Service(ReplayOf("completed-with-output-path")).GetHandoffStatusAsync(Connection, HandoffIdOf(Rec("completed-with-output-path")), CancellationToken.None);

        Assert.Equal((ProcessorHandoffStates.Queued, 12), (queued.State, queued.QueuePosition));
        Assert.Equal(ProcessorHandoffStates.Completed, resent.State);
        Assert.Null(resent.OutputPath);
        Assert.Equal(ProcessorHandoffStates.Completed, finished.State);
        Assert.False(string.IsNullOrWhiteSpace(finished.OutputPath));
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

    // ----- outcome: Deluno's request -----

    /// <summary>
    /// The body Deluno sends for an outcome is the body Weir was recorded receiving: the same members in the same order
    /// (outcome, occurredUtc, importedPath, reason) and the same values. A not-imported outcome never carries an imported path.
    /// </summary>
    [Theory]
    [InlineData("outcome-delivered")]
    [InlineData("outcome-delivered-without-output-path")]
    [InlineData("outcome-finished-in-burst-200")]
    [InlineData("outcome-already-recorded")]
    [InlineData("outcome-imported-with-reason")]
    [InlineData("outcome-not-imported-no-reason")]
    [InlineData("outcome-not-imported-default-reason")]
    [InlineData("outcome-not-imported-import-refused")]
    [InlineData("outcome-handoff-not-finished-queued")]
    [InlineData("outcome-handoff-not-finished-working")]
    [InlineData("outcome-handoff-ended")]
    [InlineData("outcome-handoff-ended-after-failed")]
    [InlineData("outcome-outcome-already-recorded")]
    [InlineData("no-record")]
    [InlineData("outcome-superseded")]
    [InlineData("outcome-superseded-newer")]
    public async Task The_outcome_Deluno_sends_is_the_outcome_Weir_was_recorded_receiving(string state)
    {
        var recording = Rec(state);
        var recorded = recording.Exchanges.Last(e => e.Request.Method == "POST" && e.Request.Path.EndsWith("/outcome"));
        var replay = new WeirReplay(recording);

        await ReportRecordedAsync(recorded, replay);

        var sent = Assert.Single(replay.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        Assert.Equal(Secret, sent.Headers["X-Webhook-Secret"]);
        var wanted = Body(recorded.Request.Body).AsObject();
        var actual = Body(sent.Body).AsObject();
        Assert.Equal(wanted.Select(p => p.Key), actual.Select(p => p.Key));
        // The instant is compared as an instant: the recorder and System.Text.Json write the same moment two ways.
        Assert.Equal(
            DateTimeOffset.Parse(wanted["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(actual["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        wanted.Remove("occurredUtc");
        actual.Remove("occurredUtc");
        Assert.True(JsonNode.DeepEquals(wanted, actual), $"Deluno sent {sent.Body}, Weir was recorded receiving {recorded.Request.Body}.");
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
