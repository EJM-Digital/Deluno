using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// Which profile judges a probed file, and when none can: a pinned plan that is missing, a file whose library or title
/// the pass does not know, a title with a profile of its own and a library with none. In every case the file facts
/// are still written; only the installed-file snapshot waits.
/// </summary>
public sealed class LibraryMediaProbePreferenceContextTests
{
    private const string FilePath = "/media/Movie.2020.1080p.WEB-DL.x264.DTS.2.0-GROUP.mkv";

    private sealed record Outcome(int FactsWritten, List<PreferenceEvaluationSnapshot> Snapshots, string Result);

    [Fact]
    public async Task A_profile_whose_pinned_plan_is_missing_keeps_the_files_facts_and_waits_for_the_plan_to_be_repaired()
    {
        var pinned = Profile("profile-1") with { ReleasePreferencePlan = new ReleasePreferencePlanReference("plan-1", "1", "hash-1") };
        var plans = new Mock<IReleasePreferencePlanRepository>();

        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [pinned], plans: plans.Object);

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    [Fact]
    public async Task A_file_in_a_library_the_pass_does_not_know_is_probed_and_gets_no_snapshot()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [Profile("profile-1")], candidateLibraryId: "library-gone");

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    [Fact]
    public async Task A_file_with_no_library_is_probed_and_gets_no_snapshot()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [Profile("profile-1")], candidateLibraryId: null);

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    [Fact]
    public async Task A_title_the_wanted_list_does_not_hold_is_probed_and_gets_no_snapshot()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [Profile("profile-1")], holdsWanted: false);

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    [Fact]
    public async Task A_title_with_a_profile_of_its_own_is_judged_by_that_profile_not_its_librarys()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [Profile("profile-1"), Profile("profile-2")], ownProfileId: "profile-2");

        var snapshot = Assert.Single(outcome.Snapshots);
        Assert.Equal(ReleasePreferencePlanFactory.CreateQualityPlan(Profile("profile-2"), []).Id, snapshot.PlanId);
    }

    [Fact]
    public async Task A_title_whose_own_profile_no_longer_exists_is_judged_by_its_librarys()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-1", profiles: [Profile("profile-1")], ownProfileId: "profile-deleted");

        var snapshot = Assert.Single(outcome.Snapshots);
        Assert.Equal(ReleasePreferencePlanFactory.CreateQualityPlan(Profile("profile-1"), []).Id, snapshot.PlanId);
    }

    [Fact]
    public async Task A_library_with_no_profile_and_a_title_with_none_is_probed_and_gets_no_snapshot()
    {
        var outcome = await RunAsync(libraryProfileId: "", profiles: [Profile("profile-1")]);

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    [Fact]
    public async Task A_library_whose_profile_is_not_in_the_profile_list_is_probed_and_gets_no_snapshot()
    {
        var outcome = await RunAsync(libraryProfileId: "profile-gone", profiles: [Profile("profile-1")]);

        Assert.Equal(1, outcome.FactsWritten);
        Assert.Empty(outcome.Snapshots);
    }

    private static async Task<Outcome> RunAsync(
        string libraryProfileId,
        IReadOnlyList<QualityProfileItem> profiles,
        string? candidateLibraryId = "library-1",
        bool holdsWanted = true,
        string? ownProfileId = null,
        IReleasePreferencePlanRepository? plans = null)
    {
        var library = Library("library-1", libraryProfileId);
        var wanted = new MediaWantedItem(
            Id: "movie-1", Title: "Movie", Year: 2020, ImdbId: "tt0000001", LibraryId: library.Id,
            WantedStatus: "covered", WantedReason: "Imported", HasFile: true, CurrentQuality: "WEB 1080p", TargetQuality: "WEB 1080p",
            QualityCutoffMet: true, MissingSinceUtc: null, LastSearchUtc: null, NextEligibleSearchUtc: null, LastSearchResult: null,
            PreventLowerQualityReplacements: false, LastQualityDeltaDecision: null, UpdatedUtc: DateTimeOffset.UnixEpoch, FilePath: FilePath);

        var factsWritten = 0;
        var snapshots = new List<PreferenceEvaluationSnapshot>();
        var mediaState = new Mock<IMediaStateRepository>();
        mediaState
            .Setup(repository => repository.ListFileProbeCandidatesAsync(MediaKind.Movie, It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<MediaPreferencePlanExpectation>>()))
            .ReturnsAsync([new MediaFileProbeCandidate("movie-1", FilePath, 1_000, candidateLibraryId)]);
        mediaState
            .Setup(repository => repository.ListWantedByIdsAsync(MediaKind.Movie, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(holdsWanted ? [wanted] : []);
        mediaState
            .Setup(repository => repository.GetTitleQualityProfileIdAsync(MediaKind.Movie, "movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ownProfileId);
        mediaState
            .Setup(repository => repository.UpdateProbedFileFactsAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProbedFileFacts>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback(() => factsWritten++)
            .Returns(Task.CompletedTask);
        mediaState
            .Setup(repository => repository.SavePreferenceEvaluationSnapshotAsync(It.IsAny<MediaKind>(), It.IsAny<PreferenceEvaluationSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<MediaKind, PreferenceEvaluationSnapshot, CancellationToken>((_, snapshot, _) => snapshots.Add(snapshot))
            .Returns(Task.CompletedTask);

        var probe = new Mock<IMediaProbeService>();
        probe
            .Setup(service => service.ProbeAsync(FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeInfo(
                "succeeded", "ffprobe", null, 120, "matroska", null,
                [new MediaVideoStreamInfo(0, "hevc", null, 1920, 1080, null, null, null, null)],
                [new MediaAudioStreamInfo(1, "truehd", null, 6, "5.1", null, null, null)],
                []));

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var quality = new Mock<IQualityRepository>();
        quality.Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
        quality.Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var handler = new LibraryMediaProbeJobHandler(
            mediaState.Object,
            probe.Object,
            new Mock<IJobScheduler>().Object,
            NullLogger<LibraryMediaProbeJobHandler>.Instance,
            TimeProvider.System,
            libraries.Object,
            quality.Object,
            plans ?? new Mock<IReleasePreferencePlanRepository>().Object);

        var result = await handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "movies"), CancellationToken.None);
        return new Outcome(factsWritten, snapshots, result);
    }

    private static LibraryItem Library(string id, string profileId)
        => new(
            Id: id, Name: "Movies", MediaType: "movies", Purpose: "Movie collection", RootPath: "/media", DownloadsPath: null,
            QualityProfileId: profileId, QualityProfileName: "Home theatre", CutoffQuality: "WEB 1080p", UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true, ImportWorkflow: "direct", ProcessorName: null, ProcessorOutputPath: null,
            ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold", AutoSearchEnabled: true, MissingSearchEnabled: true,
            UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 12, MaxItemsPerRun: 50,
            SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active", SearchRequested: false,
            LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static QualityProfileItem Profile(string id)
        => new(
            Id: id, Name: "Home theatre", MediaType: "movies", CutoffQuality: "WEB 1080p", AllowedQualities: "WEB 1080p",
            CustomFormatIds: string.Empty, UpgradeUntilCutoff: true, UpgradeUnknownItems: true, AllowLowerQualityReplacements: false,
            PresetId: null, PresetVersion: null, PresetDrifted: false, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch);
}
