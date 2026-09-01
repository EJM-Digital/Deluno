using Deluno.Filesystem;
using Deluno.Contracts;
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

public sealed class LibraryMediaProbeJobHandlerTests
{
    [Fact]
    public async Task HandleAsync_scopes_probe_writes_to_the_library_and_persists_measured_preference_facts()
    {
        var path = @"D:\Media\Movie.2020.1080p.WEB-DL.x264.DTS.2.0-GROUP.mkv";
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var library = Library("library-1", "profile-1");
        var profile = Profile("profile-1");
        var wanted = new MediaWantedItem(
            Id: "movie-1",
            Title: "Movie",
            Year: 2020,
            ImdbId: "tt0000001",
            LibraryId: library.Id,
            WantedStatus: "covered",
            WantedReason: "Imported",
            HasFile: true,
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            QualityCutoffMet: true,
            MissingSinceUtc: null,
            LastSearchUtc: null,
            NextEligibleSearchUtc: null,
            LastSearchResult: null,
            PreventLowerQualityReplacements: false,
            LastQualityDeltaDecision: null,
            UpdatedUtc: now,
            FilePath: path);

        var mediaState = new Mock<IMediaStateRepository>(MockBehavior.Strict);
        mediaState
            .Setup(repository => repository.ListFileProbeCandidatesAsync(
                MediaKind.Movie,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.Is<IReadOnlyList<MediaPreferencePlanExpectation>>(plans =>
                    plans.Count == 1
                    && plans[0].LibraryId == library.Id)))
            .ReturnsAsync([new MediaFileProbeCandidate("movie-1", path, 1_000, library.Id)]);
        mediaState
            .Setup(repository => repository.ListWantedByIdsAsync(
                MediaKind.Movie,
                It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "movie-1" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([wanted]);
        mediaState
            .Setup(repository => repository.UpdateProbedFileFactsAsync(
                MediaKind.Movie,
                "movie-1",
                path,
                It.Is<ProbedFileFacts>(facts =>
                    facts.VideoCodec == "HEVC"
                    && facts.AudioCodec == "TrueHD"
                    && facts.AudioChannels == "5.1"),
                It.IsAny<CancellationToken>(),
                library.Id))
            .Returns(Task.CompletedTask);
        mediaState
            .Setup(repository => repository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Movie,
                "movie-1",
                library.Id,
                 null,
                 It.IsAny<CancellationToken>(),
                 path,
                 1000))
            .ReturnsAsync((PreferenceEvaluationSnapshot?)null);

        PreferenceEvaluationSnapshot? saved = null;
        mediaState
            .Setup(repository => repository.SavePreferenceEvaluationSnapshotAsync(
                MediaKind.Movie,
                It.IsAny<PreferenceEvaluationSnapshot>(),
                It.IsAny<CancellationToken>()))
            .Callback<MediaKind, PreferenceEvaluationSnapshot, CancellationToken>((_, snapshot, _) => saved = snapshot)
            .Returns(Task.CompletedTask);

        var probe = new Mock<IMediaProbeService>(MockBehavior.Strict);
        probe
            .Setup(service => service.ProbeAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeInfo(
                Status: "succeeded",
                Tool: "ffprobe",
                Message: null,
                DurationSeconds: 120,
                Container: "matroska",
                Bitrate: null,
                VideoStreams: [new MediaVideoStreamInfo(0, "hevc", null, 1920, 1080, null, null, null, null)],
                AudioStreams: [new MediaAudioStreamInfo(1, "truehd", null, 6, "5.1", null, null, null)],
                SubtitleStreams: []));

        var libraries = new Mock<ILibrariesRepository>(MockBehavior.Strict);
        libraries
            .Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([library]);

        var quality = new Mock<IQualityRepository>(MockBehavior.Strict);
        quality
            .Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([profile]);
        quality
            .Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        var handler = new LibraryMediaProbeJobHandler(
            mediaState.Object,
            probe.Object,
            scheduler.Object,
            NullLogger<LibraryMediaProbeJobHandler>.Instance,
            TimeProvider.System,
            libraries.Object,
            quality.Object,
            new Mock<IReleasePreferencePlanRepository>(MockBehavior.Strict).Object);

        var result = await handler.HandleAsync(
            TestJobs.Create("library.media.probe", relatedEntityType: "movies"),
            CancellationToken.None);

        Assert.Equal("Read 1 file.", result);
        Assert.NotNull(saved);
        Assert.Equal(library.Id, saved!.LibraryId);
        Assert.Equal(1_000, saved.FileSizeBytes);
        Assert.Contains(saved.Facts, fact =>
            fact.TraitId.Equals("video.codec.hevc", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "media-probe");
        Assert.Contains(saved.Facts, fact => fact.TraitId.Equals("audio.format.truehd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(saved.Facts, fact => fact.TraitId.Equals("audio.channels.5-1", StringComparison.OrdinalIgnoreCase));
        mediaState.VerifyAll();
    }

    private static LibraryItem Library(string id, string profileId)
        => new(
            Id: id,
            Name: "Movies",
            MediaType: "movies",
            Purpose: "Movie collection",
            RootPath: @"D:\Media",
            DownloadsPath: null,
            QualityProfileId: profileId,
            QualityProfileName: "Home theatre",
            CutoffQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            ImportWorkflow: "direct",
            ProcessorName: null,
            ProcessorOutputPath: null,
            ProcessorTimeoutMinutes: 60,
            ProcessorFailureMode: "hold",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalHours: 6,
            RetryDelayHours: 12,
            MaxItemsPerRun: 50,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            AutomationStatus: "active",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static QualityProfileItem Profile(string id)
        => new(
            Id: id,
            Name: "Home theatre",
            MediaType: "movies",
            CutoffQuality: "WEB 1080p",
            AllowedQualities: "WEB 1080p",
            CustomFormatIds: string.Empty,
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            AllowLowerQualityReplacements: false,
            PresetId: null,
            PresetVersion: null,
            PresetDrifted: false,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);
}
