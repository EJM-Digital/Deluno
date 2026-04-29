using Deluno.Platform.Quality;

namespace Deluno.Platform.Tests.Quality;

public sealed class MediaDecisionServiceTests
{
    [Fact]
    public void DecideWantedState_is_deterministic_for_identical_inputs()
    {
        var service = new MediaDecisionService(new VersionedMediaPolicyEngine());
        var input = new MediaWantedDecisionInput(
            MediaType: "movies",
            HasFile: true,
            CurrentQuality: "WEB 720p",
            CutoffQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false);

        var first = service.DecideWantedState(input);
        var second = service.DecideWantedState(input);

        Assert.Equal(first, second);
        Assert.Equal("upgrade", first.WantedStatus);
        Assert.Equal(MediaPolicyCatalog.CurrentVersion, first.PolicyVersion);
        Assert.Contains("WEB 1080p", first.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("series")]
    [InlineData("shows")]
    public void DecideWantedState_normalizes_series_media_aliases(string mediaType)
    {
        var service = new MediaDecisionService(new VersionedMediaPolicyEngine());
        var decision = service.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: mediaType,
            HasFile: false,
            CurrentQuality: null,
            CutoffQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true));

        Assert.Equal("missing", decision.WantedStatus);
        Assert.Equal(MediaPolicyCatalog.CurrentVersion, decision.PolicyVersion);
        Assert.Contains("TV show", decision.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_engine_migrates_legacy_snapshot_to_current_version()
    {
        var engine = new VersionedMediaPolicyEngine();

        var result = engine.Migrate(new MediaPolicySnapshot(
            Version: "legacy/manual",
            CutoffQuality: "web-dl 1080p",
            AllowedQualities: ["web-dl 720p", "bluray 1080p"],
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false));

        Assert.True(result.Changed);
        Assert.Equal(MediaPolicyCatalog.CurrentVersion, result.ToVersion);
        Assert.Equal(MediaPolicyCatalog.CurrentVersion, result.Snapshot.Version);
        Assert.Equal("WEB 1080p", result.Snapshot.CutoffQuality);
        Assert.Contains("Bluray 1080p", result.Snapshot.AllowedQualities);
    }
}
