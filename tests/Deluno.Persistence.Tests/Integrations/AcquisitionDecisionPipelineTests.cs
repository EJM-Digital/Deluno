using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class AcquisitionDecisionPipelineTests
{
    [Fact]
    public async Task PlanAsync_blocks_when_no_sources_are_linked()
    {
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(null, [], "unused")));

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            null,
            "WEB 1080p",
            Sources: [],
            DownloadClients: [DownloadClient()]));

        Assert.Equal("blocked", plan.Outcome);
        Assert.False(plan.ShouldDispatch);
        Assert.Contains("No indexers", plan.SearchResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_uses_same_decision_for_preview_and_automatic_paths()
    {
        var candidate = Candidate(
            status: "preferred",
            meetsCutoff: true,
            qualityDelta: 1);
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate")));

        var request = new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 720p",
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()]);

        var automatic = await pipeline.PlanAsync(request);
        var preview = await pipeline.PlanAsync(request with { PreviewOnly = true });

        Assert.Equal("matched", automatic.Outcome);
        Assert.Equal(automatic.Outcome, preview.Outcome);
        Assert.Equal(Deluno.Platform.Quality.MediaPolicyCatalog.CurrentVersion, automatic.PolicyVersion);
        Assert.True(automatic.ShouldDispatch);
        Assert.False(preview.ShouldDispatch);
        Assert.NotNull(automatic.DispatchRequest);
        Assert.Equal(automatic.SearchResult, preview.SearchResult);
    }

    [Fact]
    public async Task PlanAsync_holds_candidate_that_is_not_safe_for_automatic_dispatch()
    {
        var candidate = Candidate(
            status: "eligible",
            meetsCutoff: false,
            qualityDelta: -1);
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(candidate, [candidate], "below cutoff")));

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()]));

        Assert.Equal("held", plan.Outcome);
        Assert.False(plan.ShouldDispatch);
        Assert.Contains("manual review", plan.SearchResult, StringComparison.OrdinalIgnoreCase);
        Assert.Single(plan.Alternatives);
    }

    [Fact]
    public void EvaluateSelectedRelease_requires_force_for_unsafe_manual_release()
    {
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(null, [], "")));

        var blocked = pipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
            "Movie.2024.CAM.sample-Group",
            null,
            "Indexer",
            "https://example.test/file.torrent",
            null,
            "WEB 1080p"));
        var forced = pipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
            "Movie.2024.CAM.sample-Group",
            null,
            "Indexer",
            "https://example.test/file.torrent",
            null,
            "WEB 1080p",
            ForceOverride: true,
            OverrideReason: "testing override"));

        Assert.False(blocked.CanDispatch);
        Assert.True(blocked.RequiresOverride);
        Assert.Equal(Deluno.Platform.Quality.MediaPolicyCatalog.CurrentVersion, blocked.PolicyVersion);
        Assert.True(forced.CanDispatch);
        Assert.True(forced.RequiresOverride);
    }

    private static MediaSearchCandidate Candidate(string status, bool meetsCutoff, int qualityDelta)
        => new(
            ReleaseName: "Dune.Part.Two.2024.1080p.WEB-DL-GRP",
            IndexerId: "idx",
            IndexerName: "Indexer",
            Quality: "WEB 1080p",
            Score: 1200,
            MeetsCutoff: meetsCutoff,
            Summary: "Candidate summary.",
            DownloadUrl: "https://example.test/file.torrent",
            SizeBytes: 4_000_000_000,
            Seeders: 20,
            DecisionStatus: status,
            DecisionReasons: ["reason"],
            RiskFlags: [],
            QualityDelta: qualityDelta);

    private static LibrarySourceLinkItem Source()
        => new(
            "source-link",
            "library",
            "indexer",
            "Indexer",
            10,
            "",
            "",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static LibraryDownloadClientLinkItem DownloadClient()
        => new(
            "client-link",
            "library",
            "client",
            "qBittorrent",
            10,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class StubPlanner(MediaSearchPlan plan) : IMediaSearchPlanner
    {
        public Task<MediaSearchPlan> BuildPlanAsync(
            string title,
            int? year,
            string mediaType,
            string? currentQuality,
            string? targetQuality,
            IReadOnlyList<LibrarySourceLinkItem> sources,
            IReadOnlyList<CustomFormatItem>? customFormats = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(plan);
    }
}
