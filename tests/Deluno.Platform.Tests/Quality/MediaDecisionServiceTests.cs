using Deluno.Platform.Quality;

namespace Deluno.Platform.Tests.Quality;

public sealed class MediaDecisionServiceTests
{
    [Fact]
    public void DecideWantedState_is_deterministic_for_identical_inputs()
    {
        var service = new MediaDecisionService();
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
        Assert.Contains("WEB 1080p", first.WantedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("series")]
    [InlineData("shows")]
    public void DecideWantedState_normalizes_series_media_aliases(string mediaType)
    {
        var service = new MediaDecisionService();
        var decision = service.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: mediaType,
            HasFile: false,
            CurrentQuality: null,
            CutoffQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true));

        Assert.Equal("missing", decision.WantedStatus);
        Assert.Contains("TV show", decision.WantedReason, StringComparison.OrdinalIgnoreCase);
    }
}
