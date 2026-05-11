using Deluno.Movies.Contracts;
using Deluno.Movies.Services;
using Deluno.Platform.Contracts;
using Deluno.Platform.Quality;
using Moq;

namespace Deluno.Movies.Tests.Services;

public class MovieWorkflowServiceTests
{
    private readonly Mock<IVersionedMediaPolicyEngine> mockPolicyEngine;
    private readonly MovieWorkflowService service;

    public MovieWorkflowServiceTests()
    {
        mockPolicyEngine = new Mock<IVersionedMediaPolicyEngine>();
        service = new MovieWorkflowService(mockPolicyEngine.Object);
    }

    [Fact]
    public void EvaluateWantedStatus_WithNoFile_ReturnsMissingStatus()
    {
        var result = service.EvaluateWantedStatus(
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
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    [Fact]
    public void EvaluateWantedStatus_BelowCutoffWithUpgradeEnabled_ReturnsUpgradeStatus()
    {
        var result = service.EvaluateWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("upgrade", result.WantedStatus);
    }

    [Fact]
    public void EvaluateCandidate_WithNullCandidateQuality_ReturnsUnknownStatus()
    {
        var input = new MovieCandidateEvaluationInput(
            MovieId: "123",
            CurrentQuality: "WEB 720p",
            CandidateQuality: null!,
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("unknown", result.WantedStatus);
        Assert.Contains("quality could not be detected", result.Reason.ToLower());
    }

    [Fact]
    public void IsReplacementAllowed_WithProtectionDisabled_ReturnsTrue()
    {
        var result = service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: false);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_WithProtectionEnabledAndSameQuality_ReturnsTrue()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var result = service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 1080p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_WithProtectionEnabledAndHigherQuality_ReturnsTrue()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("Remux 2160p")).Returns(120);

        var result = service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "Remux 2160p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_WithProtectionEnabledAndLowerQuality_ReturnsFalse()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var result = service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.False(result);
    }

    [Fact]
    public void IsReplacementAllowed_WithNoCurrentFile_ReturnsTrue()
    {
        var result = service.IsReplacementAllowed(
            currentQuality: null,
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void CalculateQualityDelta_WithValidQualities_ReturnsCorrectDelta()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var result = service.CalculateQualityDelta("WEB 720p", "WEB 1080p", null);

        Assert.Equal(30, result);
    }

    [Fact]
    public void CalculateQualityDelta_WithLowerQuality_ReturnsNegativeDelta()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var result = service.CalculateQualityDelta("WEB 1080p", "WEB 720p", null);

        Assert.Equal(-30, result);
    }

    [Fact]
    public void CalculateQualityDelta_WithSameQuality_ReturnsZero()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var result = service.CalculateQualityDelta("WEB 1080p", "WEB 1080p", null);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateQualityDelta_WithNullCurrentQuality_ReturnsNull()
    {
        var result = service.CalculateQualityDelta(null, "WEB 1080p", null);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateQualityDelta_WithNullCandidateQuality_ReturnsNull()
    {
        var result = service.CalculateQualityDelta("WEB 720p", null!, null);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateQualityDelta_WithInvalidRank_ReturnsNull()
    {
        mockPolicyEngine.Setup(x => x.QualityRank(It.IsAny<string>())).Returns(-1);

        var result = service.CalculateQualityDelta("WEB 1080p", "WEB 720p", null);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateCandidate_WithUpgradeCandidate_ReturnsUpgradeDecision()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var input = new MovieCandidateEvaluationInput(
            MovieId: "123",
            CurrentQuality: "WEB 720p",
            CandidateQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("upgrade", result.WantedStatus);
        Assert.Equal(30, result.QualityDelta);
        Assert.True(result.IsReplacementAllowed);
    }

    [Fact]
    public void EvaluateCandidate_WithDowngradeAndProtection_BlocksReplacement()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var input = new MovieCandidateEvaluationInput(
            MovieId: "123",
            CurrentQuality: "WEB 1080p",
            CandidateQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: false,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: true,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("blocked", result.WantedStatus);
        Assert.False(result.IsReplacementAllowed);
        Assert.Equal(-30, result.QualityDelta);
    }

    [Fact]
    public void EvaluateCandidate_MissingMovie_AllowsAnyReplacement()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var input = new MovieCandidateEvaluationInput(
            MovieId: "123",
            CurrentQuality: null,
            CandidateQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: true,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("missing", result.WantedStatus);
        Assert.True(result.IsReplacementAllowed);
    }
}
