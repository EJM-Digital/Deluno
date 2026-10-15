using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// POST /api/blocked-releases/{id}/cleanup over a real host, a real database file and a real temp folder: the Blocklist's
/// "Clean up now" on one refused release asks qBittorrent 5.2.1 (its recorded replies, over a real loopback port) to remove
/// the copy with its files and says so in Activity; a refusal that never justifies deleting leaves the files on disk.
/// </summary>
public sealed class CleanUpOneRefusedReleaseRouteTests
{
    private const string Hash = "69ca35bca234b9cb822abc435ddbababf6147591";
    private const string Release = "First.Film.2020.1080p.WEB-DL-DELUNO";

    private static Task<RecordedAppServer> QbittorrentAsync()
    {
        var before = RecordedReplies.Qbittorrent("downloading").Exchanges[0];
        var after = RecordedReplies.Qbittorrent("removed-with-data").Exchanges[1];
        return RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "removed-with-data" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.InOrder("torrents/info", before, after, before, after, before, after));
    }

    private static async Task<(string ClientId, string Downloads)> ClientAndLibraryAsync(ApplicationTestHost app, RecordedAppServer qbit)
    {
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent",
            host = qbit.Host,
            port = qbit.Port,
            username = "deluno",
            password = "a-test-password",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var clientId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Incomplete")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        // Deluno sent this release to the client: only a download Deluno sent is ever removed by a clean-up.
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await jobs.RecordDownloadDispatchAsync(libraryId, "movies", "movie", "movie-1", Release, "Fixture", clientId, "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: Hash);
        return (clientId, downloads);
    }

    private static BlockedRelease Refusal(string clientId, string reason)
        => new(Guid.NewGuid().ToString("N"), "key-" + Release, Release, "Fixture Indexer", "movies", "movie-1", "First Film",
            reason, "Refused.", Hash, clientId, "qBittorrent", DateTimeOffset.UtcNow,
            BlockedReleaseStates.Refused, BlockedReleaseOrigins.Automatic);

    [Fact]
    public async Task Clean_up_now_on_one_refused_release_removes_it_and_its_files_at_the_client_and_says_so_in_Activity()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var (clientId, _) = await ClientAndLibraryAsync(app, qbit);
        var blocked = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        var refusal = Refusal(clientId, "noVideoStream");
        await blocked.BlockAsync(refusal, CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/blocked-releases/{refusal.Id}/cleanup", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RefusedDownloadCleanupOutcomes.Cleared, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
        var delete = Assert.Single(qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
        Assert.Contains(Hash, delete.Body, StringComparison.Ordinal);
        Assert.Contains("deleteFiles=true", delete.Body, StringComparison.Ordinal);
        Assert.Contains("download.cleanup.cleared", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
    }

    [Fact]
    public async Task Clean_up_now_answers_404_for_a_release_that_was_never_refused_and_touches_no_file_or_client()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, downloads) = await ClientAndLibraryAsync(app, qbit);
        var bystander = Path.Combine(downloads, "keep-me.mkv");
        File.WriteAllBytes(bystander, new byte[64]);

        var response = await app.Client.PostAsync("/api/blocked-releases/no-such-block/cleanup", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(File.Exists(bystander));
        Assert.DoesNotContain(qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_up_now_leaves_the_files_of_a_refusal_whose_reason_never_justifies_deleting_them()
    {
        await using var qbit = await QbittorrentAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var (clientId, downloads) = await ClientAndLibraryAsync(app, qbit);
        var folder = Directory.CreateDirectory(Path.Combine(downloads, "qBittorrent", "deluno-movies")).FullName;
        var video = Path.Combine(folder, Release + ".mkv");
        File.WriteAllBytes(video, new byte[256]);
        File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddHours(-5));
        var refusal = Refusal(clientId, "unmatched");
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(refusal, CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/blocked-releases/{refusal.Id}/cleanup", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RefusedDownloadCleanupOutcomes.NothingToClear, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
        Assert.True(File.Exists(video), "an unmatched refusal alone does not justify deleting a video");
        Assert.DoesNotContain(qbit.Requests, request => request.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal));
    }
}
