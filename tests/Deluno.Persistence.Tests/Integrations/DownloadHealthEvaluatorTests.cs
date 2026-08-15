using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadHealthEvaluatorTests
{
    private static readonly DateTimeOffset CapturedUtc = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_StalledClientItem_ExplainsTheReversibleFirstAction()
    {
        var findings = DownloadHealthEvaluator.Evaluate(CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "Tracker timed out."), CapturedUtc);

        var finding = Assert.Single(findings);
        Assert.Equal("client-stalled", finding.Kind);
        Assert.Equal("critical", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
        Assert.Contains("Tracker timed out", finding.Evidence);
    }

    [Fact]
    public void Evaluate_NoThroughputAfterThirtyMinutes_FlagsAReviewWithoutRemoval()
    {
        var findings = DownloadHealthEvaluator.Evaluate(CreateItem(
            addedUtc: CapturedUtc.AddMinutes(-31),
            speedMbps: 0), CapturedUtc);

        var finding = Assert.Single(findings);
        Assert.Equal("no-throughput", finding.Kind);
        Assert.Equal("warning", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
    }

    [Fact]
    public void Evaluate_ImportReadyWithoutSourcePath_ExplainsWhyManualImportIsNotSafeYet()
    {
        var findings = DownloadHealthEvaluator.Evaluate(CreateItem(
            status: DownloadQueueStatuses.ImportReady,
            sourcePath: null), CapturedUtc);

        var finding = Assert.Single(findings);
        Assert.Equal("missing-import-path", finding.Kind);
        Assert.Contains("path mapping", finding.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SuspiciousPayloadName_RequiresHumanVerification()
    {
        var findings = DownloadHealthEvaluator.Evaluate(CreateItem(releaseName: "Example.Movie.2026.exe"), CapturedUtc);

        var finding = Assert.Single(findings);
        Assert.Equal("suspicious-payload-name", finding.Kind);
        Assert.False(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
        Assert.Contains("Verify", finding.RecommendedAction);
    }

    [Fact]
    public void Evaluate_HealthyActiveDownload_HasNoFinding()
    {
        var findings = DownloadHealthEvaluator.Evaluate(CreateItem(
            addedUtc: CapturedUtc.AddMinutes(-5),
            speedMbps: 4.8,
            etaSeconds: 3600), CapturedUtc);

        Assert.Empty(findings);
    }

    [Fact]
    public void CleanupPreview_IsReadOnlyAndRedactsKnownPayloadPaths()
    {
        var item = CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "Tracker timed out.",
            sourcePath: "D:\\downloads\\private\\example.mkv");
        var finding = Assert.Single(DownloadHealthEvaluator.Evaluate(item, CapturedUtc));

        var preview = DownloadCleanupPreviewBuilder.Create(item with { HealthFindings = [finding] });

        Assert.False(preview.RemovalAllowed);
        Assert.False(preview.ReplacementSearchWillRun);
        Assert.True(preview.RequiresReview);
        Assert.Contains("observation", preview.MatchedPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", preview.AffectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", preview.AffectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual action", preview.ProposedAction, StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadQueueItem CreateItem(
        string status = DownloadQueueStatuses.Downloading,
        string releaseName = "Example.Movie.2026.1080p.mkv",
        string? errorMessage = null,
        DateTimeOffset? addedUtc = null,
        double speedMbps = 1.2,
        int etaSeconds = 600,
        string? sourcePath = "D:\\downloads\\example.mkv")
        => new(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Test client",
            Protocol: "qbittorrent",
            MediaType: "movies",
            Title: "Example Movie",
            ReleaseName: releaseName,
            Category: "movies",
            Status: status,
            Progress: 50,
            SpeedMbps: speedMbps,
            EtaSeconds: etaSeconds,
            SizeBytes: 1_000_000_000,
            DownloadedBytes: 500_000_000,
            Peers: 4,
            IndexerName: "Test indexer",
            ErrorMessage: errorMessage,
            AddedUtc: addedUtc ?? CapturedUtc.AddMinutes(-10),
            SourcePath: sourcePath);
}
