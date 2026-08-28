using Deluno.Contracts;

namespace Deluno.Series.Tests;

/// <summary>
/// A show's rung, decided from its episodes.
///
/// <para>This rule used to be written twice — once on the server from the
/// title-level row, once in the browser from the episode counts — and the two
/// disagreed. On the lab rig, Severance with three of twenty episodes was
/// "Quality met" to the chips and "Missing" on its own poster, and clicking the
/// chip returned a title whose poster contradicted it.</para>
/// </summary>
public sealed class SeriesRungTests
{
    /// <summary>
    /// The case that started it. A collection has no title-level file, so it can
    /// have no title-level quality — asking "is this the quality you asked for"
    /// about a show is a category mistake, and the old code answered it from
    /// whichever file the import happened to see first.
    /// </summary>
    [Fact]
    public void Three_of_twenty_episodes_is_missing_not_quality_met()
    {
        Assert.Equal(
            WantedStatuses.Missing,
            SeriesRung.From(aired: 20, airedWithFile: 3, airedUpgradable: 0, hasFutureAirDate: false));
    }

    [Fact]
    public void Every_aired_episode_present_and_at_cutoff_is_quality_met()
    {
        Assert.Equal(
            WantedStatuses.Covered,
            SeriesRung.From(aired: 20, airedWithFile: 20, airedUpgradable: 0, hasFutureAirDate: false));
    }

    /// <summary>
    /// Complete, and some of it could be better. This is the rung a show could
    /// never reach while its status came from one arbitrary episode's file.
    /// </summary>
    [Fact]
    public void Every_aired_episode_present_with_some_below_cutoff_is_upgradable()
    {
        Assert.Equal(
            WantedStatuses.Upgrade,
            SeriesRung.From(aired: 20, airedWithFile: 20, airedUpgradable: 4, hasFutureAirDate: false));
    }

    /// <summary>
    /// Nothing aired yet is not a failure to find anything. A show announced but
    /// not started is Upcoming, and only becomes Missing once there is something
    /// to have missed.
    /// </summary>
    [Fact]
    public void Nothing_aired_with_something_to_come_is_upcoming()
    {
        Assert.Equal(
            WantedStatuses.Upcoming,
            SeriesRung.From(aired: 0, airedWithFile: 0, airedUpgradable: 0, hasFutureAirDate: true));
    }

    /// <summary>
    /// And a show with no catalogued episodes at all is not upcoming — it is
    /// unknown, which reads as Missing so it gets searched for rather than
    /// quietly sitting as "nothing to do yet" for ever.
    /// </summary>
    [Fact]
    public void Nothing_aired_and_nothing_to_come_is_missing()
    {
        Assert.Equal(
            WantedStatuses.Missing,
            SeriesRung.From(aired: 0, airedWithFile: 0, airedUpgradable: 0, hasFutureAirDate: false));
    }

    /// <summary>
    /// The fill on the dot, which is what stops three-of-twenty looking exactly
    /// like none-of-eighty-seven. Both are Missing and both are red; only one is
    /// nearly done.
    /// </summary>
    [Theory]
    [InlineData(20, 3, 0.15)]
    [InlineData(87, 0, 0.0)]
    [InlineData(20, 20, 1.0)]
    public void Progress_is_what_you_hold_over_what_has_aired(int aired, int held, double expected)
    {
        Assert.Equal(expected, SeriesRung.Progress(aired, held), precision: 3);
    }

    /// <summary>
    /// Nothing aired draws a full ring rather than an empty one: an Upcoming
    /// show is not missing anything, and an empty ring would read as the worst
    /// possible state when it is simply early.
    /// </summary>
    [Fact]
    public void A_show_with_nothing_aired_is_drawn_full_rather_than_empty()
    {
        Assert.Equal(1, SeriesRung.Progress(aired: 0, airedWithFile: 0));
    }

    /// <summary>
    /// More files than aired episodes is possible — a specials-heavy show whose
    /// air dates are unknown — and must not draw a ring past full.
    /// </summary>
    [Fact]
    public void Progress_never_exceeds_full()
    {
        Assert.Equal(1, SeriesRung.Progress(aired: 5, airedWithFile: 9));
    }
}
