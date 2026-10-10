using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #511: Deluno stops giving files to a processor a fixed six hours. It asks
/// the processor what it is doing, waits while the answer is "busy", and gives
/// up only when nothing has changed for the library's limit while the
/// processor was answering.
/// </summary>
public sealed class ProcessorHandoffWatcherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");

    private readonly Mock<IProcessorRepository> _repository = new();
    private readonly Mock<IProcessorConnectionService> _processor = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly List<(string Status, string? Message)> _updates = [];
    private readonly List<ProcessorHandoffObservation> _observations = [];
    private readonly List<CreateMovieImportRecoveryCaseRequest> _cases = [];

    public ProcessorHandoffWatcherTests()
    {
        _repository.Setup(repo => repo.FindProcessorConnectionAsync(It.IsAny<string?>(), "Weir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorConnectionItem(
                "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
                "X-Webhook-Secret", "secret", true, "healthy", null, null, Now, Now));
        _repository.Setup(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, CancellationToken>((_, status, _, _, message, _) => _updates.Add((status, message)))
            .ReturnsAsync((ProcessorHandoffItem?)null);
        _repository.Setup(repo => repo.RecordProcessorObservationAsync(It.IsAny<string>(), It.IsAny<ProcessorHandoffObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProcessorHandoffObservation, CancellationToken>((_, observation, _) => _observations.Add(observation))
            .Returns(Task.CompletedTask);
        _movies.Setup(repo => repo.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => _cases.Add(request))
            .ReturnsAsync((MovieImportRecoveryCase)null!);
        CanAsk(status: true, cancel: true);
    }

    [Fact]
    public async Task A_file_queued_behind_others_is_waited_for_however_long_it_has_been_with_the_processor()
    {
        GivenHandoff(updated: Now.AddDays(-4), previous: Answering(since: Now.AddDays(-4)));
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddHours(-3), queuePosition: 3);

        await Watch();

        Assert.Empty(_updates);
        var seen = Assert.Single(_observations);
        Assert.Equal(3, seen.QueuePosition);
        Assert.Equal(Now, seen.CheckedUtc);
    }

    /// <summary>
    /// #572. Every other rule measures from the processor's own last change, or
    /// from it answering again after being unreachable — so a processor that
    /// goes quiet for one pass and comes back sets the clock to zero. One blip
    /// every other day held a file for a whole fourteen-day run, and because a
    /// hand-off still with a processor is deliberately not counted, nothing on
    /// any screen said so.
    /// </summary>
    [Fact]
    public async Task A_processor_that_keeps_resetting_the_clock_is_still_given_up_on_in_the_end()
    {
        GivenHandoff(
            updated: Now.AddMinutes(-5),
            previous: Answering(since: Now.AddMinutes(-5)),
            mode: "block",
            created: Now.AddDays(-9));
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddMinutes(-5), queuePosition: 2);

        await Watch();

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
        Assert.Contains("has had Sintel.2010.1080p for 9 days", message);
    }

    /// <summary>It says it is working, and has said so for over a week.</summary>
    [Fact]
    public async Task A_processor_that_says_it_is_working_for_ever_is_still_given_up_on()
    {
        GivenHandoff(
            updated: Now.AddDays(-9),
            previous: Answering(since: Now.AddDays(-9)),
            mode: "block",
            created: Now.AddDays(-9));
        Answers(ProcessorHandoffStates.Working, lastChanged: Now.AddDays(-9));

        await Watch();

        var (status, _) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
    }

    /// <summary>A run always scheduled for tomorrow is a run that never comes.</summary>
    [Fact]
    public async Task A_run_always_scheduled_ahead_is_still_given_up_on()
    {
        GivenHandoff(
            updated: Now.AddDays(-9),
            previous: Answering(since: Now.AddDays(-9)),
            mode: "block",
            created: Now.AddDays(-9));
        Answers(ProcessorHandoffStates.Scheduled, lastChanged: Now.AddHours(-1), scheduledFor: Now.AddHours(13));

        await Watch();

        var (status, _) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
    }

    /// <summary>
    /// The backstop is generous on purpose: the library's limit is what the
    /// person chose, and a processor working within it is not a problem.
    /// </summary>
    [Fact]
    public async Task A_file_held_for_less_than_the_backstop_is_left_alone()
    {
        GivenHandoff(
            updated: Now.AddMinutes(-5),
            previous: Answering(since: Now.AddMinutes(-5)),
            created: Now.AddDays(-5));
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddMinutes(-5), queuePosition: 2);

        await Watch();

        Assert.Empty(_updates);
    }

    [Fact]
    public async Task A_run_scheduled_ahead_is_never_a_stall()
    {
        GivenHandoff(updated: Now.AddDays(-5), previous: Answering(since: Now.AddDays(-5)));
        Answers(ProcessorHandoffStates.Scheduled, lastChanged: Now.AddDays(-5), scheduledFor: Now.AddHours(13));

        await Watch();

        Assert.Empty(_updates);
    }

    [Fact]
    public async Task Queued_while_the_processor_waits_on_Deluno_is_never_a_stall()
    {
        GivenHandoff(updated: Now.AddDays(-5), previous: Answering(since: Now.AddDays(-5)));
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddDays(-5), message: "Waiting for Deluno to finish importing the previous copy.");

        await Watch();

        Assert.Empty(_updates);
    }

    [Fact]
    public async Task No_change_for_the_limit_imports_the_original_when_that_is_the_library_rule()
    {
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddDays(-3)), mode: "import-original");
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddHours(-49), queuePosition: 1);

        await Watch();

        _processor.Verify(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), "handoff-1", It.IsAny<CancellationToken>()), Times.Once);
        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Released, status);
        Assert.Contains("no change for 2 days", message, StringComparison.Ordinal);
        Assert.Empty(_cases);
    }

    [Theory]
    [InlineData("manual-review")]
    [InlineData("block")]
    public async Task No_change_for_the_limit_leaves_it_for_a_person_under_the_other_rules(string mode)
    {
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddDays(-3)), mode: mode);
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddHours(-49));

        await Watch();

        Assert.Equal(ProcessorHandoffStatuses.Stalled, Assert.Single(_updates).Status);
        Assert.Equal("processor-stalled", Assert.Single(_cases).FailureKind);
    }

    [Fact]
    public async Task A_file_the_processor_finished_that_never_arrived_ends_at_the_limit()
    {
        // #561: finished more than two days ago, and neither its report nor its
        // output closed the hand-off. The answering streak restarted an hour
        // ago, when the processor was upgraded; the processor's own finish time
        // is what counts.
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddHours(-1)), mode: "block");
        Answers(ProcessorHandoffStates.Completed, lastChanged: Now.AddHours(-49));

        await Watch();

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
        Assert.Contains("Weir finished Sintel.2010.1080p 2 days ago, but no processed file arrived", message, StringComparison.Ordinal);
        Assert.Equal("processor-timeout", Assert.Single(_cases).FailureKind);
    }

    [Theory]
    [InlineData(ProcessorHandoffStates.Completed)]
    [InlineData(ProcessorHandoffStates.PassedThrough)]
    public async Task A_file_the_processor_has_just_finished_is_left_for_its_output(string state)
    {
        GivenHandoff(updated: Now.AddHours(-2), previous: Answering(since: Now.AddHours(-2)), mode: "block");
        Answers(state, lastChanged: Now.AddMinutes(-10));

        await Watch();

        Assert.Empty(_updates);
    }

    [Fact]
    public async Task A_limit_set_on_the_library_is_the_limit_used()
    {
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddDays(-3)), stallMinutes: 24 * 60);
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddHours(-25));

        await Watch();

        Assert.Equal(ProcessorHandoffStatuses.Released, Assert.Single(_updates).Status);
    }

    [Fact]
    public async Task A_processor_that_started_the_file_meanwhile_is_left_to_finish()
    {
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddDays(-3)));
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddHours(-49));
        _processor.Setup(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffCancelResult(ProcessorAnswer.Busy, "Working on it."));

        await Watch();

        Assert.Empty(_updates);
    }

    [Fact]
    public async Task Working_with_no_change_is_said_once_and_never_interrupted()
    {
        GivenHandoff(updated: Now.AddDays(-3), previous: Answering(since: Now.AddDays(-3)));
        Answers(ProcessorHandoffStates.Working, lastChanged: Now.AddHours(-60));

        await Watch();

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Started, status);
        Assert.Contains("working on", message, StringComparison.Ordinal);
        _processor.Verify(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A processor switched off for the weekend does not get its files given
    /// up on - neither while it is off, nor the moment it comes back.
    /// </summary>
    [Fact]
    public async Task Time_the_processor_could_not_be_asked_does_not_count_against_the_file()
    {
        GivenHandoff(updated: Now.AddDays(-4), previous: Answering(since: Now.AddDays(-4)));
        _processor.Setup(service => service.GetHandoffStatusAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffStatusResult(ProcessorAnswer.Unreachable, Message: "Deluno could not reach Weir."));

        await Watch();

        Assert.Empty(_updates);
        Assert.Equal(Now, Assert.Single(_observations).UnreachableSinceUtc);
    }

    [Fact]
    public async Task A_processor_back_after_being_unreachable_gets_the_whole_limit_again()
    {
        var previous = new ProcessorHandoffObservation(ProcessorHandoffStates.Queued, null, 2, null, Now.AddDays(-3), Now.AddMinutes(-5), Now.AddDays(-2), null);
        GivenHandoff(updated: Now.AddDays(-4), previous: previous);
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddDays(-3), queuePosition: 2);

        await Watch();

        Assert.Empty(_updates);
        Assert.Equal(Now, Assert.Single(_observations).AnsweringSinceUtc);
    }

    [Fact]
    public async Task A_processor_stored_as_not_answering_that_answers_again_is_stored_healthy_at_once()
    {
        _repository.Setup(repo => repo.FindProcessorConnectionAsync(It.IsAny<string?>(), "Weir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorConnectionItem(
                "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
                "X-Webhook-Secret", "secret", true, "unreachable", null, null, Now, Now));
        var previous = new ProcessorHandoffObservation(ProcessorHandoffStates.Queued, null, 2, null, Now.AddDays(-3), Now.AddMinutes(-5), Now.AddDays(-2), null);
        GivenHandoff(updated: Now.AddDays(-4), previous: previous);
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddDays(-3), queuePosition: 2);

        await Watch();

        _repository.Verify(repo => repo.RecordProcessorConnectionHealthAsync("connection-1", "healthy", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_processor_already_stored_healthy_is_not_written_again_when_it_answers()
    {
        var previous = new ProcessorHandoffObservation(ProcessorHandoffStates.Queued, null, 2, null, Now.AddDays(-3), Now.AddMinutes(-5), Now.AddDays(-2), null);
        GivenHandoff(updated: Now.AddDays(-4), previous: previous);
        Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddDays(-3), queuePosition: 2);

        await Watch();

        _repository.Verify(repo => repo.RecordProcessorConnectionHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_refused_question_says_to_set_a_webhook_secret_and_gives_up_on_nothing()
    {
        GivenHandoff(updated: Now.AddDays(-9), previous: null);
        _processor.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Forbidden, false, false, "Set a webhook secret in Weir and the same secret on this processor connection in Deluno, so Deluno can ask about hand-offs."));

        await Watch();

        Assert.Empty(_updates);
        _repository.Verify(repo => repo.RecordProcessorConnectionHealthAsync("connection-1", "degraded", It.Is<string>(text => text.Contains("webhook secret")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_hand_off_the_processor_never_received_is_lost_and_offers_the_original()
    {
        GivenHandoff(updated: Now.AddHours(-2), previous: null);
        _processor.Setup(service => service.GetHandoffStatusAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffStatusResult(ProcessorAnswer.NotFound));

        await Watch();

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Lost, status);
        // #929: the reason names the processor and the two times it was asked
        // — the moment the grace window started (the hand-off's last real
        // change, two hours ago) and now.
        Assert.Contains("Weir says it never received", message);
        Assert.Contains($"asked {Now.AddHours(-2).ToLocalTime():HH:mm} and {Now.ToLocalTime():HH:mm}", message);
        var raised = Assert.Single(_cases);
        Assert.Equal("processor-lost", raised.FailureKind);
        Assert.Equal("Import the original, hand it to the processor again, or search for another release.", raised.RecommendedAction);
    }

    [Fact]
    public async Task A_hand_off_just_submitted_is_not_called_lost()
    {
        GivenHandoff(updated: Now.AddMinutes(-2), previous: null);
        _processor.Setup(service => service.GetHandoffStatusAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffStatusResult(ProcessorAnswer.NotFound));

        await Watch();

        Assert.Empty(_updates);
    }

    [Theory]
    [InlineData(ProcessorHandoffStates.Cancelled, ProcessorHandoffStatuses.Cancelled)]
    [InlineData(ProcessorHandoffStates.Failed, ProcessorHandoffStatuses.Failed)]
    [InlineData("exploded", ProcessorHandoffStatuses.Failed)]
    public async Task A_state_that_will_not_deliver_stops_the_wait(string state, string expected)
    {
        GivenHandoff(updated: Now.AddHours(-1), previous: null);
        Answers(state, lastChanged: Now.AddMinutes(-10));

        await Watch();

        Assert.Equal(expected, Assert.Single(_updates).Status);
    }

    /// <summary>
    /// #890 (cleanup sheet F7): the processor reporting failure outright is
    /// one of the two moments Deluno may treat a partial output as abandoned.
    /// A file still growing (zero-length here, the least flaky way to force
    /// "not ready") is deleted and the deletion is folded into the same
    /// failure entry.
    /// </summary>
    [Fact]
    public async Task A_processor_reporting_failure_deletes_its_crashed_partial_output()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"deluno-partial-{Guid.NewGuid():n}.mkv");
        File.WriteAllBytes(outputPath, []);
        try
        {
            GivenHandoff(updated: Now.AddHours(-1), previous: null, outputPath: outputPath);
            Answers(ProcessorHandoffStates.Failed, lastChanged: Now.AddMinutes(-10));

            await Watch();

            var (status, message) = Assert.Single(_updates);
            Assert.Equal(ProcessorHandoffStatuses.Failed, status);
            Assert.Contains("Deleted the processor's partial output", message);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    /// <summary>
    /// #890: a complete, stable file sitting at the hand-off's output path is
    /// not what crashed - it is a different problem (#891/#929/#921) and
    /// giving up here must never delete it.
    /// </summary>
    [Fact]
    public async Task Giving_up_never_deletes_a_complete_stable_output()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"deluno-complete-{Guid.NewGuid():n}.mkv");
        File.WriteAllBytes(outputPath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddMinutes(-10));
        try
        {
            GivenHandoff(
                updated: Now.AddMinutes(-5),
                previous: Answering(since: Now.AddMinutes(-5)),
                mode: "block",
                created: Now.AddDays(-9),
                outputPath: outputPath);
            Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddMinutes(-5), queuePosition: 2);

            await Watch();

            var (status, message) = Assert.Single(_updates);
            Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
            Assert.DoesNotContain("Deleted the processor's partial output", message);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// #890: giving up on a hand-off is the other of the two moments a
    /// partial output may be treated as abandoned - here via the "no change
    /// for the limit" path rather than an outright failure report.
    /// </summary>
    [Fact]
    public async Task Giving_up_on_a_stalled_hand_off_deletes_its_crashed_partial_output()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"deluno-partial-{Guid.NewGuid():n}.mkv");
        File.WriteAllBytes(outputPath, []);
        try
        {
            GivenHandoff(
                updated: Now.AddMinutes(-5),
                previous: Answering(since: Now.AddMinutes(-5)),
                mode: "block",
                created: Now.AddDays(-9),
                outputPath: outputPath);
            Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddMinutes(-5), queuePosition: 2);

            await Watch();

            var (status, message) = Assert.Single(_updates);
            Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
            Assert.Contains("Deleted the processor's partial output", message);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    /// <summary>
    /// #953: giving up on a hand-off must clean up more than the single
    /// tracked output path - the processor's own working folder for it can
    /// hold other scraps and, once those are gone, an empty folder. Both are
    /// this hand-off's own, so both go; a sibling hand-off's folder is left
    /// alone (keeping a complete file is covered directly on
    /// <c>ProcessorHandoffOutputCleanup</c>, which this delegates to).
    /// </summary>
    [Fact]
    public async Task Giving_up_cleans_scraps_and_the_now_empty_handoff_folder()
    {
        var processorOutputPath = Path.Combine(Path.GetTempPath(), $"deluno-output-{Guid.NewGuid():n}");
        var handoffFolder = Directory.CreateDirectory(Path.Combine(processorOutputPath, "Sintel.2010.1080p")).FullName;
        var scrap = Path.Combine(handoffFolder, "Sintel.2010.1080p.mkv.partial");
        File.WriteAllText(scrap, "still writing");
        var siblingFolder = Directory.CreateDirectory(Path.Combine(processorOutputPath, "Arrival.2016.1080p")).FullName;
        var siblingFile = Path.Combine(siblingFolder, "Arrival.2016.1080p.mkv.partial");
        File.WriteAllText(siblingFile, "unrelated hand-off");
        try
        {
            GivenHandoff(
                updated: Now.AddMinutes(-5),
                previous: Answering(since: Now.AddMinutes(-5)),
                mode: "block",
                created: Now.AddDays(-9));
            Answers(ProcessorHandoffStates.Queued, lastChanged: Now.AddMinutes(-5), queuePosition: 2);

            await Watcher().WatchAsync(
                [Library(_mode, _stallMinutes) with { ProcessorOutputPath = processorOutputPath }],
                Now,
                CancellationToken.None);

            var (status, _) = Assert.Single(_updates);
            Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
            Assert.False(File.Exists(scrap), "the not-yet-renamed leftover should be cleared");
            Assert.False(Directory.Exists(handoffFolder), "the folder should go once its only files are cleared");
            Assert.True(File.Exists(siblingFile), "a sibling hand-off's own folder must never be touched");
        }
        finally
        {
            if (Directory.Exists(processorOutputPath))
            {
                Directory.Delete(processorOutputPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_processor_that_cannot_be_asked_is_judged_from_the_hand_off_last_change()
    {
        CanAsk(status: false, cancel: false);
        GivenHandoff(updated: Now.AddHours(-49), previous: null, mode: "block");

        await Watch();

        Assert.Equal(ProcessorHandoffStatuses.TimedOut, Assert.Single(_updates).Status);
        Assert.Equal("processor-timeout", Assert.Single(_cases).FailureKind);
    }

    [Fact]
    public async Task A_processor_that_cannot_be_asked_still_gets_the_whole_limit()
    {
        CanAsk(status: false, cancel: false);
        GivenHandoff(updated: Now.AddHours(-47), previous: null);

        await Watch();

        Assert.Empty(_updates);
    }

    /// <summary>
    /// Seen on the rig before the soak: a proof library was removed with two
    /// hand-offs still submitted. The watcher skipped any hand-off whose library
    /// it could not find, so no limit ever applied to them, no screen could
    /// close them, and Transfers counted them as work in progress for good.
    /// </summary>
    [Fact]
    public async Task A_hand_off_whose_library_was_removed_is_closed_rather_than_left_open_for_ever()
    {
        GivenHandoff(updated: Now.AddHours(-1), previous: null);

        await Watcher().WatchAsync([], Now, CancellationToken.None, allLibraries: []);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Contains("has been removed", update.Message);
    }

    [Fact]
    public async Task A_hand_off_whose_library_stopped_processing_before_import_is_closed()
    {
        GivenHandoff(updated: Now.AddHours(-1), previous: null);
        var switched = Library(_mode, _stallMinutes) with { ImportWorkflow = "standard" };

        await Watcher().WatchAsync([switched], Now, CancellationToken.None, allLibraries: [switched]);

        var update = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Cancelled, update.Status);
        Assert.Contains("no longer processes downloads", update.Message);
    }

    /// <summary>
    /// A NAS that is switched off is not a removed library. Closing its hand-offs
    /// would throw away work the processor is still doing.
    /// </summary>
    [Fact]
    public async Task A_library_whose_folder_cannot_be_reached_right_now_keeps_its_hand_offs()
    {
        GivenHandoff(updated: Now.AddHours(-1), previous: null);

        await Watcher().WatchAsync([], Now, CancellationToken.None, allLibraries: [Library(_mode, _stallMinutes)]);

        Assert.Empty(_updates);
    }

    /// <summary>
    /// #1216. Retryfilm's download was removed by Deluno; its hand-off had been put back to waiting by a retry, and
    /// forty minutes later the waiting-too-long check raised "never picked up" for a download that no longer existed.
    /// </summary>
    [Fact]
    public async Task A_waiting_hand_off_whose_download_was_removed_raises_nothing_when_the_wait_runs_out()
    {
        GivenWaitingHandoff(updated: Now.AddMinutes(-41));
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.FindOpenDispatchesByHashAsync("qbittorrent", "hash-1", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("qbittorrent", "hash-1", It.IsAny<CancellationToken>())).ReturnsAsync(ArchivedDispatch());

        await new ProcessorHandoffWatcher(_repository.Object, _processor.Object, _activity.Object, _movies.Object, _series.Object, null, dispatches.Object)
            .WatchAsync([Library(_mode, _stallMinutes)], Now, CancellationToken.None);

        Assert.DoesNotContain(_updates, update => update.Status == ProcessorHandoffStatuses.Failed);
        Assert.All(_updates, update => Assert.Null(update.Message));
        _activity.Verify(feed => feed.RecordActivityAsync("processing.waiting-too-long", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_waiting_hand_off_whose_download_is_still_there_is_still_reported_when_the_wait_runs_out()
    {
        GivenWaitingHandoff(updated: Now.AddMinutes(-41));
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.FindOpenDispatchesByHashAsync("qbittorrent", "hash-1", It.IsAny<CancellationToken>())).ReturnsAsync([ArchivedDispatch()]);

        await new ProcessorHandoffWatcher(_repository.Object, _processor.Object, _activity.Object, _movies.Object, _series.Object, null, dispatches.Object)
            .WatchAsync([Library(_mode, _stallMinutes)], Now, CancellationToken.None);

        var failed = Assert.Single(_updates, update => update.Status == ProcessorHandoffStatuses.Failed);
        Assert.Contains("never picked up", failed.Message);
    }

    private void GivenWaitingHandoff(DateTimeOffset updated)
    {
        _mode = "block";
        _repository.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-1", "library-1", "movies", "qbittorrent", "hash-1", "Retryfilm.2017.1080p",
                    @"D:\Downloads\Retryfilm.2017.1080p", "Weir", ProcessorHandoffStatuses.Waiting, null, null, null,
                    updated.AddHours(-1), updated)
            ]);
    }

    private static Deluno.Jobs.Contracts.DownloadDispatchItem ArchivedDispatch()
        => new("dispatch-1", "library-1", "movies", "movie", "movie-1", "Retryfilm.2017.1080p", "Indexer",
            "qbittorrent", "qBittorrent", "archived", null, Now.AddHours(-2),
            "succeeded", null, null, null, null, null,
            null, "hash-1", null,
            null, null, null, null, null, null,
            null);

    private ProcessorHandoffWatcher Watcher()
        => new(_repository.Object, _processor.Object, _activity.Object, _movies.Object, _series.Object);

    private Task Watch()
        => new ProcessorHandoffWatcher(_repository.Object, _processor.Object, _activity.Object, _movies.Object, _series.Object)
            .WatchAsync([Library(_mode, _stallMinutes)], Now, CancellationToken.None);

    private string _mode = "import-original";
    private int _stallMinutes = ProcessorStallLimits.DefaultMinutes;

    private void GivenHandoff(
        DateTimeOffset updated,
        ProcessorHandoffObservation? previous,
        string mode = "import-original",
        int stallMinutes = ProcessorStallLimits.DefaultMinutes,
        DateTimeOffset? created = null,
        string? outputPath = null)
    {
        _mode = mode;
        _stallMinutes = stallMinutes;
        _repository.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-1", "library-1", "movies", "qbittorrent", "hash-1", "Sintel.2010.1080p",
                    @"D:\Downloads\Sintel.2010.1080p", "Weir", ProcessorHandoffStatuses.Started, outputPath, null, null,
                    created ?? updated.AddHours(-1), updated, previous)
            ]);
    }

    private void CanAsk(bool status, bool cancel)
    {
        _processor.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, status, cancel, null));
        _processor.Setup(service => service.CancelHandoffAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffCancelResult(ProcessorAnswer.Answered, null));
    }

    private void Answers(string state, DateTimeOffset lastChanged, int? queuePosition = null, DateTimeOffset? scheduledFor = null, string? message = null)
        => _processor.Setup(service => service.GetHandoffStatusAsync(It.IsAny<ProcessorConnectionItem>(), "handoff-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorHandoffStatusResult(ProcessorAnswer.Answered, state, queuePosition, scheduledFor, lastChanged, null, message));

    private static ProcessorHandoffObservation Answering(DateTimeOffset since)
        => new(ProcessorHandoffStates.Queued, null, null, null, since, Now.AddMinutes(-5), null, since);

    private static LibraryItem Library(string mode, int stallMinutes)
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return library with
        {
            Id = "library-1",
            Name = "Movies",
            MediaType = "movies",
            ImportWorkflow = "refine-before-import",
            ProcessorName = "Weir",
            ProcessorTimeoutMinutes = stallMinutes,
            ProcessorFailureMode = mode
        };
    }
}
