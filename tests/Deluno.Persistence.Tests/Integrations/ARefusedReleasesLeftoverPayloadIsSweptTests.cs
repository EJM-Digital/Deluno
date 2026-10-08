using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1167: a refused release whose client entry went without its files left a payload nothing listed any more
/// (a fake episode .exe, 952 MB, on the rig). The refused-release cleanup deletes it, with an Activity line, only
/// under Deluno's own folders and only for a release Deluno sent.
/// </summary>
public sealed class ARefusedReleasesLeftoverPayloadIsSweptTests
{
    private const string Release = "Severance S02E10 1080p WEBRip H264-WREN";

    private static BlockedRelease Refusal(string reasonCode, string? clientId = "qb-1", string? hash = "hash-1", string name = Release)
        => new(Guid.NewGuid().ToString("N"), $"key-{name}", name, "Fixture Indexer", "tv", "episode-1", "Severance",
            reasonCode, "Refused.", hash, clientId, "qBittorrent", DateTimeOffset.UtcNow,
            BlockedReleaseStates.Refused, BlockedReleaseOrigins.Automatic);

    private static string Payload(string root, string name = Release + ".exe", bool old = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "qBittorrent", "deluno-tv")).FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[256]);
        if (old)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-5));
        }

        return path;
    }

    private static async Task<string> LibraryDownloadsAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Incomplete")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Shows", mediaType = "tv", rootPath = root, downloadsPath = downloads });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return downloads;
    }

    private static async Task<RefusedDownloadCleanupResult> CleanAsync(ApplicationTestHost app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRefusedDownloadCleanupService>().CleanUpEverythingAsync(CancellationToken.None);
    }

    private static async Task<int> ClearedLinesAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None))
            .Count(line => line.Category == "download.cleanup.cleared");

    [Fact]
    public async Task A_refused_payload_no_client_lists_any_more_is_deleted_with_one_activity_line()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = await LibraryDownloadsAsync(app);
        var payload = Payload(downloads);
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(Refusal("noVideoStream"), CancellationToken.None);

        var result = await CleanAsync(app);
        await CleanAsync(app);

        Assert.False(File.Exists(payload));
        Assert.True(result.Cleared >= 1);
        Assert.Equal(1, await ClearedLinesAsync(app));
    }

    [Fact]
    public async Task A_payload_that_carries_an_executable_is_deleted_whatever_the_refusal_says()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = await LibraryDownloadsAsync(app);
        var exe = Payload(downloads);
        var video = Payload(downloads, "Severance S02E11 1080p WEBRip H264-WREN.mkv");
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(Refusal("unmatched"), CancellationToken.None);
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(Refusal("unmatched", name: "Severance S02E11 1080p WEBRip H264-WREN"), CancellationToken.None);

        await CleanAsync(app);

        Assert.False(File.Exists(exe), "an executable has no place in a video release");
        Assert.True(File.Exists(video), "an unmatched refusal alone does not justify deleting a video");
    }

    [Fact]
    public async Task A_release_deluno_did_not_send_or_a_file_outside_its_folders_is_never_touched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = await LibraryDownloadsAsync(app);
        var outside = Payload(Path.Combine(app.DataRoot, "SomebodyElses"));
        var manual = Payload(downloads, "Hand.Added.Thing.exe");
        var blocked = app.Services.GetRequiredService<IBlockedReleaseRepository>();
        await blocked.BlockAsync(Refusal("noVideoStream"), CancellationToken.None);
        // Blocked by hand, never sent to a client by Deluno.
        await blocked.BlockAsync(Refusal("noVideoStream", clientId: null, hash: null, name: "Hand.Added.Thing"), CancellationToken.None);

        await CleanAsync(app);

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(manual));
    }

    [Fact]
    public void Nothing_a_client_still_lists_or_an_import_is_using_or_a_library_holds_is_swept()
    {
        using var storage = TestStorage.Create();
        var downloads = Path.Combine(storage.DataRoot, "Incomplete");
        var listed = Payload(downloads);
        var libraryRoot = Path.Combine(storage.DataRoot, "Shows");
        var inLibrary = Payload(libraryRoot);
        var refused = new[] { Refusal("noVideoStream") };

        bool Found(IReadOnlyCollection<string> listedByClients, IReadOnlyCollection<string> libraries, IReadOnlyCollection<string> claimed, params string[] roots)
            => RefusedPayloadSweep.Find(roots, refused, listedByClients, libraries, claimed, _ => true, DateTimeOffset.UtcNow, TimeSpan.FromHours(2)).Count > 0;

        Assert.True(Found([], [], [], downloads));
        Assert.False(Found([listed], [], [], downloads));
        Assert.False(Found([], [], [Path.GetDirectoryName(listed)!], downloads));
        Assert.False(Found([], [libraryRoot], [], libraryRoot));
        Assert.True(File.Exists(inLibrary));
        // Too young to be sure nothing is still writing it.
        File.SetLastWriteTimeUtc(listed, DateTime.UtcNow);
        Assert.False(Found([], [], [], downloads));
    }
}
