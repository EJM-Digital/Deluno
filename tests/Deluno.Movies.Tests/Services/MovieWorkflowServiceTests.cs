using Deluno.Movies.Contracts;
using Deluno.Movies.Services;
using Deluno.Platform.Contracts;
using Deluno.Quality;

namespace Deluno.Movies.Tests.Services;

public class MovieWorkflowServiceTests
{
    private readonly MovieWorkflowService service;

    private readonly MovieWorkflowService realEngineService = new();

    public MovieWorkflowServiceTests()
    {
        service = new MovieWorkflowService();
    }

    [Fact]
    public void EvaluateWantedStatus_WithNoFile_ReturnsMissingStatus()
    {
        var result = service.EvaluateWantedStatus(
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
        Assert.Contains("looking", result.Reason.ToLower());
    }

    [Fact]
    public void EvaluateWantedStatus_WithFileAtCutoff_ReturnsWaitingStatus()
    {
        var result = service.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("covered", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_BelowCutoffWithUpgradeEnabled_ReturnsUpgradeStatus()
    {
        var result = service.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("upgrade", result.WantedStatus);
    }

    // ── Ported from tests/Deluno.Integrations.Tests/Search/MovieWorkflowServiceTests.cs ──
    // These use the real VersionedMediaPolicyEngine, matching that file's setup.

    [Fact]
    public void EvaluateWantedStatus_EmptyCurrentQuality_ReturnsMissing()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: false,
            currentQuality: "",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_BelowCutoffWithUpgradeDisabled_ReturnsWaiting()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("covered", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_AtCutoff_ReturnsWaiting()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("covered", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_AboveCutoff_ReturnsWaiting()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "Remux 2160p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("covered", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_ReturnsCurrentAndTargetQuality()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("WEB 720p", result.CurrentQuality);
        Assert.Equal("WEB 1080p", result.TargetQuality);
    }

    /// <summary>
    /// Seen on the physical rig: a film imported from an existing library, on
    /// disk with an unnamed quality, was reported as "still looking for this
    /// movie" because a known quality was taken as the proof of a file.
    /// </summary>
    [Fact]
    public void EvaluateWantedStatus_FileOnDiskWithUnknownQuality_IsNotMissing()
    {
        var kept = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("covered", kept.WantedStatus);
        Assert.DoesNotContain("looking", kept.Reason, StringComparison.OrdinalIgnoreCase);

        var upgraded = realEngineService.EvaluateWantedStatus(
            hasFile: true,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: true);

        Assert.Equal("upgrade", upgraded.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_Missing_HasNonEmptyReason()
    {
        var result = realEngineService.EvaluateWantedStatus(
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

}
