using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

public sealed class DownloadClientTagMatchTests
{
    [Fact]
    public void An_untagged_client_is_eligible_for_any_title()
    {
        Assert.True(DownloadClientTagMatch.IsEligible(clientTags: "", titleTags: null));
        Assert.True(DownloadClientTagMatch.IsEligible(clientTags: "", titleTags: ["anime"]));
        Assert.True(DownloadClientTagMatch.IsEligible(clientTags: null, titleTags: ["anime"]));
    }

    [Fact]
    public void A_tagged_client_needs_a_shared_tag()
    {
        Assert.False(DownloadClientTagMatch.IsEligible(clientTags: "anime", titleTags: null));
        Assert.False(DownloadClientTagMatch.IsEligible(clientTags: "anime", titleTags: ["kids"]));
        Assert.True(DownloadClientTagMatch.IsEligible(clientTags: "anime", titleTags: ["kids", "anime"]));
    }

    [Fact]
    public void Tag_matching_ignores_case_and_surrounding_whitespace()
    {
        Assert.True(DownloadClientTagMatch.IsEligible(clientTags: " Anime , 4K ", titleTags: ["anime"]));
    }
}
