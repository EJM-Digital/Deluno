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

    // ── Explaining the routing choice in plain English (#808) ─────────────

    [Fact]
    public void An_untagged_client_never_needs_explaining()
    {
        Assert.Null(DownloadClientTagMatch.Explain(clientTags: "", titleTags: ["anime"], clientName: "qBittorrent"));
        Assert.Null(DownloadClientTagMatch.Explain(clientTags: null, titleTags: null, clientName: "qBittorrent"));
    }

    [Fact]
    public void A_tagged_client_explains_which_tag_sent_the_title_there()
    {
        var reason = DownloadClientTagMatch.Explain(clientTags: "anime", titleTags: ["kids", "anime"], clientName: "Anime qBittorrent");

        Assert.NotNull(reason);
        Assert.Contains("Anime qBittorrent", reason);
        Assert.Contains("anime", reason);
    }

    [Fact]
    public void A_tagged_client_with_no_matching_title_tag_has_nothing_to_explain()
    {
        // IsEligible would already have excluded this client from routing, so
        // this only guards against Explain being called out of that context.
        Assert.Null(DownloadClientTagMatch.Explain(clientTags: "anime", titleTags: ["kids"], clientName: "Anime qBittorrent"));
    }
}
