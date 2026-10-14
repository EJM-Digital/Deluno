using Deluno.Contracts;
using Deluno.Infrastructure.Http;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1181: reading a github.com release page, and showing GitHub's reset time without a server-zone guess.</summary>
public sealed class ReleaseAtomFeedTests
{
    // GitHub's own feeds, recorded on 14 Oct 2026 (tests/fixtures/recorded/github): Weir's releases.atom (release candidates only,
    // the newest published without files), NZBGet's (a rolling "testing" tag ahead of v26.3) and qBittorrent's tags.atom.
    private static string WeirFeed() => RecordedReplies.Github("weir-newest-release-has-no-files").Find("Weir/releases.atom").Reply.Body!;

    private static string NzbgetFeed() => RecordedReplies.Github("nzbget-release-feed").Single.Reply.Body!;

    private static string QbittorrentTagsFeed() => RecordedReplies.Github("qbittorrent-tags").Find("tags.atom").Reply.Body!;

    /// <summary>The same recorded feed with its entries listed in the opposite order (an oldest-first listing, which GitHub does not send).</summary>
    private static string Reversed(string atom)
    {
        var entries = System.Text.RegularExpressions.Regex.Matches(atom, "<entry>.*?</entry>", System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Value).ToArray();
        var head = atom[..atom.IndexOf("<entry>", StringComparison.Ordinal)];
        return head + string.Join('\n', entries.Reverse()) + "\n</feed>";
    }

    [Fact]
    public void The_newest_tag_is_by_version_order_with_release_candidates_only_when_asked()
    {
        Assert.Equal("v1.0.0-rc.17", ReleaseAtomFeed.NewestTag(WeirFeed()));
        // The listing order does not decide it.
        Assert.Equal("v1.0.0-rc.17", ReleaseAtomFeed.NewestTag(Reversed(WeirFeed())));
        // Weir has published release candidates only, so a stable-only reader finds nothing.
        Assert.Null(ReleaseAtomFeed.NewestTag(WeirFeed(), includePreReleases: false));
        // NZBGet's feed leads with a rolling tag called "testing", which is no version.
        Assert.Equal("testing", ReleaseAtomFeed.Tags(NzbgetFeed())[0]);
        Assert.Equal("v26.3", ReleaseAtomFeed.NewestTag(NzbgetFeed()));
        Assert.Equal("v26.3", ReleaseAtomFeed.NewestTag(NzbgetFeed(), includePreReleases: false));
        Assert.Null(ReleaseAtomFeed.NewestTag(string.Empty));
    }

    [Fact]
    public void A_tag_prefix_picks_only_that_projects_tags()
    {
        // qBittorrent's tags page lists 5.3.0rc1 first, then 5.2.4: a stable-only reader takes 5.2.4.
        Assert.Equal("release-5.2.4", ReleaseAtomFeed.NewestTag(QbittorrentTagsFeed(), includePreReleases: false, "release-"));
        Assert.Equal("release-5.3.0rc1", ReleaseAtomFeed.NewestTag(QbittorrentTagsFeed(), includePreReleases: true, "release-"));
        Assert.Null(ReleaseAtomFeed.NewestTag(QbittorrentTagsFeed(), true, "deluge-"));
    }

    [Fact]
    public void Download_addresses_need_no_api_call()
    {
        Assert.Equal("https://github.com/o/r/releases/download/v1.0.0-rc.10/Setup.exe", ReleaseAtomFeed.DownloadUrl("o/r", "v1.0.0-rc.10", "Setup.exe"));
        Assert.Equal("https://github.com/o/r/releases/download/v1/", ReleaseAtomFeed.DownloadFolderUrl("o/r", "v1"));
    }

    [Fact]
    public void The_limit_message_gives_the_reset_as_a_UTC_stamp_rounded_up_so_it_is_never_early()
    {
        var message = GitHubRateLimitException.MessageFor(new DateTimeOffset(2026, 10, 9, 0, 35, 28, TimeSpan.Zero));

        Assert.Contains("used up until 2026-10-09 00:36 UTC. Deluno will try again then.", message);
    }

    [Fact]
    public void On_the_same_computer_the_stamp_reads_in_its_own_zone()
    {
        var sydney = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(11), "test", "test");
        var text = GitHubRateLimitException.LocalizeResetTimes("used up until 2026-10-09 00:36 UTC. Deluno will try again then.", sydney);

        Assert.Equal("used up until 11:36 am. Deluno will try again then.", text);
        Assert.Equal("nothing here", GitHubRateLimitException.LocalizeResetTimes("nothing here", sydney));
    }
}
