using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Moq;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1239: what Deluno DOES with each reply Weir was recorded giving. The real client reads the recorded reply; the real
/// outcome delivery, hand-off cancellation and hand-off watcher act on what it read. Each test names what is stored, what
/// a person reads in Activity, and whether Deluno asks again.
/// </summary>
public sealed class WeirContractBehaviourTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-13T20:50:00Z", CultureInfo.InvariantCulture);

    private readonly Mock<IProcessorRepository> _repository = new() { CallBase = true };
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly List<(string Kind, string Message)> _said = [];
    private readonly List<(string Status, string? Output, string? ImportJobId, string? Message)> _updates = [];
    private readonly List<ProcessorHandoffObservation> _observations = [];
    private readonly List<(string Kind, string Summary)> _cases = [];
    private ProcessorOutcomeRecord? _owed;

    public WeirContractBehaviourTests()
    {
        _repository.Setup(r => r.FindProcessorConnectionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Connection);
        _repository.Setup(r => r.SaveProcessorOutcomeAsync(It.IsAny<ProcessorOutcomeRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessorOutcomeRecord, CancellationToken>((record, _) => _owed = record)
            .Returns(Task.CompletedTask);
        _repository.Setup(r => r.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, CancellationToken>((_, status, output, job, message, _) => _updates.Add((status, output, job, message)))
            .ReturnsAsync((string id, string status, string? _, string? _, string? _, CancellationToken _) => Handoff(id, status));
        _repository.Setup(r => r.RecordProcessorObservationAsync(It.IsAny<string>(), It.IsAny<ProcessorHandoffObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProcessorHandoffObservation, CancellationToken>((_, observation, _) => _observations.Add(observation))
            .Returns(Task.CompletedTask);
        _activity.Setup(a => a.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((kind, message, _, _, _, _, _) => _said.Add((kind, message)))
            .ReturnsAsync((ActivityEventItem)null!);
        _movies.Setup(m => m.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => _cases.Add((request.FailureKind!, request.Summary!)))
            .ReturnsAsync((MovieImportRecoveryCase)null!);
    }

    // ===== telling Weir what became of the file =====

    /// <summary>What Deluno stores and says for each way Weir acknowledges an outcome.</summary>
    [Theory]
    [InlineData("outcome-delivered", "delivered", false, "processing.outcome-reported", "Deluno told Weir it has")]
    [InlineData("outcome-delivered-without-output-path", "delivered", true, "processing.outcome-reported", "It released its copy.")]
    [InlineData("outcome-finished-in-burst-200", "delivered", true, "processing.outcome-reported", "It released its copy.")]
    [InlineData("outcome-already-recorded", "delivered", false, "processing.outcome-reported", "Weir left it where it is")]
    [InlineData("outcome-imported-with-reason", "delivered", false, "processing.outcome-reported", "(Left alone: Recorded.Sample.mkv, Recorded.Featurette.mkv)")]
    [InlineData("outcome-not-imported-no-reason", "delivered", false, "processing.outcome-reported", "kept its copy")]
    [InlineData("outcome-not-imported-default-reason", "delivered", false, "processing.outcome-reported", "The hand-off was closed, so Deluno will not import this file")]
    [InlineData("outcome-not-imported-import-refused", "delivered", false, "processing.outcome-reported", "Deluno could not match this file to a film in the library")]
    [InlineData("outcome-superseded", "delivered", false, "processing.outcome-reported", "Deluno told Weir it has")]
    [InlineData("outcome-superseded-newer", "delivered", false, "processing.outcome-reported", "Deluno told Weir it has")]
    public async Task An_outcome_Weir_acknowledges_is_stored_delivered_and_said_in_Activity_and_never_asked_again(
        string state, string delivery, bool released, string kind, string saidInActivity)
    {
        var recording = Rec(state);
        var recorded = OutcomeOf(recording);

        var record = await TellAsync(recording, recorded);

        Assert.Equal(delivery, record.Delivery);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(released, record.Released);
        Assert.Null(record.NextAttemptUtc);
        Assert.Equal(Body(recorded.Reply.Body)["message"]!.GetValue<string>(), record.ProcessorSaid);
        Assert.Equal(record, _owed);
        var line = Assert.Single(_said);
        Assert.Equal(kind, line.Kind);
        Assert.Contains(saidInActivity, line.Message);
    }

    [Theory]
    [InlineData("outcome-handoff-ended", "ended cancelled")]
    [InlineData("outcome-handoff-ended-after-failed", "ended failed")]
    public async Task A_hand_off_Weir_says_has_ended_is_refused_for_good_and_Weirs_words_are_in_Activity(string state, string weirSaid)
    {
        var recording = Rec(state);

        var record = await TellAsync(recording, recording.Single);

        Assert.Equal(ProcessorOutcomeDeliveries.Refused, record.Delivery);
        Assert.Equal(1, record.Attempts);
        Assert.Null(record.NextAttemptUtc);
        Assert.Contains(weirSaid, record.ProcessorSaid);
        var line = Assert.Single(_said);
        Assert.Equal("processing.outcome-not-reported", line.Kind);
        Assert.Contains(weirSaid, line.Message);
    }

    [Theory]
    [InlineData("outcome-handoff-not-finished-queued", "it is queued")]
    [InlineData("outcome-handoff-not-finished-working", "it is working")]
    public async Task A_hand_off_Weir_has_not_finished_stays_owed_and_is_asked_again_in_a_minute(string state, string weirSaid)
    {
        var recording = Rec(state);

        var record = await TellAsync(recording, recording.Single);

        Assert.Equal(ProcessorOutcomeDeliveries.Pending, record.Delivery);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(Start + TimeSpan.FromMinutes(1), record.NextAttemptUtc);
        Assert.Contains(weirSaid, record.ProcessorSaid);
        var line = Assert.Single(_said);
        Assert.Equal("processing.outcome-delayed", line.Kind);
        Assert.Contains("and will keep trying", line.Message);
    }

    [Fact]
    public async Task An_answer_Weir_already_has_is_settled_and_Deluno_stops_telling_it()
    {
        var recording = Rec("outcome-outcome-already-recorded");

        var record = await TellAsync(recording, recording.Single);

        Assert.Equal(ProcessorOutcomeDeliveries.Settled, record.Delivery);
        Assert.Null(record.NextAttemptUtc);
        var line = Assert.Single(_said);
        Assert.Equal("processing.outcome-kept", line.Kind);
        Assert.Contains("kept its earlier answer", line.Message);
    }

    [Fact]
    public async Task A_hand_off_Weir_never_received_is_not_a_failure_and_is_not_asked_again()
    {
        var recording = Rec("no-record");

        var record = await TellAsync(recording, ExchangeOf(recording, "POST", "/outcome"));

        Assert.Equal(ProcessorOutcomeDeliveries.NeverReceived, record.Delivery);
        Assert.Null(record.NextAttemptUtc);
        var line = Assert.Single(_said);
        Assert.Equal("processing.outcome-never-received", line.Kind);
        Assert.Contains("never received this hand-off", line.Message);
    }

    /// <summary>
    /// The resend timings (#1221): the outcome of one file is sent before Weir has started it (queued), while it works, and
    /// after it finished. The first two stay owed with a longer wait each time and say so once; the third is heard.
    /// </summary>
    [Fact]
    public async Task An_outcome_sent_before_and_during_Weirs_work_is_kept_and_retried_until_Weir_has_finished()
    {
        var finished = OutcomeOf(Rec("outcome-delivered"));
        var record = await TellAsync(Rec("outcome-handoff-not-finished-queued"), Rec("outcome-handoff-not-finished-queued").Single, ignoreIds: true);
        Assert.Equal((ProcessorOutcomeDeliveries.Pending, 1), (record.Delivery, record.Attempts));

        record = await Retry(record, "outcome-handoff-not-finished-working", Start.AddMinutes(1));
        Assert.Equal((ProcessorOutcomeDeliveries.Pending, 2), (record.Delivery, record.Attempts));
        Assert.Equal(Start.AddMinutes(1) + TimeSpan.FromMinutes(2), record.NextAttemptUtc);
        Assert.Single(_said);

        record = await Retry(record, "outcome-delivered", Start.AddMinutes(3));
        Assert.Equal((ProcessorOutcomeDeliveries.Delivered, 3), (record.Delivery, record.Attempts));
        Assert.Null(record.NextAttemptUtc);
        Assert.Equal(2, _said.Count);
        Assert.Equal("processing.outcome-reported", _said[1].Kind);
        Assert.Contains("on attempt 3", _said[1].Message);
        Assert.Equal(Body(finished.Reply.Body)["message"]!.GetValue<string>(), record.ProcessorSaid);
    }

    /// <summary>Weir switched off, and Weir refusing the secret: the outcome stays owed, with the reason in the words.</summary>
    [Fact]
    public async Task An_outcome_sent_while_Weir_is_switched_off_stays_owed_and_says_it_could_not_be_told()
    {
        var recording = Rec("outcome-delivered");

        var record = await TellAsync(recording, recording.Single, down: true);

        Assert.Equal(ProcessorOutcomeDeliveries.Pending, record.Delivery);
        Assert.Equal(Start + TimeSpan.FromMinutes(1), record.NextAttemptUtc);
        Assert.Equal("Deluno could not reach Weir.", record.ProcessorSaid);
        Assert.Equal("processing.outcome-delayed", Assert.Single(_said).Kind);
    }

    [Fact]
    public async Task An_outcome_sent_with_a_secret_Weir_refuses_stays_owed_and_names_the_fix()
    {
        var recording = Rec("outcome-delivered");
        var replay = new WeirReplay(Rec("capabilities-wrong-secret"), recording);

        var record = await TellAsync(recording, recording.Single, replay);

        Assert.Equal(ProcessorOutcomeDeliveries.Pending, record.Delivery);
        Assert.Contains("refused the webhook secret", record.ProcessorSaid);
        Assert.Equal("processing.outcome-delayed", Assert.Single(_said).Kind);
        Assert.DoesNotContain(replay.Sent, s => s.Path.EndsWith("/outcome"));
    }

    // ===== stopping the wait =====

    [Theory]
    [InlineData("handoff-cancelled")]
    [InlineData("handoff-cancelled-in-burst-204")]
    public async Task A_cancel_Weir_accepts_ends_the_hand_off_as_stopped_and_says_Weir_was_asked_to_drop_it(string state)
    {
        var recording = Rec(state);

        var stopped = await StopAsync(recording);

        Assert.False(stopped.Busy);
        Assert.NotNull(stopped.Ended);
        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Contains("and it was asked to drop", update.Message);
        Assert.Equal("processing.cancelled", Assert.Single(_said).Kind);
    }

    [Theory]
    [InlineData("handoff-cancel-working", "This hand-off is working, so Weir did not cancel it.")]
    [InlineData("handoff-cancel-too-late", "This hand-off is completed, so Weir did not cancel it.")]
    public async Task A_cancel_Weir_refuses_leaves_the_hand_off_alone_and_says_why(string state, string weirSaid)
    {
        var recording = Rec(state);

        var stopped = await StopAsync(recording);

        Assert.True(stopped.Busy);
        Assert.Null(stopped.Ended);
        Assert.Empty(_updates);
        Assert.Empty(_said);
        Assert.Contains("will not interrupt it", stopped.Message);
        Assert.EndsWith(weirSaid, stopped.Message);
    }

    // The same two answers as the two theories above, each loaded by its own recording name.
    [Fact]
    public async Task A_cancel_Weir_answered_204_to_ends_the_hand_off_as_stopped_and_says_Weir_was_asked_to_drop_it()
    {
        var recording = Rec("handoff-cancelled");

        var stopped = await StopAsync(recording);

        Assert.False(stopped.Busy);
        Assert.NotNull(stopped.Ended);
        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Contains("and it was asked to drop", update.Message);
    }

    [Fact]
    public async Task A_cancel_Weir_answered_409_working_to_leaves_the_hand_off_open_and_says_it_will_not_interrupt()
    {
        var recording = Rec("handoff-cancel-working");

        var stopped = await StopAsync(recording);

        Assert.True(stopped.Busy);
        Assert.Null(stopped.Ended);
        Assert.Empty(_updates);
        Assert.Contains("will not interrupt it", stopped.Message);
        Assert.EndsWith("This hand-off is working, so Weir did not cancel it.", stopped.Message);
    }

    [Fact]
    public async Task A_cancel_Weir_refuses_for_a_title_that_was_removed_ends_the_hand_off_anyway_and_does_not_import_its_output()
    {
        var recording = Rec("handoff-cancel-working");

        var stopped = await StopAsync(recording, discardEvenIfBusy: true);

        Assert.True(stopped.Busy);
        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, update.Status);
        Assert.Contains("had already started work", update.Message);
        Assert.Contains("will not be imported", update.Message);
    }

    [Fact]
    public async Task A_cancel_while_Weir_is_switched_off_ends_the_hand_off_and_says_Weir_may_still_have_it()
    {
        var recording = Rec("handoff-cancelled");

        var stopped = await StopAsync(recording, down: true);

        Assert.False(stopped.Busy);
        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Contains("could not be told, so it may still have", update.Message);
    }

    // ===== watching a hand-off: the status poll =====

    [Theory]
    [InlineData("status-queued", 0, "queued", 8)]
    [InlineData("status-queued", 1, "queued", 2)]
    [InlineData("status-working", 0, "working", null)]
    [InlineData("missing-file-status-working", 0, "working", null)]
    [InlineData("release-folder-status-working", 0, "working", null)]
    [InlineData("source-files-status-working", 0, "working", null)]
    [InlineData("source-files-status-queued", 0, "queued", 2)]
    [InlineData("resend-status-working", 0, "working", null)]
    [InlineData("resend-takeover-second-status-working", 0, "working", null)]
    [InlineData("completed-season-pack-status-working", 0, "working", null)]
    [InlineData("failed-partial-season-pack-corrupt-status-working", 0, "working", null)]
    public async Task A_hand_off_Weir_is_still_working_on_stays_open_and_the_screen_shows_what_Weir_said(string state, int exchange, string shown, int? place)
    {
        var recording = WeirContract.Rec(state);

        await WatchAsync(recording, exchange);

        var seen = Assert.Single(_observations);
        Assert.Equal(shown, seen.State);
        Assert.Equal(place, seen.QueuePosition);
        Assert.Equal(Body(recording.Exchanges[exchange].Reply.Body)["message"]?.GetValue<string>(), seen.Message);
        Assert.Empty(_updates);
        Assert.Empty(_said);
        Assert.Empty(_cases);
    }

    [Theory]
    [InlineData("status-completed")]
    [InlineData("completed-with-output-path")]
    [InlineData("completed-without-output-path")]
    [InlineData("resend-status-completed")]
    [InlineData("release-folder-status-completed")]
    [InlineData("source-files-status-completed")]
    [InlineData("resend-takeover-second-status-completed")]
    [InlineData("resend-takeover-second-final-status")]
    [InlineData("output-collision-replace-first-status")]
    [InlineData("output-collision-replace-second-status")]
    [InlineData("output-collision-keep-both-first-status")]
    [InlineData("output-collision-keep-both-second-status")]
    [InlineData("completed-season-pack-status-completed")]
    public async Task A_hand_off_Weir_says_is_completed_stays_open_for_its_own_report_or_the_output_folder(string state)
    {
        var recording = Rec(state);

        await WatchAsync(recording, 0);

        var seen = Assert.Single(_observations);
        Assert.Equal("completed", seen.State);
        Assert.Empty(_updates);
        Assert.Empty(_said);
        Assert.Empty(_cases);
    }

    [Fact]
    public async Task A_hand_off_Weir_says_failed_ends_failed_in_Weirs_words_and_leaves_a_case_to_clear()
    {
        var recording = Rec("status-failed");

        await WatchAsync(recording, 0);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Failed, update.Status);
        Assert.Equal("Weir reports failed for Sintel.2010.1080p: Skipped because this file is 0.0 MB, under the 50 MB minimum.", update.Message);
        Assert.Equal("processing.failed", Assert.Single(_said).Kind);
        Assert.Equal("processor-failed", Assert.Single(_cases).Kind);
    }

    [Fact]
    public async Task A_hand_off_Weir_says_it_cancelled_ends_stopped()
    {
        var recording = Rec("status-cancelled");

        await WatchAsync(recording, 0);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Equal("Weir stopped work on Sintel.2010.1080p.", update.Message);
        Assert.Equal("processing.cancelled", Assert.Single(_said).Kind);
        Assert.Empty(_cases);
    }

    /// <summary>The older of two sends of one file (Weir rc.18): cancelled with "This release was sent again ...", and the hand-off ends stopped.</summary>
    [Fact]
    public async Task The_older_send_of_a_file_sent_again_is_cancelled_by_Weir_and_ends_stopped()
    {
        var recording = Rec("resend-takeover-first-status");

        await WatchAsync(recording, 0);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Equal("Weir stopped work on Sintel.2010.1080p.", update.Message);
        Assert.Equal("processing.cancelled", Assert.Single(_said).Kind);
        Assert.Empty(_cases);
    }

    /// <summary>Weir 1.0.0-rc.18 says "rejected" for a file it threw out under its reject policy (#1239): the hand-off ends failed in Weir's words and a case is raised.</summary>
    [Fact]
    public async Task A_hand_off_Weir_says_it_rejected_ends_failed_in_Weirs_words_and_leaves_a_case_to_clear()
    {
        var recording = Rec("failed-rejected-status-rejected");

        await WatchAsync(recording, 0);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Failed, update.Status);
        Assert.StartsWith("Weir reports rejected for Sintel.2010.1080p: Weir only processes movie and TV files that contain a video stream.", update.Message);
        Assert.Equal("processing.failed", Assert.Single(_said).Kind);
        Assert.Equal("processor-failed", Assert.Single(_cases).Kind);
    }

    /// <summary>A queued answer whose message says how long Weir waits for a file to settle is a wait, not a problem.</summary>
    [Fact]
    public async Task A_queued_hand_off_Weir_is_letting_settle_stays_open_and_ends_nothing()
    {
        var recording = Rec("failed-rejected-status-queued");

        await WatchAsync(recording, 0);

        Assert.Equal("queued", Assert.Single(_observations).State);
        Assert.Empty(_updates);
        Assert.Empty(_cases);
    }

    [Fact]
    public async Task A_queued_season_pack_Weir_is_letting_settle_stays_open_and_ends_nothing()
    {
        var recording = Rec("failed-partial-season-pack-corrupt-status-queued");

        await WatchAsync(recording, 0);

        Assert.Equal("queued", Assert.Single(_observations).State);
        Assert.Empty(_updates);
        Assert.Empty(_cases);
    }

    [Fact]
    public async Task A_hand_off_Weir_has_no_record_of_is_not_believed_inside_the_grace_window()
    {
        var recording = Rec("no-record");

        await WatchAsync(recording, 0, updatedAgo: TimeSpan.FromMinutes(5));

        Assert.Empty(_updates);
        Assert.Empty(_observations);
        Assert.Empty(_said);
    }

    [Fact]
    public async Task A_hand_off_Weir_has_no_record_of_after_the_grace_window_is_lost_and_a_case_is_raised()
    {
        var recording = Rec("no-record");

        await WatchAsync(recording, 0, updatedAgo: TimeSpan.FromMinutes(20));

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Lost, update.Status);
        Assert.StartsWith("Weir says it never received Sintel.2010.1080p (asked ", update.Message);
        Assert.Equal("processing.lost", Assert.Single(_said).Kind);
        Assert.Equal("processor-lost", Assert.Single(_cases).Kind);
    }

    [Fact]
    public async Task A_hand_off_is_not_given_up_while_Weir_is_switched_off()
    {
        var recording = Rec("status-working");

        await WatchAsync(recording, 0, down: true);

        var seen = Assert.Single(_observations);
        Assert.NotNull(seen.UnreachableSinceUtc);
        Assert.Equal("Deluno could not reach Weir.", seen.Message);
        Assert.Empty(_updates);
        Assert.Empty(_cases);
    }

    [Fact]
    public async Task A_hand_off_is_not_given_up_while_Weir_refuses_the_secret_and_the_connection_says_so()
    {
        var replay = new WeirReplay(Rec("capabilities-wrong-secret"));

        await WatchWithAsync(replay, "any-id");

        var seen = Assert.Single(_observations);
        Assert.Contains("refused the webhook secret", seen.Message);
        Assert.Empty(_updates);
        Assert.Equal("processing.status-forbidden", Assert.Single(_said).Kind);
        _repository.Verify(r => r.RecordProcessorConnectionHealthAsync(Connection.Id, "degraded", It.Is<string?>(m => m!.Contains("refused the webhook secret")), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ===== the limit: how long Deluno waits for a processor that is queued or working =====

    /// <summary>
    /// Weir's recorded "queued" and "working" answers, read two days and an hour after Weir's last change: the library's limit.
    /// Under "import the original" the file is released to import unprocessed and Weir is asked to drop it.
    /// </summary>
    [Theory]
    [InlineData("status-queued")]
    public async Task A_hand_off_Weir_has_not_changed_for_the_limit_is_released_to_import_the_original_when_that_is_the_library_rule(string state)
    {
        var recording = RecordedReplies.Load("weir", "1.0.0-rc.13", state);
        var changed = DateTimeOffset.Parse(Body(recording.Exchanges[0].Reply.Body)["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var replay = new WeirReplay(Rec("capabilities"), recording, WeirContract.Rec("handoff-cancelled")) { IgnoreIds = true };

        await WatchWithAsync(replay, HandoffIdOf(recording), changed.AddHours(49), failureMode: "import-original", previous: AskedForDays(state, changed));

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Released, update.Status);
        Assert.Contains("no change for 2 days", update.Message);
        Assert.Contains(replay.Sent, sent => sent.Method == "DELETE" && sent.Path.Contains("/handoffs/deluno/", StringComparison.Ordinal));
        Assert.Empty(_cases);
    }

    [Theory]
    [InlineData("manual-review")]
    [InlineData("block")]
    public async Task A_queued_hand_off_Weir_has_not_changed_for_the_limit_is_left_for_a_person_under_the_other_library_rules(string mode)
    {
        var recording = RecordedReplies.Load("weir", "1.0.0-rc.13", "status-queued");
        var changed = DateTimeOffset.Parse(Body(recording.Exchanges[0].Reply.Body)["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var replay = new WeirReplay(Rec("capabilities"), recording, WeirContract.Rec("handoff-cancelled")) { IgnoreIds = true };

        await WatchWithAsync(replay, HandoffIdOf(recording), changed.AddHours(49), failureMode: mode, previous: AskedForDays("status-queued", changed));

        Assert.Equal(ProcessorHandoffStatuses.Stalled, Assert.Single(_updates).Status);
        Assert.Equal("processor-stalled", Assert.Single(_cases).Kind);
    }

    [Theory]
    [InlineData("import-original")]
    [InlineData("manual-review")]
    [InlineData("block")]
    public async Task A_hand_off_Weir_says_it_is_working_on_is_never_interrupted_at_the_limit_whatever_the_library_rule(string mode)
    {
        var recording = RecordedReplies.Load("weir", "1.0.0-rc.13", "status-working");
        var changed = DateTimeOffset.Parse(Body(recording.Exchanges[0].Reply.Body)["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var replay = new WeirReplay(Rec("capabilities"), recording) { IgnoreIds = true };

        await WatchWithAsync(replay, HandoffIdOf(recording), changed.AddHours(49), failureMode: mode, previous: AskedForDays("status-working", changed));

        Assert.All(_updates, update => Assert.Equal(ProcessorHandoffStatuses.Started, update.Status));
        Assert.Empty(_cases);
        Assert.DoesNotContain(replay.Sent, sent => sent.Method == "DELETE");
    }

    [Theory]
    [InlineData("status-queued", 3)]
    [InlineData("status-working", 3)]
    public async Task A_hand_off_Weir_changed_within_the_limit_keeps_waiting(string state, int hoursSinceChange)
    {
        var recording = RecordedReplies.Load("weir", "1.0.0-rc.13", state);
        var changed = DateTimeOffset.Parse(Body(recording.Exchanges[0].Reply.Body)["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var replay = new WeirReplay(Rec("capabilities"), recording) { IgnoreIds = true };

        await WatchWithAsync(replay, HandoffIdOf(recording), changed.AddHours(hoursSinceChange), previous: AskedForDays(state, changed));

        Assert.Empty(_updates);
        Assert.Empty(_cases);
        Assert.Single(_observations);
    }

    /// <summary>Deluno has been asking Weir about this hand-off since days before its last change, and Weir has always answered.</summary>
    private static ProcessorHandoffObservation AskedForDays(string state, DateTimeOffset lastChanged)
        => new(state.Contains("queued", StringComparison.Ordinal) ? ProcessorHandoffStates.Queued : ProcessorHandoffStates.Working, null, null, null, lastChanged, lastChanged.AddMinutes(-5), null, lastChanged.AddDays(-3));

    // ===== a release sent again, finished with no output path to name (#1232) =====

    /// <summary>
    /// Weir cleaned the file once for the older send and answers the newer one "completed" with no output path. Deluno
    /// matches the cleaned file it finds to the new send at once, and queues its import.
    /// </summary>
    [Fact]
    public async Task A_resend_Weir_answers_completed_without_an_output_path_is_matched_to_the_cleaned_file_at_once()
    {
        var recording = Rec("completed-without-output-path");
        using var folder = new TempFolder();
        var source = Path.Combine(folder.Path, "Completed", "Recorded.Weir.R.mkv");
        var cleaned = Path.Combine(folder.Path, "Ready", "Recorded.Weir.R.mkv", "Recorded.Weir.R.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(cleaned)!);
        File.WriteAllText(cleaned, "cleaned");
        File.SetLastWriteTimeUtc(cleaned, DateTime.UtcNow.AddHours(-1));
        var imports = new Mock<IProcessorOutputImportQueue>();
        imports.Setup(i => i.QueueAsync(It.IsAny<LibraryItem>(), It.IsAny<ProcessorHandoffItem>(), cleaned, It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new ProcessorOutputImport("job-1", "dispatch-1", "movie-1"));
        _repository.Setup(r => r.ReplacedAnEarlierSendAsync("library-1", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await WatchAsync(recording, 0, imports: imports.Object, source: source, outputFolder: Path.Combine(folder.Path, "Ready"));

        var update = Assert.Single(_updates);
        Assert.Equal((ProcessorHandoffStatuses.Completed, cleaned, "job-1"), (update.Status, update.Output, update.ImportJobId));
        var line = Assert.Single(_said);
        Assert.Equal("processing.completed.import-queued", line.Kind);
        Assert.Contains("had already finished", line.Message);
        Assert.Contains("for the send Deluno replaced when the release was sent again", line.Message);
    }

    [Fact]
    public async Task A_completed_answer_with_no_output_path_for_a_hand_off_that_replaced_nothing_waits_for_Weirs_report()
    {
        var recording = Rec("completed-without-output-path");
        var imports = new Mock<IProcessorOutputImportQueue>();

        await WatchAsync(recording, 0, imports: imports.Object);

        Assert.Empty(_updates);
        Assert.Empty(_said);
        imports.Verify(i => i.QueueAsync(It.IsAny<LibraryItem>(), It.IsAny<ProcessorHandoffItem>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    // ===== helpers =====

    private static Exchange OutcomeOf(Recording recording)
        => recording.Exchanges.Last(e => e.Request.Method == "POST" && e.Request.Path.EndsWith("/outcome"));

    private async Task<ProcessorOutcomeRecord> TellAsync(Recording recording, Exchange recorded, WeirReplay? replay = null, bool down = false, bool ignoreIds = false)
    {
        replay ??= new WeirReplay(Rec("capabilities"), recording) { Down = down, IgnoreIds = ignoreIds };
        var sent = Body(recorded.Request.Body).AsObject();
        var id = Regex.Match(recorded.Request.Path, @"/handoffs/deluno/([0-9a-fA-F-]{36})/outcome").Groups[1].Value;
        var handoff = Handoff(id, ProcessorHandoffStatuses.Completed) with { ProcessorName = "Weir", ReleaseName = "Sintel.2010.1080p" };
        var outcome = sent["outcome"]!.GetValue<string>() == "imported" ? ProcessorHandoffOutcome.Imported : ProcessorHandoffOutcome.NotImported;
        return (await ProcessorOutcomeDelivery.RecordAndSendAsync(
            _repository.Object,
            Service(replay),
            _activity.Object,
            handoff,
            outcome,
            sent["importedPath"]?.GetValue<string>(),
            sent["reason"]?.GetValue<string>(),
            Start,
            CancellationToken.None))!;
    }

    private async Task<ProcessorOutcomeRecord> Retry(ProcessorOutcomeRecord owed, string state, DateTimeOffset now)
    {
        var replay = new WeirReplay(Rec("capabilities"), Rec(state)) { IgnoreIds = true };
        return await ProcessorOutcomeDelivery.SendAsync(_repository.Object, Service(replay), _activity.Object, owed, now, CancellationToken.None);
    }

    private async Task<ProcessorHandoffStopped> StopAsync(Recording recording, bool down = false, bool discardEvenIfBusy = false)
    {
        var id = HandoffIdOf(recording);
        return await ProcessorHandoffCancellation.StopWaitingAsync(
            _repository.Object,
            Service(new WeirReplay(recording) { Down = down }),
            _activity.Object,
            Handoff(id, ProcessorHandoffStatuses.Started) with { ProcessorName = "Weir", ReleaseName = "Sintel.2010.1080p" },
            name => $"You stopped waiting for {name}",
            CancellationToken.None,
            discardEvenIfBusy: discardEvenIfBusy,
            ending: discardEvenIfBusy ? ProcessorHandoffStatuses.Dismissed : ProcessorHandoffStatuses.Cancelled);
    }

    private Task WatchAsync(
        Recording recording,
        int exchange,
        bool down = false,
        TimeSpan? updatedAgo = null,
        IProcessorOutputImportQueue? imports = null,
        string? source = null,
        string? outputFolder = null,
        string failureMode = "import-original")
        => WatchWithAsync(
            new WeirReplay(Rec("capabilities"), recording) { Down = down },
            HandoffIdOf(recording, exchange),
            Now(recording, exchange),
            updatedAgo,
            imports,
            source,
            outputFolder,
            failureMode);

    /// <summary>The moment the watcher looks: shortly after Weir's last change, so that no stall limit is near.</summary>
    private static DateTimeOffset Now(Recording recording, int exchange)
        => Body(recording.Exchanges[exchange].Reply.Body)["lastChangedUtc"] is { } changed
            ? DateTimeOffset.Parse(changed.GetValue<string>(), CultureInfo.InvariantCulture).AddSeconds(30)
            : Start;

    private async Task WatchWithAsync(
        WeirReplay replay,
        string handoffId,
        DateTimeOffset? now = null,
        TimeSpan? updatedAgo = null,
        IProcessorOutputImportQueue? imports = null,
        string? source = null,
        string? outputFolder = null,
        string failureMode = "import-original",
        ProcessorHandoffObservation? previous = null)
    {
        var at = now ?? Start;
        var updated = at - (updatedAgo ?? TimeSpan.FromMinutes(1));
        _repository.Setup(r => r.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    handoffId, "library-1", "movies", "qbittorrent", "hash-1", "Sintel.2010.1080p",
                    source ?? "/downloads/completed/Sintel.2010.1080p", "Weir", ProcessorHandoffStatuses.Started, null, null, null,
                    at - TimeSpan.FromMinutes(2), updated, previous)
            ]);
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        library = library with
        {
            Id = "library-1",
            Name = "Movies",
            MediaType = "movies",
            ImportWorkflow = "refine-before-import",
            ProcessorName = "Weir",
            ProcessorTimeoutMinutes = ProcessorStallLimits.DefaultMinutes,
            ProcessorFailureMode = failureMode,
            ProcessorOutputPath = outputFolder
        };
        await new ProcessorHandoffWatcher(_repository.Object, Service(replay), _activity.Object, _movies.Object, _series.Object, imports)
            .WatchAsync([library], at, CancellationToken.None);
    }

    private static ProcessorHandoffItem Handoff(string id, string status)
        => new(id, "library-1", "movies", "qbittorrent", "hash-1", "Sintel.2010.1080p", "/downloads/completed/Sintel.2010.1080p", "Weir", status,
            null, null, null, Start.AddHours(-1), Start.AddHours(-1));

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-contract-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
