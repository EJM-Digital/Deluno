using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.BlockedReleases;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Cleanup sheet J5 (#925): a release can be blocked from wherever it is
/// named — a search result never grabbed, a title's search history, or an
/// Activity entry — not only from a proposal Deluno itself raised.
///
/// <para>The "it is downloading right now" case is not this endpoint: a
/// caller that finds the release still in a client's queue uses the
/// existing POST /api/download-clients/{clientId}/queue/{queueItemId}/remove
/// with reason "failed" (#924), covered by
/// <c>ARemovalReasonDoesExactlyWhatItSaysTests</c>. This endpoint is the
/// plain "just block it" case: no client action, nothing to stop.</para>
/// </summary>
public sealed class ABlockedReleaseCanBeAddedFromAnywhereTests
{
    [Fact]
    public async Task A_release_never_downloaded_can_still_be_blocked()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest("Arrival.2016.2160p.UHD.BluRay", "Nebula", MediaType: "movie", Title: "Arrival"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);

        var blocklist = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        var refused = Assert.Single(await blocklist.ListAsync(CancellationToken.None));

        Assert.Equal("Arrival.2016.2160p.UHD.BluRay", refused.ReleaseName);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(ImportFailurePolicy.ManualFailure, refused.ReasonCode);
        Assert.Null(refused.TorrentHashOrItemId);
        Assert.Null(refused.DownloadClientId);
        // Never downloaded: no client to stop, no client to clean up.
        Assert.Empty(await blocklist.ListAwaitingCleanupAsync(CancellationToken.None));

        var activityFeed = app.Services.GetRequiredService<IActivityFeedRepository>();
        var activity = Assert.Single(
            await activityFeed.ListActivityAsync(10, null, null, CancellationToken.None),
            entry => entry.Category == "release.blocked");
        Assert.Contains("Arrival", activity.Message);
        Assert.Contains("blocked", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blocking_by_hash_records_the_hash_and_keys_every_indexer()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest(
                "Some.Show.S01E01.1080p.WEB-DL",
                "Nebula",
                TorrentHashOrItemId: "ABCDEF0123456789",
                MediaType: "tv",
                EntityId: "series-1",
                Title: "Some Show"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);

        var blocklist = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        var refused = Assert.Single(await blocklist.ListAsync(CancellationToken.None));
        Assert.Equal("abcdef0123456789", refused.TorrentHashOrItemId?.ToLowerInvariant());

        // #874: a manual block, like every other bad-file reason, refuses the
        // release wherever it is offered again — by hash, and by name from any
        // indexer — not only from the one it was named from here.
        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.ForHash("ABCDEF0123456789"), keys);
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer("Some.Show.S01E01.1080p.WEB-DL"), keys);
    }

    [Fact]
    public async Task A_release_name_is_required()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest(string.Empty));

        Assert.False(response.IsSuccessStatusCode);

        var blocklist = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        Assert.Empty(await blocklist.ListAsync(CancellationToken.None));
    }
}
