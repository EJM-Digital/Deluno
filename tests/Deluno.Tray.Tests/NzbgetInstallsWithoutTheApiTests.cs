using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1368: NZBGet's file names carry the version, so the picker listed the release through api.github.com (60 calls an hour
/// for a whole network), and a used-up allowance stopped the install. The files are now named from the tag, so the release
/// feed on github.com is enough. Every reply is GitHub's own, recorded: NZBGet's releases.atom (its newest entry is the
/// rolling tag "testing"), its sig.txt files, and the status of the two download addresses (tests/fixtures/recorded/github).
/// The API's answer is the used-up allowance: 403 with no calls left, built here because recording it would spend the
/// shared address's whole hour (recorded/MISSING.md).
/// </summary>
public sealed class NzbgetInstallsWithoutTheApiTests
{
    private static BundledComponentSpec Nzbget() => BundledComponents.Load().Single(s => s.Kind == "nzbget");

    private static DirectoryListingLatestReleaseTests.Replay Replay(bool apiLimited = true)
        => new(request => request.RequestUri!.Host == "api.github.com" && apiLimited ? Limit() : null,
            RecordedReplies.Github("nzbget-release-feed"), RecordedReplies.Github("nzbget-latest-release"), RecordedReplies.Github("nzbget-download-files"));

    private static HttpResponseMessage Limit()
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeSeconds().ToString());
        return response;
    }

    [Fact]
    public async Task With_the_api_used_up_NZBGet_still_installs_from_the_release_feed()
    {
        var replay = Replay();

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.False(lookup.RateLimited);
        Assert.Equal("https://github.com/nzbgetcom/nzbget/releases/download/v26.3/nzbget-26.3-bin-windows-setup.exe", lookup.Asset?.AssetUrl);
        Assert.Equal("v26.3", lookup.Tag);
        Assert.Empty(replay.ApiRequests);
    }

    [Fact]
    public async Task The_installer_is_checked_against_the_hash_in_that_releases_own_sig_file()
    {
        var replay = Replay();
        var signature = RecordedReplies.Github("nzbget-latest-release").Find("v26.3/nzbget-26.3.sig.txt").Reply.Body!;
        var expected = GithubLatestRelease.ParseChecksum(signature, "nzbget-26.3-bin-windows-setup.exe");

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.NotNull(expected);
        Assert.Equal(expected, lookup.Asset?.Sha256);
    }

    [Fact]
    public async Task The_rolling_testing_tag_at_the_top_of_the_feed_is_never_picked()
    {
        var replay = Replay();

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.DoesNotContain(replay.Requests, url => url.Contains("/download/testing/", StringComparison.Ordinal));
        Assert.NotEqual("testing", lookup.Tag);
    }

    [Fact]
    public async Task A_release_whose_sig_file_does_not_list_the_installer_is_passed_over_for_the_next_one()
    {
        // Same recorded feed; v26.3's sig file is answered with v26.2's text, which names other file names (the one value changed).
        var older = RecordedReplies.Github("nzbget-latest-release").Find("v26.2/nzbget-26.2.sig.txt").Reply.Body!;
        var replay = new DirectoryListingLatestReleaseTests.Replay(request =>
            request.RequestUri!.AbsoluteUri.EndsWith("/v26.3/nzbget-26.3.sig.txt", StringComparison.Ordinal)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(older) }
                : null,
            RecordedReplies.Github("nzbget-release-feed"), RecordedReplies.Github("nzbget-latest-release"));

        var lookup = await GithubLatestRelease.LookupAsync(Nzbget(), CancellationToken.None, replay.Handler);

        Assert.Equal("v26.2", lookup.Tag);
        Assert.Contains(lookup.Skipped ?? [], s => s.StartsWith("v26.3", StringComparison.Ordinal));
        Assert.Empty(replay.ApiRequests);
    }

    [Fact]
    public void A_file_name_template_is_filled_from_the_tag_without_its_leading_v()
    {
        Assert.Equal("nzbget-26.3-bin-windows-setup.exe", Deluno.Contracts.ReleaseAtomFeed.ExpandAssetTemplate("nzbget-{version}-bin-windows-setup.exe", "v26.3"));
        Assert.Equal("nzbget-26.3.sig.txt", Deluno.Contracts.ReleaseAtomFeed.ExpandAssetTemplate("nzbget-{version}.sig.txt", "26.3"));
    }
}
