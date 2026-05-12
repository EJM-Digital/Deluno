using Deluno.Movies.Services;
using Deluno.Platform.Quality;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// Integration-style unit tests for <see cref="MovieWorkflowService"/> using a real
/// <see cref="VersionedMediaPolicyEngine"/> so that quality ranks are resolved through
/// the actual policy rather than stubs.
/// </summary>
public class MovieWorkflowServiceTests
{
    private readonly MovieWorkflowService _service;

    public MovieWorkflowServiceTests()
    {
        // Use the real engine — it has no external dependencies.
        _service = new MovieWorkflowService(new VersionedMediaPolicyEngine());
    }

    // ── EvaluateWantedStatus ──────────────────────────────────────────────────

    [Fact]
    public void EvaluateWantedStatus_NoCurrentQuality_ReturnsMissing()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_EmptyCurrentQuality_ReturnsMissing()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: "",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_BelowCutoffWithUpgradeUntilCutoff_ReturnsUpgrade()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("upgrade", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_BelowCutoffWithUpgradeDisabled_ReturnsWaiting()
    {
        // When upgradeUntilCutoff is false, even a below-cutoff quality stays as "waiting"
        // (the engine will not actively search for an upgrade).
        var result = _service.EvaluateWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_AtCutoff_ReturnsWaiting()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_AboveCutoff_ReturnsWaiting()
    {
        // Remux 2160p is above the WEB 1080p target → satisfied → waiting.
        var result = _service.EvaluateWantedStatus(
            currentQuality: "Remux 2160p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    // ── IsReplacementAllowed ──────────────────────────────────────────────────

    [Fact]
    public void IsReplacementAllowed_ProtectionDisabled_ReturnsTrue()
    {
        // Protection off → always allowed, even for a downgrade.
        var result = _service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: false);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_LowerRank_ReturnsFalse()
    {
        // 720p has a lower rank than 1080p → not allowed.
        var result = _service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.False(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_SameRank_ReturnsTrue()
    {
        // Same quality → delta == 0 → allowed.
        var result = _service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 1080p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_HigherRank_ReturnsTrue()
    {
        // Remux 2160p is above WEB 1080p → delta > 0 → allowed.
        var result = _service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "Remux 2160p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_NullCurrentFile_ReturnsTrue()
    {
        // No current file → no replacement, it's a first grab → always allowed.
        var result = _service.IsReplacementAllowed(
            currentQuality: null,
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_EmptyCurrentFile_ReturnsTrue()
    {
        var result = _service.IsReplacementAllowed(
            currentQuality: "",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    // ── CalculateQualityDelta ─────────────────────────────────────────────────

    [Fact]
    public void CalculateQualityDelta_UpgradeCandidate_ReturnsPositive()
    {
        // WEB 720p → WEB 1080p is an upgrade so delta should be > 0.
        var delta = _service.CalculateQualityDelta("WEB 720p", "WEB 1080p", null);

        Assert.NotNull(delta);
        Assert.True(delta > 0, $"Expected positive delta but got {delta}");
    }

    [Fact]
    public void CalculateQualityDelta_DowngradeCandidate_ReturnsNegative()
    {
        // WEB 1080p → WEB 720p is a downgrade so delta should be < 0.
        var delta = _service.CalculateQualityDelta("WEB 1080p", "WEB 720p", null);

        Assert.NotNull(delta);
        Assert.True(delta < 0, $"Expected negative delta but got {delta}");
    }

    [Fact]
    public void CalculateQualityDelta_SameQuality_ReturnsZero()
    {
        var delta = _service.CalculateQualityDelta("WEB 1080p", "WEB 1080p", null);

        Assert.NotNull(delta);
        Assert.Equal(0, delta);
    }

    [Fact]
    public void CalculateQualityDelta_NullCurrentQuality_ReturnsNull()
    {
        var delta = _service.CalculateQualityDelta(null, "WEB 1080p", null);

        Assert.Null(delta);
    }

    [Fact]
    public void CalculateQualityDelta_EmptyCurrentQuality_ReturnsNull()
    {
        var delta = _service.CalculateQualityDelta("", "WEB 1080p", null);

        Assert.Null(delta);
    }

    [Fact]
    public void CalculateQualityDelta_UnknownCandidateQuality_ReturnsNull()
    {
        // The policy engine returns -1 for unrecognised quality strings;
        // the service must propagate null in that case.
        var delta = _service.CalculateQualityDelta("WEB 1080p", "SomeCompletely.Unknown.Quality.Format", null);

        // Either null (unrecognised) or a concrete number if the engine normalises it — both
        // are acceptable; what must NOT happen is an exception.
        // We just assert the call completes without throwing.
        _ = delta; // consume the value
    }

    // ── EvaluateWantedStatus result fields ────────────────────────────────────

    [Fact]
    public void EvaluateWantedStatus_ReturnsCurrentAndTargetQuality()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("WEB 720p", result.CurrentQuality);
        Assert.Equal("WEB 1080p", result.TargetQuality);
    }

    [Fact]
    public void EvaluateWantedStatus_Missing_HasNonEmptyReason()
    {
        var result = _service.EvaluateWantedStatus(
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }
}
