using System.Globalization;
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
/// How a hand-off ends, and what is cleaned when it ends badly, when Weir's own recorded answers (tests/fixtures/recorded/weir) are
/// what the watcher reads: no change for the library's limit, a file Weir finished that never arrived, a run Weir has already started,
/// Weir saying it failed, and a hand-off held for days. The real client reads each recorded reply and the real watcher acts on it; the
/// time is moved on from Weir's own "last changed" instant, and the files are real files in a real temp folder.
/// </summary>
public sealed class WeirContractWatcherEndingsTests : IDisposable
{
    private readonly Mock<IProcessorRepository> _repository = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly List<(string Status, string? Message)> _updates = [];
    private readonly List<ProcessorHandoffObservation> _observations = [];
    private readonly List<string> _cases = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "weir-endings-" + Guid.NewGuid().ToString("N"));

    public WeirContractWatcherEndingsTests()
    {
        Directory.CreateDirectory(_folder);
        _repository.Setup(r => r.FindProcessorConnectionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Connection);
        _repository.Setup(r => r.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, CancellationToken>((_, status, _, _, message, _) => _updates.Add((status, message)))
            .ReturnsAsync((ProcessorHandoffItem?)null);
        _repository.Setup(r => r.RecordProcessorObservationAsync(It.IsAny<string>(), It.IsAny<ProcessorHandoffObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProcessorHandoffObservation, CancellationToken>((_, observation, _) => _observations.Add(observation))
            .Returns(Task.CompletedTask);
        _activity.Setup(a => a.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActivityEventItem)null!);
        _movies.Setup(m => m.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateMovieImportRecoveryCaseRequest, CancellationToken>((request, _) => _cases.Add(request.FailureKind!))
            .ReturnsAsync((MovieImportRecoveryCase)null!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The waiting limit is judged on a hand-off Weir has left plainly queued. rc.18 records its queued state with the words of an
    /// earlier cancel on it (the hand-off was cancelled and sent again), which is a different situation, so these tests read the rc.13
    /// recording they were written against, as the same waiting-limit tests in WeirContractBehaviourTests do.
    /// </summary>
    private static Recording QueuedRc13 => RecordedReplies.Load("weir", "1.0.0-rc.13", "status-queued");

    private static DateTimeOffset ChangedAt(Recording recording)
        => DateTimeOffset.Parse(Body(recording.Exchanges[0].Reply.Body)["lastChangedUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static ProcessorHandoffObservation Answering(DateTimeOffset now, TimeSpan since)
        => new(ProcessorHandoffStates.Queued, null, null, null, now - since, now - TimeSpan.FromMinutes(5), null, now - since);

    /// <summary>
    /// Watches one hand-off. <paramref name="sinceWeirChanged"/> is how long after Weir's recorded "last changed" the watcher looks;
    /// <paramref name="heldFor"/> is how long Deluno has had the hand-off.
    /// </summary>
    private async Task<WeirReplay> WatchAsync(
        Recording status,
        TimeSpan sinceWeirChanged,
        TimeSpan heldFor,
        string mode,
        bool answeringForTheWholeHold = false,
        string? outputPath = null,
        string? libraryOutput = null,
        Recording? cancel = null)
    {
        var now = ChangedAt(status) + sinceWeirChanged;
        var handoffId = HandoffIdOf(status);
        var replay = cancel is null
            ? new WeirReplay(Rec("capabilities"), status)
            : new WeirReplay(Rec("capabilities"), status, cancel) { IgnoreIds = true };
        _repository.Setup(r => r.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    handoffId, "library-1", "movies", "qbittorrent", "hash-1", "Sintel.2010.1080p",
                    "/downloads/completed/Sintel.2010.1080p", "Weir", ProcessorHandoffStatuses.Started, outputPath, null, null,
                    now - heldFor, now - (answeringForTheWholeHold ? heldFor : sinceWeirChanged),
                    answeringForTheWholeHold ? Answering(now, heldFor) : null)
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
            ProcessorFailureMode = mode,
            ProcessorOutputPath = libraryOutput
        };
        await new ProcessorHandoffWatcher(_repository.Object, Service(replay), _activity.Object, _movies.Object, _series.Object)
            .WatchAsync([library], now, CancellationToken.None);
        return replay;
    }

    [Fact]
    public async Task No_change_from_Weir_for_the_limit_imports_the_original_when_that_is_the_library_rule_after_asking_Weir_to_drop_it()
    {
        var replay = await WatchAsync(QueuedRc13, TimeSpan.FromHours(49), TimeSpan.FromDays(3), "import-original", answeringForTheWholeHold: true, cancel: Rec("handoff-cancelled"));

        Assert.Contains(replay.Sent, request => request.Method == "DELETE");
        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Released, status);
        Assert.Contains("no change for 2 days", message, StringComparison.Ordinal);
        Assert.Empty(_cases);
    }

    [Theory]
    [InlineData("manual-review")]
    [InlineData("block")]
    public async Task No_change_from_Weir_for_the_limit_leaves_it_for_a_person_under_the_other_rules(string mode)
    {
        await WatchAsync(QueuedRc13, TimeSpan.FromHours(49), TimeSpan.FromDays(3), mode, answeringForTheWholeHold: true, cancel: Rec("handoff-cancelled"));

        Assert.Equal(ProcessorHandoffStatuses.Stalled, Assert.Single(_updates).Status);
        Assert.Equal("processor-stalled", Assert.Single(_cases));
    }

    [Fact]
    public async Task A_file_Weir_finished_that_never_arrived_ends_at_the_limit_in_Weirs_own_finish_time()
    {
        await WatchAsync(Rec("status-completed"), TimeSpan.FromHours(49), TimeSpan.FromDays(3), "block");

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
        Assert.Contains("Weir finished Sintel.2010.1080p 2 days ago, but no processed file arrived", message, StringComparison.Ordinal);
        Assert.Equal("processor-timeout", Assert.Single(_cases));
    }

    [Fact]
    public async Task A_file_Weir_has_just_finished_is_left_for_its_report_or_its_output_folder()
    {
        await WatchAsync(Rec("status-completed"), TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), "block");

        Assert.Empty(_updates);
        Assert.Equal("completed", Assert.Single(_observations).State);
    }

    [Fact]
    public async Task A_run_Weir_has_already_started_when_Deluno_asks_it_to_drop_the_file_is_left_to_finish()
    {
        var replay = await WatchAsync(QueuedRc13, TimeSpan.FromHours(49), TimeSpan.FromDays(3), "import-original", answeringForTheWholeHold: true, cancel: Rec("handoff-cancel-working"));

        Assert.Contains(replay.Sent, request => request.Method == "DELETE");
        Assert.Empty(_updates);
        Assert.Empty(_cases);
    }

    [Fact]
    public async Task Weir_working_with_no_change_for_days_is_said_once_and_never_interrupted()
    {
        var replay = await WatchAsync(Rec("status-working"), TimeSpan.FromHours(60), TimeSpan.FromDays(3), "import-original", answeringForTheWholeHold: true);

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Started, status);
        Assert.Contains("working on", message, StringComparison.Ordinal);
        Assert.DoesNotContain(replay.Sent, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task A_hand_off_Weir_reports_failed_has_its_crashed_partial_output_deleted_and_Weirs_words_kept()
    {
        var partial = Path.Combine(_folder, "Sintel.2010.1080p.mkv");
        File.WriteAllBytes(partial, []);

        await WatchAsync(Rec("status-failed"), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), "block", outputPath: partial);

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.Failed, status);
        Assert.Contains("Skipped because this file is 0.0 MB, under the 50 MB minimum.", message, StringComparison.Ordinal);
        Assert.Contains("Deleted the processor's partial output", message, StringComparison.Ordinal);
        Assert.False(File.Exists(partial));
        Assert.Equal("processor-failed", Assert.Single(_cases));
    }

    [Fact]
    public async Task Giving_up_on_a_hand_held_for_days_never_deletes_a_complete_stable_output()
    {
        var complete = Path.Combine(_folder, "Sintel.2010.1080p.mkv");
        File.WriteAllBytes(complete, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(complete, DateTime.UtcNow.AddMinutes(-10));

        await WatchAsync(Rec("status-queued"), TimeSpan.FromMinutes(5), TimeSpan.FromDays(9), "block", outputPath: complete);

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
        Assert.Contains("for 9 days", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Deleted the processor's partial output", message, StringComparison.Ordinal);
        Assert.True(File.Exists(complete));
    }

    [Fact]
    public async Task Giving_up_on_a_hand_off_held_for_days_deletes_its_crashed_partial_output()
    {
        var partial = Path.Combine(_folder, "Sintel.2010.1080p.mkv");
        File.WriteAllBytes(partial, []);

        await WatchAsync(Rec("status-queued"), TimeSpan.FromMinutes(5), TimeSpan.FromDays(9), "block", outputPath: partial);

        var (status, message) = Assert.Single(_updates);
        Assert.Equal(ProcessorHandoffStatuses.TimedOut, status);
        Assert.Contains("Deleted the processor's partial output", message, StringComparison.Ordinal);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task Giving_up_clears_the_scraps_and_the_now_empty_folder_of_this_hand_off_and_never_touches_a_sibling_hand_offs_folder()
    {
        var output = Path.Combine(_folder, "Ready");
        var own = Directory.CreateDirectory(Path.Combine(output, "Sintel.2010.1080p")).FullName;
        var scrap = Path.Combine(own, "Sintel.2010.1080p.mkv.partial");
        File.WriteAllText(scrap, "still writing");
        var siblingFolder = Directory.CreateDirectory(Path.Combine(output, "Arrival.2016.1080p")).FullName;
        var siblingFile = Path.Combine(siblingFolder, "Arrival.2016.1080p.mkv.partial");
        File.WriteAllText(siblingFile, "unrelated hand-off");

        await WatchAsync(Rec("status-queued"), TimeSpan.FromMinutes(5), TimeSpan.FromDays(9), "block", libraryOutput: output);

        Assert.Equal(ProcessorHandoffStatuses.TimedOut, Assert.Single(_updates).Status);
        Assert.False(File.Exists(scrap), "the leftover that was never renamed is cleared");
        Assert.False(Directory.Exists(own), "the folder goes once its only files are cleared");
        Assert.True(File.Exists(siblingFile), "a sibling hand-off's own folder is never touched");
    }
}
