using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadHealthEvaluatorTests
{
    private static readonly DateTimeOffset CapturedUtc = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly IRecoveryHealthEvaluator Evaluator = new DownloadHealthEvaluator();

    [Fact]
    public void Evaluate_StalledClientItem_ExplainsTheReversibleFirstAction()
    {
        var findings = Evaluate(CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "Tracker timed out."));

        var finding = Assert.Single(findings);
        Assert.Equal("client-stalled", finding.Kind);
        Assert.Equal("critical", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
        Assert.Contains("Tracker timed out", finding.Evidence);
    }

    /// <summary>#880/#881: a local setup problem never strikes, so it must be its own Kind, never "client-stalled".</summary>
    [Fact]
    public void Evaluate_SetupProblem_IsItsOwnKindAndNeverCountsAStrike()
    {
        var findings = Evaluate(CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "qBittorrent reports this torrent's data is missing from disk.",
            isSetupProblem: true));

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.ClientSetupProblem, finding.Kind);
        Assert.False(DownloadHealthStrikes.Counts(finding.Kind));
        Assert.Equal("critical", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
        Assert.Contains("machine", finding.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same stalled/errored item, without the client's setup-problem signal, keeps the old strike-counting behavior.</summary>
    [Fact]
    public void Evaluate_StalledClientItem_WithoutSetupSignal_StillCountsAsClientStalled()
    {
        var findings = Evaluate(CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "No seeds found.",
            isSetupProblem: false));

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.ClientStalled, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Evaluate_NoThroughputAfterThirtyMinutes_FlagsAReviewWithoutRemoval()
    {
        var findings = Evaluate(CreateItem(
            addedUtc: CapturedUtc.AddMinutes(-31),
            speedMbps: 0));

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.NoThroughput, finding.Kind);
        Assert.Equal("warning", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
    }

    [Fact]
    public void Evaluate_ImportReadyWithoutSourcePath_ExplainsWhyManualImportIsNotSafeYet()
    {
        var findings = Evaluate(CreateItem(
            status: DownloadQueueStatuses.ImportReady,
            sourcePath: null));

        var finding = Assert.Single(findings);
        Assert.Equal("missing-import-path", finding.Kind);
        Assert.Contains("path mapping", finding.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #816: with the content blocklist off (its default, #936), a
    /// suspicious release name is no longer flagged on its own - the
    /// release-name check now only runs as the blocklist's own fallback when
    /// it is turned on and no file listing is available.
    /// </summary>
    [Fact]
    public void Evaluate_SuspiciousReleaseName_ContentBlocklistOff_HasNoFinding()
    {
        var findings = Evaluate(CreateItem(releaseName: "Example.Movie.2026.exe"));

        Assert.Empty(findings);
    }

    /// <summary>#816: turned on, with no file listing available, the same suspicious release name is caught by matching the name itself against the built-in patterns.</summary>
    [Fact]
    public void Evaluate_SuspiciousReleaseName_ContentBlocklistOn_NoFileListing_FallsBackToReleaseName()
    {
        var findings = Evaluate(
            CreateItem(releaseName: "Example.Movie.2026.exe"),
            contentBlocklist: ContentBlocklistSettings.Default with { Enabled = true });

        var finding = Assert.Single(findings);
        Assert.Equal("content-blocklist-match", finding.Kind);
        Assert.False(finding.CanSafelyRetry);
        Assert.True(finding.CanSafelyRemove);
        Assert.Contains("Example.Movie.2026.exe", finding.Evidence);
    }

    /// <summary>#816: a clean release name with a dangerous file packaged inside it is caught by checking the files themselves, not the release name.</summary>
    [Fact]
    public void Evaluate_CleanReleaseName_DangerousFileInside_IsCaughtByFileListing()
    {
        var findings = Evaluate(
            CreateItem(releaseName: "Example.Movie.2026.1080p.mkv"),
            files: ["Example.Movie.2026.1080p.mkv", "Setup.exe"],
            contentBlocklist: ContentBlocklistSettings.Default with { Enabled = true });

        var finding = Assert.Single(findings);
        Assert.Equal("content-blocklist-match", finding.Kind);
        Assert.Contains("Setup.exe", finding.Evidence);
    }

    /// <summary>#816 whitelist mode: any file not covered by an allow pattern is the offender.</summary>
    [Fact]
    public void Evaluate_WhitelistMode_FileNotOnTheAllowListIsTheOffender()
    {
        var findings = Evaluate(
            CreateItem(releaseName: "Example.Movie.2026.1080p.mkv"),
            files: ["Example.Movie.2026.1080p.mkv", "readme.exe"],
            contentBlocklist: new ContentBlocklistSettings(
                Enabled: true,
                Mode: ContentBlocklistModes.Whitelist,
                UseBuiltInPatterns: false,
                CustomPatterns: ["*.mkv"],
                RemoteUrl: null,
                RemotePatterns: [],
                LastRefreshedUtc: null,
                UpdatedUtc: DateTimeOffset.MinValue));

        var finding = Assert.Single(findings);
        Assert.Equal("content-blocklist-match", finding.Kind);
        Assert.Contains("readme.exe", finding.Evidence);
    }

    /// <summary>#879: the same match only strikes (and can go on to act) once the dedicated dangerous-file rule is enabled - the passive #816 finding above never does on its own.</summary>
    [Fact]
    public void Evaluate_DangerousFileMatch_WithRuleEnabled_AlsoProducesAStrikingFinding()
    {
        var rules = new[]
        {
            new DownloadHealthRule(
                Id: "dangerous-file",
                Kind: DownloadHealthRuleKinds.DangerousFile,
                Enabled: true,
                StrikeLimit: 1,
                TimerMinutes: 1,
                AppliesTo: DownloadHealthAppliesTo.Both,
                MinCompletionPercent: 0,
                MaxCompletionPercent: 100,
                ResetStrikesOnProgress: true,
                ActionPreset: DownloadHealthActionPresets.Replace,
                CreatedUtc: DateTimeOffset.MinValue,
                UpdatedUtc: DateTimeOffset.MinValue)
        };

        var findings = Evaluate(
            CreateItem(releaseName: "Example.Movie.2026.1080p.mkv"),
            files: ["Example.Movie.2026.1080p.mkv", "Setup.exe"],
            contentBlocklist: ContentBlocklistSettings.Default with { Enabled = true },
            rules: rules);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.Kind == "content-blocklist-match");
        var strikingFinding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        Assert.Equal("dangerous-file", strikingFinding.RuleId);
        Assert.Equal(DownloadHealthActionPresets.Replace, strikingFinding.RuleActionPreset);
    }

    [Fact]
    public void Evaluate_HealthyActiveDownload_HasNoFinding()
    {
        var findings = Evaluate(CreateItem(
            addedUtc: CapturedUtc.AddMinutes(-5),
            speedMbps: 4.8,
            etaSeconds: 3600));

        Assert.Empty(findings);
    }

    [Fact]
    public void CleanupPreview_IsReadOnlyAndRedactsKnownPayloadPaths()
    {
        var item = CreateItem(
            status: DownloadQueueStatuses.Stalled,
            errorMessage: "Tracker timed out.",
            sourcePath: "D:\\downloads\\private\\example.mkv");
        var finding = Assert.Single(Evaluate(item));

        var preview = DownloadCleanupPreviewBuilder.Create(item with { HealthFindings = [finding] });

        Assert.False(preview.RemovalAllowed);
        Assert.False(preview.ReplacementSearchWillRun);
        Assert.True(preview.RequiresReview);
        Assert.Contains("observation", preview.MatchedPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", preview.AffectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", preview.AffectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual action", preview.ProposedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DownloadQueueStatuses.Downloading, true)]
    [InlineData(DownloadQueueStatuses.Completed, false)]
    [InlineData(DownloadQueueStatuses.ImportReady, false)]
    [InlineData(DownloadQueueStatuses.WaitingForProcessor, false)]
    [InlineData(DownloadQueueStatuses.Imported, false)]
    public void An_unusually_long_completion_time_is_only_a_finding_while_downloading(string status, bool flagged)
    {
        // qBittorrent's "never": what it reports for a finished torrent that is seeding (#523).
        var findings = Evaluate(CreateItem(status: status, etaSeconds: 8_640_000, speedMbps: 1));

        Assert.Equal(flagged, findings.Any(finding => finding.Kind == "excessive-eta"));
    }

    private static DownloadQueueItem CreateItem(
        string status = DownloadQueueStatuses.Downloading,
        string releaseName = "Example.Movie.2026.1080p.mkv",
        string? errorMessage = null,
        DateTimeOffset? addedUtc = null,
        double speedMbps = 1.2,
        int etaSeconds = 600,
        string? sourcePath = "D:\\downloads\\example.mkv",
        bool isSetupProblem = false)
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
            SourcePath: sourcePath,
            IsSetupProblem: isSetupProblem);

    private static IReadOnlyList<DownloadHealthFinding> Evaluate(
        DownloadQueueItem item,
        IReadOnlyList<string>? files = null,
        ContentBlocklistSettings? contentBlocklist = null,
        IReadOnlyList<DownloadHealthRule>? rules = null) =>
        Evaluator.Evaluate(
            new RecoveryQueueSnapshot(
                item.Status,
                item.ErrorMessage,
                item.SourcePath,
                item.SpeedMbps,
                item.AddedUtc,
                item.EtaSeconds,
                item.ReleaseName,
                item.Peers,
                item.IsSetupProblem,
                item.Progress,
                Files: files),
            CapturedUtc,
            rules ?? DownloadHealthRules.Defaults,
            contentBlocklist)
        .Select(finding => new DownloadHealthFinding(
            finding.Severity,
            finding.Kind,
            finding.Summary,
            finding.Evidence,
            finding.RecommendedAction,
            finding.CanSafelyRetry,
            finding.CanSafelyRemove,
            RuleId: finding.RuleId,
            RuleActionPreset: finding.RuleActionPreset))
        .ToArray();
}
