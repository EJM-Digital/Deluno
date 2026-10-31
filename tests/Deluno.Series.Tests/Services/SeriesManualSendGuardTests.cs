using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Series.Services;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #1175: a release sent by hand is compared with what is already on its way by its season and episode
/// tokens: a pack and one of its episodes are the same thing, a different episode never is.
/// </summary>
public sealed class SeriesManualSendGuardTests
{
    private static readonly (string EpisodeId, int SeasonNumber, int EpisodeNumber)[] Episodes =
    [
        ("e-101", 1, 1), ("e-102", 1, 2), ("e-201", 2, 1)
    ];

    private static Deluno.Media.ActiveDownloadCheck Check(string release)
        => SeriesManualSendGuard.Build("s-1", "The Show", Episodes, release, new Mock<IDownloadClientTelemetryService>().Object);

    private static DownloadInFlight Episode(string id) => new("episode", id, "whatever");

    private static DownloadInFlight Series(string release, string seriesId = "s-1") => new("series", seriesId, release);

    [Fact]
    public void A_release_names_the_episodes_it_carries_in_order()
    {
        Assert.Equal("S01E02", Check("The.Show.S01E02.1080p.WEB-DL-GRP").Subject);
        Assert.Equal("S01E01, S01E02", Check("The.Show.S01E01E02.720p").Subject);
    }

    [Fact]
    public void A_season_pack_names_its_seasons_and_a_release_with_no_numbers_names_the_show()
    {
        Assert.Equal("Season 1", Check("The.Show.S01.1080p.WEB-DL-GRP").Subject);
        Assert.Equal("The Show", Check("The Show complete collection").Subject);
        Assert.Equal("The Show", Check("").Subject);
    }

    [Fact]
    public void An_episode_download_is_the_same_target_only_when_the_release_covers_that_episode()
    {
        var one = Check("The.Show.S01E02.1080p.WEB-DL-GRP");
        Assert.True(one.IsSameTarget(Episode("e-102")));
        Assert.True(one.IsSameTarget(Episode("E-102")));
        Assert.False(one.IsSameTarget(Episode("e-101")));
        Assert.False(one.IsSameTarget(Episode("e-201")));
        Assert.False(one.IsSameTarget(Episode("not-this-show")));

        var pack = Check("The.Show.S01.1080p.WEB-DL-GRP");
        Assert.True(pack.IsSameTarget(Episode("e-101")));
        Assert.True(pack.IsSameTarget(Episode("e-102")));
        Assert.False(pack.IsSameTarget(Episode("e-201")));
    }

    [Fact]
    public void A_download_for_another_show_is_never_the_same_target()
        => Assert.False(Check("The.Show.S01E02.1080p").IsSameTarget(Series("The.Show.S01E02.1080p", "another-show")));

    [Fact]
    public void A_show_level_download_is_compared_by_the_seasons_and_episodes_both_carry()
    {
        var episode = Check("The.Show.S01E02.1080p");
        Assert.True(episode.IsSameTarget(Series("The.Show.S01.720p")));
        Assert.True(episode.IsSameTarget(Series("The.Show.S01E02.720p")));
        Assert.False(episode.IsSameTarget(Series("The.Show.S01E03.720p")));
        Assert.False(episode.IsSameTarget(Series("The.Show.S02.720p")));

        var pack = Check("The.Show.S01.1080p");
        Assert.True(pack.IsSameTarget(Series("The.Show.S01.720p")));
        Assert.True(pack.IsSameTarget(Series("The.Show.S01E04.720p")));
        Assert.False(pack.IsSameTarget(Series("The.Show.S02.720p")));
    }

    [Fact]
    public void A_release_or_download_with_no_numbers_is_compared_by_name()
    {
        var unnumbered = Check("The Show complete collection");
        Assert.True(unnumbered.IsSameTarget(Series("the show COMPLETE collection")));
        Assert.False(unnumbered.IsSameTarget(Series("The Show something else")));
        Assert.False(unnumbered.IsSameTarget(Series("The.Show.S01E02.720p")));
        Assert.False(Check("The.Show.S01E02.1080p").IsSameTarget(Series("The Show complete collection")));
    }

    [Fact]
    public async Task Replacing_removes_the_first_download_from_its_client_or_says_why_it_cannot()
    {
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(t => t.RemoveWithOptionsAsync("c-1", "q-1", true, false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueRemovalResult("c-1", "q-1", "replace", true, "removed", false, false, null, null));
        telemetry.Setup(t => t.RemoveWithOptionsAsync("c-1", "q-2", true, false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueRemovalResult("c-1", "q-2", "replace", false, "client said no", false, false, null, null));
        var check = SeriesManualSendGuard.Build("s-1", "The Show", Episodes, "The.Show.S01E02.1080p", telemetry.Object);

        Assert.Null(await check.ReplaceAsync(new Deluno.Media.ActiveDownloadConflict("c-1", "qBit", "q-1", "old", "S01E02"), CancellationToken.None));
        Assert.Equal(
            "The first download could not be removed from qBit: client said no",
            await check.ReplaceAsync(new Deluno.Media.ActiveDownloadConflict("c-1", "qBit", "q-2", "old", "S01E02"), CancellationToken.None));
        Assert.Equal(
            "Deluno cannot tell which download in qBit to remove. Remove it there, or send this one as well.",
            await check.ReplaceAsync(new Deluno.Media.ActiveDownloadConflict("c-1", "qBit", null, "old", "S01E02"), CancellationToken.None));
    }
}
