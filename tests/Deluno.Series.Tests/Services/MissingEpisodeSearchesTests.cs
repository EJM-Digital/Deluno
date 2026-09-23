using Deluno.Series.Contracts;
using Deluno.Series.Services;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #593: a show's <b>Search now</b> asks for each missing episode, and only
/// the ones the page counts as missing.
/// </summary>
public sealed class MissingEpisodeSearchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:30:00Z");

    [Fact]
    public void Every_aired_monitored_episode_without_a_file_is_asked_for_in_order()
    {
        var episodes = new[]
        {
            Episode("e-205", 2, 5, aired: Now.AddDays(-1)),
            Episode("e-104", 1, 4, aired: Now.AddDays(-30)),
            Episode("e-101", 1, 1, aired: Now.AddDays(-40), hasFile: true),
            Episode("e-106", 1, 6, aired: Now.AddDays(-20), monitored: false),
            Episode("e-301", 3, 1, aired: Now.AddDays(7)),
            Episode("e-302", 3, 2, aired: null),
        };

        var planned = MissingEpisodeSearches.For("series-1", episodes, Now);

        Assert.Equal(["e-104", "e-205"], planned.Select(item => item.EpisodeId));
        Assert.All(planned, item => Assert.Equal("series-1", item.SeriesId));
    }

    [Fact]
    public void An_episode_with_no_title_is_named_by_its_code()
    {
        var planned = MissingEpisodeSearches.For("series-1", [Episode("e-104", 1, 4, aired: Now.AddDays(-1), title: null)], Now);

        Assert.Equal("S01E04", Assert.Single(planned).Title);
    }

    private static SeriesEpisodeInventoryItem Episode(
        string id,
        int season,
        int episode,
        DateTimeOffset? aired,
        bool hasFile = false,
        bool monitored = true,
        string? title = "An episode")
        => new(
            id,
            season,
            episode,
            title,
            null,
            aired,
            monitored,
            hasFile,
            hasFile ? "covered" : "missing",
            "test",
            QualityCutoffMet: false,
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            PreventLowerQualityReplacements: false,
            LastQualityDeltaDecision: null,
            LastSearchUtc: null,
            NextEligibleSearchUtc: null,
            UpdatedUtc: Now);
}
