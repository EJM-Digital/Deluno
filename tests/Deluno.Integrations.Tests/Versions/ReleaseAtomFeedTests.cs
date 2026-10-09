using Deluno.Contracts;
using Deluno.Infrastructure.Http;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1181: reading a github.com release page, and showing GitHub's reset time without a server-zone guess.</summary>
public sealed class ReleaseAtomFeedTests
{
    private const string Atom = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <link rel="alternate" type="text/html" href="https://github.com/o/r/releases"/>
          <entry><link rel="alternate" type="text/html" href="https://github.com/o/r/releases/tag/v1.0.0-rc.2"/></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/o/r/releases/tag/v1.0.0-rc.10"/></entry>
          <entry><link rel="alternate" type="text/html" href="https://github.com/o/r/releases/tag/v0.9.0"/></entry>
        </feed>
        """;

    [Fact]
    public void The_newest_tag_is_by_version_order_with_release_candidates_only_when_asked()
    {
        Assert.Equal("v1.0.0-rc.10", ReleaseAtomFeed.NewestTag(Atom));
        Assert.Equal("v0.9.0", ReleaseAtomFeed.NewestTag(Atom, includePreReleases: false));
        Assert.Null(ReleaseAtomFeed.NewestTag("<feed/>"));
    }

    [Fact]
    public void A_tag_prefix_picks_only_that_projects_tags()
        => Assert.Equal("release-5.2.4", ReleaseAtomFeed.NewestTag(
            "<a><link href=\"https://github.com/q/q/releases/tag/release-5.2.4\"/><link href=\"https://github.com/q/q/releases/tag/v9.0.0\"/></a>", true, "release-"));

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
