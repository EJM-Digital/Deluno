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
    private static long BlockedRowsOnDisk(ApplicationTestHost app)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blocked_releases;";
        return (long)command.ExecuteScalar()!;
    }

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
        // #800: blocked from this endpoint means a person named the release
        // themselves, not Deluno deciding it on its own.
        Assert.Equal(BlockedReleaseOrigins.Manual, refused.Origin);
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
    public async Task Lifting_a_block_writes_activity_as_a_manual_action_and_never_queues_a_search()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var blockResponse = await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest("Arrival.2016.2160p.UHD.BluRay", "Nebula", MediaType: "movie", EntityId: "movie-1", Title: "Arrival"));
        Assert.True(blockResponse.IsSuccessStatusCode, await blockResponse.Content.ReadAsStringAsync());
        var blocked = await blockResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = blocked.GetProperty("id").GetString()!;

        var jobsBefore = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        Assert.Equal(1L, BlockedRowsOnDisk(app));

        var liftResponse = await app.Client.DeleteAsync($"/api/blocked-releases/{id}");
        Assert.True(liftResponse.IsSuccessStatusCode, await liftResponse.Content.ReadAsStringAsync());

        var blocklist = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        Assert.Empty(await blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(0L, BlockedRowsOnDisk(app));

        // DESIGN-007 decision 8: lifting never starts a search by itself.
        var jobsAfter = await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(50, CancellationToken.None);
        Assert.Equal(jobsBefore.Count, jobsAfter.Count);

        var activityFeed = app.Services.GetRequiredService<IActivityFeedRepository>();
        var activity = Assert.Single(
            await activityFeed.ListActivityAsync(10, null, null, CancellationToken.None),
            entry => entry.Category == "release.block-lifted");
        Assert.Contains("Arrival", activity.Message);
        // Attributed to a person's own action, the same way #924's "It's
        // bad" removal reason is (see ARemovalReasonDoesExactlyWhatItSaysTests) —
        // through the message's own wording rather than a separate flag.
        Assert.StartsWith("You lifted", activity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifting_an_unknown_block_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var block = await app.Client.PostAsJsonAsync("/api/blocked-releases", new BlockReleaseRequest("Arrival.2016.2160p.UHD.BluRay", "Nebula", MediaType: "movie", EntityId: "movie-1", Title: "Arrival"));
        block.EnsureSuccessStatusCode();

        var response = await app.Client.DeleteAsync("/api/blocked-releases/does-not-exist");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1L, BlockedRowsOnDisk(app));
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
