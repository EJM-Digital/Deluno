using Deluno.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1098: the one place that decides why a download started, and the words for it.</summary>
public sealed class DownloadStartedByTests
{
    [Theory]
    [InlineData("movie-added", true, false, "manual")]
    [InlineData("series-added", true, false, "manual")]
    [InlineData("manual-season", true, false, "manual")]
    [InlineData("manual", true, true, "manual")]
    // A person pressing "search the library" is not a person asking for this one title.
    [InlineData("manual", false, false, "missing")]
    [InlineData("manual", false, true, "upgrade")]
    [InlineData("schedule", false, false, "missing")]
    [InlineData("schedule", false, true, "upgrade")]
    [InlineData("automatic-season", true, false, "missing")]
    // A feed match is missing or an upgrade according to whether the title already has a file.
    [InlineData("rss", true, false, "missing")]
    [InlineData("rss", true, true, "upgrade")]
    [InlineData("recovery-retry", true, false, "inherit")]
    [InlineData("grab-rejected", true, true, "inherit")]
    [InlineData("external-redownload", true, false, "inherit")]
    public void A_search_records_why_it_ran(string trigger, bool targeted, bool hasFile, string expected)
        => Assert.Equal(expected, DownloadStartedBy.ForSearch(trigger, targeted, hasFile));

    [Theory]
    [InlineData("manual", null, "movies", "You asked")]
    [InlineData("seerr", null, "tv", "Seerr")]
    [InlineData("list", "Trakt", "movies", "From Trakt")]
    [InlineData("list", null, "tv", "From a list")]
    [InlineData("upgrade", null, "tv", "Upgrade hunt")]
    [InlineData("missing", null, "tv", "Missing episode")]
    [InlineData("missing", null, "movies", "Missing")]
    [InlineData(null, null, "movies", null)]
    [InlineData("inherit", null, "movies", null)]
    [InlineData("nonsense", null, "movies", null)]
    public void The_label_is_plain_words_or_nothing(string? value, string? detail, string mediaType, string? expected)
        => Assert.Equal(expected, DownloadStartedBy.Label(value, detail, mediaType));
}
