using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

public sealed class GithubLatestReleaseTests
{
    private const string Releases = """
        [{"tag_name":"v1.0.0-rc.1","draft":false,"prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/rc1/Weir-win-Setup.exe"}]},
         {"tag_name":"v1.0.0-rc.2","draft":false,"prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/rc2/Weir-win-Setup.exe"},{"name":"SHA256SUMS.txt","browser_download_url":"https://x.test/rc2/SHA256SUMS.txt"}]},
         {"tag_name":"v9.0.0","draft":true,"prerelease":false,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/draft/Weir-win-Setup.exe"}]}]
        """;

    [Fact]
    public void Picks_the_highest_version_release_including_pre_releases_and_never_a_draft()
    {
        var release = GithubLatestRelease.PickRelease(Releases);

        Assert.Equal("https://x.test/rc2/Weir-win-Setup.exe", release!.Assets!.First(a => a.Name == "Weir-win-Setup.exe").BrowserDownloadUrl);
        Assert.Contains(release.Assets!, a => a.Name == "SHA256SUMS.txt");
    }

    [Fact]
    public void Resolves_a_repository_whose_only_release_is_a_pre_release()
    {
        var release = GithubLatestRelease.PickRelease("""[{"tag_name":"v1.0.0-rc.1","prerelease":true,"assets":[{"name":"Weir-win-Setup.exe","browser_download_url":"https://x.test/Weir-win-Setup.exe"}]}]""");

        Assert.Equal("https://x.test/Weir-win-Setup.exe", release!.Assets![0].BrowserDownloadUrl);
    }

    [Fact]
    public void Orders_rc2_over_rc1_and_a_stable_release_over_rc9()
    {
        string Pick(params string[] tags) => GithubLatestRelease.PickRelease("[" + string.Join(",", tags.Select(t => $$"""{"tag_name":"{{t}}","assets":[{"name":"{{t}}","browser_download_url":"https://x.test/{{t}}"}]}""")) + "]")!.Assets![0].Name!;

        Assert.Equal("v1.0.0-rc.2", Pick("v1.0.0-rc.1", "v1.0.0-rc.2"));
        Assert.Equal("v1.0.0", Pick("v1.0.0-rc.9", "v1.0.0"));
        Assert.Null(GithubLatestRelease.PickRelease("""[{"tag_name":"v1.0.0","draft":true}]"""));
    }

    [Fact]
    public void The_checksum_check_still_applies_to_the_pre_release_asset()
    {
        var release = GithubLatestRelease.PickRelease(Releases)!;
        var sums = "d34db33f  Weir-win-Setup.exe\n";

        Assert.Equal("d34db33f", GithubLatestRelease.ParseChecksum(sums, release.Assets!.First(a => a.Name == "Weir-win-Setup.exe").Name!));
        Assert.Null(GithubLatestRelease.ParseChecksum("d34db33f  OldWeir-win-Setup.exe\n", "Weir-win-Setup.exe"));
    }

    [Fact]
    public void Reads_the_matching_line_from_a_standard_SHA256SUMS_file()
    {
        var sums = "abc123  RELEASES\n" +
                   "d34db33f00000000000000000000000000000000000000000000000000000000  Weir-win-Setup.exe\n" +
                   "cafef00d  weir-web-dist.zip\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Equal("d34db33f00000000000000000000000000000000000000000000000000000000", hash);
    }

    [Fact]
    public void Answers_null_when_the_file_has_no_matching_line()
    {
        var hash = GithubLatestRelease.ParseChecksum("abc123  something-else.exe\n", "Weir-win-Setup.exe");

        Assert.Null(hash);
    }

    [Fact]
    public void Does_not_match_a_line_whose_name_merely_ends_with_the_target_file_name()
    {
        // "OldWeir-win-Setup.exe" ends with "Weir-win-Setup.exe" but is not it.
        // A pure EndsWith match would wrongly hand back this line's hash for a
        // lookup of the real asset.
        var sums = "deadbeef  OldWeir-win-Setup.exe\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Null(hash);
    }

    [Fact]
    public void Matches_a_path_prefixed_entry_by_its_base_name()
    {
        var sums = "d34db33f  dist/Weir-win-Setup.exe\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Equal("d34db33f", hash);
    }

    [Fact]
    public void Reads_a_hash_out_of_nzbgets_own_jsonp_signature_file()
    {
        // NZBGet's nzbget-<version>.sig.txt is not a SHA256SUMS.txt line --
        // it is a JSONP body naming every asset's SHA256 as its own key.
        var sig = "nzbget_signatures({\n" +
                  "\"MD5(nzbget-21.1-bin-windows-setup.exe)\" : \"4298f1eec6d7c8644844a28ceb92c0ed\",\n" +
                  "\"SHA1(nzbget-21.1-bin-windows-setup.exe)\" : \"61723cec5870438763d21bb7259a76a631c8108f\",\n" +
                  "\"SHA256(nzbget-21.1-bin-windows-setup.exe)\" : \"536515ca1e57321c5de664441d3394c4cd63c603183e257275188b3a9e168477\",\n" +
                  "\"RSA-SHA256(nzbget-21.1-bin-windows-setup.exe)\" : \"deadbeef\"\n" +
                  "});";

        var hash = GithubLatestRelease.ParseChecksum(sig, "nzbget-21.1-bin-windows-setup.exe");

        Assert.Equal("536515ca1e57321c5de664441d3394c4cd63c603183e257275188b3a9e168477", hash);
    }

    [Fact]
    public void Jsonp_signature_lookup_answers_null_for_a_different_asset_name()
    {
        var sig = "nzbget_signatures({\n" +
                  "\"SHA256(nzbget-21.1-bin-linux.run)\" : \"9c4bd5169c91c4ad56338d0989d63632abe708bdf496137b484e3faa18708011\"\n" +
                  "});";

        var hash = GithubLatestRelease.ParseChecksum(sig, "nzbget-21.1-bin-windows-setup.exe");

        Assert.Null(hash);
    }
}

public sealed class DirectoryListingLatestReleaseTests
{
    [Fact]
    public void Picks_the_highest_version_among_matching_links_regardless_of_listing_order()
    {
        var html = "<a href=\"deluge-2.1.1-win64-lt2.0-setup.exe\">deluge-2.1.1-win64-lt2.0-setup.exe</a>\n" +
                    "<a href=\"deluge-2.2.0-win64-setup.exe\">deluge-2.2.0-win64-setup.exe</a>\n" +
                    "<a href=\"deluge-2.1.0-win64-setup.exe\">deluge-2.1.0-win64-setup.exe</a>\n" +
                    "<a href=\"deluge-2.2.0-win32-setup.exe\">deluge-2.2.0-win32-setup.exe</a>\n";

        var picked = DirectoryListingLatestRelease.PickLatest(html, @"^deluge-[0-9][0-9.]*-win64-setup\.exe$");

        // Not the win32 build (wrong pattern), not the lt2.0 compatibility
        // build (excluded by the pattern), and not 2.1.0 (older version).
        Assert.Equal("deluge-2.2.0-win64-setup.exe", picked);
    }

    [Fact]
    public void Answers_null_when_nothing_in_the_listing_matches()
    {
        var html = "<a href=\"deluge-2.2.0-win32-setup.exe\">deluge-2.2.0-win32-setup.exe</a>\n";

        var picked = DirectoryListingLatestRelease.PickLatest(html, @"^deluge-[0-9][0-9.]*-win64-setup\.exe$");

        Assert.Null(picked);
    }
}
