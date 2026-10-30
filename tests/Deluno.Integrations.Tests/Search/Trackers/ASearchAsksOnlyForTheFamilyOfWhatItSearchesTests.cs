using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#1337: a film search never sends a TV category and a TV search never sends a film one, whatever the person ticked or the indexer lists.</summary>
public sealed class ASearchAsksOnlyForTheFamilyOfWhatItSearchesTests
{
    [Fact]
    public void Ticked_categories_are_cut_to_the_family_searched()
    {
        Assert.Equal([2040, 2045], TrackerCategories.ForFamily(isTv: false, [2040, 5040, 2045], configured: "5000"));
        Assert.Equal([5040], TrackerCategories.ForFamily(isTv: true, [2040, 5040], configured: "2000"));
    }

    [Fact]
    public void With_nothing_ticked_the_indexers_own_list_is_cut_to_the_family()
    {
        Assert.Equal([2000, 2040], TrackerCategories.ForFamily(isTv: false, null, configured: "2000,5000,2040"));
        Assert.Equal([5000], TrackerCategories.ForFamily(isTv: true, [], configured: "2000,5000,2040"));
    }

    [Fact]
    public void A_list_with_nothing_in_the_family_asks_the_whole_family()
    {
        Assert.Equal([2000], TrackerCategories.ForFamily(isTv: false, [5040], configured: "5000"));
        Assert.Equal([5000], TrackerCategories.ForFamily(isTv: true, [2040], configured: null));
    }
}
