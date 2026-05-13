using Deluno.Integrations.Search;
using Moq;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// Tests for the replacement-protection logic inside
/// <see cref="AcquisitionDecisionPipeline.EvaluateSelectedRelease"/>.
/// </summary>
public class AcquisitionDecisionPipelineTests
{
    private readonly AcquisitionDecisionPipeline _pipeline;

    public AcquisitionDecisionPipelineTests()
    {
        // EvaluateSelectedRelease does not use IMediaSearchPlanner; mock it to satisfy the constructor.
        var mockPlanner = new Mock<IMediaSearchPlanner>();
        _pipeline = new AcquisitionDecisionPipeline(mockPlanner.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal request whose release name resolves to a recognised quality tier.
    /// "WEB.1080p" is detected as "WEB 1080p" (rank 70) by the policy engine.
    /// </summary>
    private static AcquisitionSelectedReleaseRequest BuildRequest(
        string releaseName = "Movie.2023.WEB.1080p-GROUP",
        string? currentQuality = null,
        string? targetQuality = "WEB 1080p",
        bool preventLowerQualityReplacements = false,
        bool forceOverride = false,
        string? overrideReason = null)
        => new(
            ReleaseName: releaseName,
            IndexerId: null,
            IndexerName: null,
            DownloadUrl: "https://example.com/movie.torrent",
            CurrentQuality: currentQuality,
            TargetQuality: targetQuality,
            SizeBytes: 8_000_000_000L,   // 8 GB – within 1080p expected range
            Seeders: 20,
            ForceOverride: forceOverride,
            OverrideReason: overrideReason,
            NeverGrabPatterns: null,
            PreventLowerQualityReplacements: preventLowerQualityReplacements);

    // ── PreventLowerQualityReplacements = false ───────────────────────────────

    [Fact]
    public void EvaluateSelectedRelease_ProtectionDisabled_AllowsDowngradeWithoutForceOverride()
    {
        // Candidate is 720p, current is 1080p — normally a downgrade.
        // With protection disabled the pipeline should NOT block it.
        // The release will be "held" (not "preferred") by the decision engine because it's a
        // downgrade; canDispatch requires ForceOverride in that case but protection is not involved.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: "WEB 1080p",
            preventLowerQualityReplacements: false,
            forceOverride: false);

        var result = _pipeline.EvaluateSelectedRelease(request);

        // replacementBlocked must be false when protection is disabled
        Assert.False(result.CanDispatch);             // held because downgrade, but NOT replacement-blocked
        Assert.DoesNotContain("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSelectedRelease_ProtectionDisabled_ForceOverrideBypassesHeldStatus()
    {
        // Protection is off; ForceOverride=true should make CanDispatch=true even if
        // the release would otherwise be held.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: "WEB 1080p",
            preventLowerQualityReplacements: false,
            forceOverride: true,
            overrideReason: "testing override");

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.True(result.CanDispatch);
        Assert.Contains("override", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── PreventLowerQualityReplacements = true ────────────────────────────────

    [Fact]
    public void EvaluateSelectedRelease_ProtectionEnabled_BlocksDowngradeEvenWithForceOverride()
    {
        // Replacement protection is a hard block — ForceOverride must NOT bypass it.
        // Candidate: 720p, Current: 1080p → QualityDelta < 0 → blocked.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: "WEB 1080p",
            preventLowerQualityReplacements: true,
            forceOverride: true,
            overrideReason: "trying to force a downgrade");

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.False(result.CanDispatch);
        Assert.Contains("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSelectedRelease_ProtectionEnabled_AllowsSameQualityGrab()
    {
        // Candidate quality matches current quality → QualityDelta = 0 → not a downgrade → allowed.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.1080p-GROUP",
            currentQuality: "WEB 1080p",
            preventLowerQualityReplacements: true,
            forceOverride: false);

        var result = _pipeline.EvaluateSelectedRelease(request);

        // QualityDelta == 0 so replacementBlocked is false.
        // The release is safe (preferred + meetsCutoff + delta >= 0) so CanDispatch = true.
        Assert.True(result.CanDispatch);
        Assert.DoesNotContain("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSelectedRelease_ProtectionEnabled_AllowsUpgrade()
    {
        // Candidate: Remux 2160p (higher rank), Current: WEB 1080p → QualityDelta > 0 → allowed.
        var request = BuildRequest(
            releaseName: "Movie.2023.Remux.2160p-GROUP",
            currentQuality: "WEB 1080p",
            targetQuality: "Remux 2160p",
            preventLowerQualityReplacements: true,
            forceOverride: false);
        // Adjust size to something plausible for a Remux 2160p (40 GB)
        request = request with { SizeBytes = 40_000_000_000L };

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.False(result.CanDispatch == false && result.Reason.Contains("Replacement protection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSelectedRelease_ProtectionEnabled_NoCurrentFile_AllowsGrab()
    {
        // No current file (CurrentQuality is empty) → replacementBlocked = false regardless of delta.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: "",          // empty = no current file
            preventLowerQualityReplacements: true,
            forceOverride: false);

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.DoesNotContain("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSelectedRelease_ProtectionEnabled_NullCurrentFile_AllowsGrab()
    {
        // Null current quality → no file → replacementBlocked = false.
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: null,
            preventLowerQualityReplacements: true,
            forceOverride: false);

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.DoesNotContain("Replacement protection", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── RequiresOverride flag ─────────────────────────────────────────────────

    [Fact]
    public void EvaluateSelectedRelease_ReplacementBlocked_RequiresOverrideIsFalse()
    {
        // When replacementBlocked the pipeline sets RequiresOverride = false
        // (the user must disable protection on the item, not just click "force grab").
        var request = BuildRequest(
            releaseName: "Movie.2023.WEB.720p-GROUP",
            currentQuality: "WEB 1080p",
            preventLowerQualityReplacements: true,
            forceOverride: false);

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.False(result.CanDispatch);
        Assert.False(result.RequiresOverride);
    }

    // ── Candidate metadata ────────────────────────────────────────────────────

    [Fact]
    public void EvaluateSelectedRelease_ManualIndexerFallback_UsesManualLabel()
    {
        // When IndexerId and IndexerName are null/empty the pipeline fills in "manual" / "Manual selection".
        var request = BuildRequest() with
        {
            IndexerId = null,
            IndexerName = null
        };

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.Equal("manual", result.Candidate.IndexerId);
        Assert.Equal("Manual selection", result.Candidate.IndexerName);
    }

    [Fact]
    public void EvaluateSelectedRelease_CustomIndexerName_IsPreserved()
    {
        var request = BuildRequest() with
        {
            IndexerId = "idx-42",
            IndexerName = "My Indexer"
        };

        var result = _pipeline.EvaluateSelectedRelease(request);

        Assert.Equal("idx-42", result.Candidate.IndexerId);
        Assert.Equal("My Indexer", result.Candidate.IndexerName);
    }

    [Fact]
    public void EvaluateSelectedRelease_uses_source_priority_and_custom_formats_from_manual_context()
    {
        var customFormats = new[]
        {
            new CustomFormatItem(
                Id: "cf-1",
                Name: "Preferred Group",
                MediaType: "movies",
                Score: 125,
                TrashId: "trash-group",
                Conditions: """[{"type":"releaseGroup","value":"GROUP","required":true}]""",
                UpgradeAllowed: true,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)
        };

        var baseline = _pipeline.EvaluateSelectedRelease(BuildRequest());
        var enriched = _pipeline.EvaluateSelectedRelease(BuildRequest() with
        {
            IndexerId = "idx-1",
            IndexerName = "Primary",
            CandidateQuality = "WEB 1080p",
            SourcePriorityScore = 175,
            CustomFormats = customFormats
        });

        Assert.True(enriched.Candidate.Score > baseline.Candidate.Score);
        Assert.Equal(125, enriched.Candidate.CustomFormatScore);
        Assert.NotEmpty(enriched.Candidate.MatchedCustomFormats ?? []);
    }
}
