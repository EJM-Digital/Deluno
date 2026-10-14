using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Leftover downloads when the request is right (coverage register, area api): real folders in a library's download folder,
/// and a real qBittorrent 5.2.1 reply (tests/fixtures/recorded) saying which one it still holds. A folder the client holds is
/// never touched; one it does not is kept, removed or swept, and what is on the disk afterwards is what each route said.
/// </summary>
public sealed class OrphanRoutesAgainstRecordedAppsTests
{
    private sealed record Scene(RecordedAppServer Qbit, ApplicationTestHost App, string Downloads, string Held, string Stray1, string Stray2, string Stray3);

    private static async Task<Scene> SceneAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var held = Directory.CreateDirectory(Path.Combine(downloads, "Held.By.The.Client.2020")).FullName;
        var stray1 = Directory.CreateDirectory(Path.Combine(downloads, "Stray.One.2019")).FullName;
        var stray2 = Directory.CreateDirectory(Path.Combine(downloads, "Stray.Two.2018")).FullName;
        var stray3 = Directory.CreateDirectory(Path.Combine(downloads, "Stray.Three.2017")).FullName;
        foreach (var folder in new[] { held, stray1, stray2, stray3 })
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "video.mkv"), new byte[4096]);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(Path.Combine(folder, "video.mkv"), DateTime.UtcNow.AddDays(-3));
        }

        // Each of these folders is a release Deluno itself once sent to a client (only those are ever treated as leftovers).
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        foreach (var folder in new[] { held, stray1, stray2, stray3 })
        {
            var dispatchId = await jobs.RecordDownloadDispatchAsync("library-1", "movies", "movie", Path.GetFileName(folder), Path.GetFileName(folder), "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.ArchiveDispatchAsync(dispatchId, "Imported.", CancellationToken.None);
        }

        // The real reply of a finished torrent, changed only in where it says its files are: the folder the client still holds.
        var finished = RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info", ("0.content_path", held), ("0.save_path", downloads), ("0.state", "stalledUP"));
        var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "trackers" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(finished) : null);
        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
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
        Assert.True(client.IsSuccessStatusCode, await client.Content.ReadAsStringAsync());
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName,
            downloadsPath = downloads,
            importWorkflow = "copy"
        });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        return new Scene(qbit, app, downloads, held, stray1, stray2, stray3);
    }

    private static async Task DisposeAsync(Scene scene)
    {
        await scene.App.DisposeAsync();
        await scene.Qbit.DisposeAsync();
    }

    private static Task<HttpResponseMessage> PostPathAsync(ApplicationTestHost app, string route, string path)
        => app.Client.PostAsJsonAsync($"/api/download-clients/orphans/{route}", new { path });

    [Fact]
    public async Task Keeping_a_leftover_folder_leaves_it_on_disk_and_removing_one_deletes_it_while_the_folder_the_client_holds_is_never_touched()
    {
        var scene = await SceneAsync();
        try
        {
            var kept = await PostPathAsync(scene.App, "keep", scene.Stray1);
            Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync());
            Assert.True(Directory.Exists(scene.Stray1), "keeping never deletes");
            Assert.Contains("Stray.One.2019 was kept", await scene.App.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);
            var again = await PostPathAsync(scene.App, "keep", scene.Stray1);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            var removed = await PostPathAsync(scene.App, "remove", scene.Stray2);
            Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
            Assert.False(Directory.Exists(scene.Stray2), "the folder is gone from the disk");
            Assert.Contains("Stray.Two.2018 was removed", await scene.App.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

            var held = await PostPathAsync(scene.App, "remove", scene.Held);
            Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
            Assert.Contains("not an orphaned download any more", await held.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(scene.Held, "video.mkv")), "a download the client still holds is left alone");
            Assert.True(Directory.Exists(scene.Stray3));
            Assert.Empty(scene.Qbit.Unanswered);
        }
        finally
        {
            await DisposeAsync(scene);
        }
    }

    [Fact]
    public async Task Running_the_leftover_clean_up_now_deletes_only_the_unclaimed_folders_older_than_the_chosen_age_when_it_is_switched_on()
    {
        var scene = await SceneAsync();
        try
        {
            // Nothing automatic is on by default: a run now does nothing until the person has switched it on.
            var off = await scene.App.Client.PostAsync("/api/download-clients/leftovers/cleanup", content: null);
            Assert.Equal(0, (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
            Assert.True(Directory.Exists(scene.Stray1) && Directory.Exists(scene.Stray2) && Directory.Exists(scene.Stray3));

            var on = await scene.App.Client.PatchAsync("/api/settings", new StringContent("""{"leftoverDownloadCleanupEnabled":true,"leftoverDownloadCleanupAgeHours":24}""", Encoding.UTF8, "application/json"));
            Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync());
            Directory.SetLastWriteTimeUtc(scene.Stray3, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(Path.Combine(scene.Stray3, "video.mkv"), DateTime.UtcNow.AddHours(-3));

            var run = await scene.App.Client.PostAsync("/api/download-clients/leftovers/cleanup", content: null);

            Assert.True(run.IsSuccessStatusCode, await run.Content.ReadAsStringAsync());
            var result = await run.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, result.GetProperty("removed").GetInt32());
            Assert.False(Directory.Exists(scene.Stray1) || Directory.Exists(scene.Stray2), "the old unclaimed folders are gone");
            Assert.True(Directory.Exists(scene.Stray3), "a folder younger than the chosen age waits");
            Assert.True(File.Exists(Path.Combine(scene.Held, "video.mkv")), "the folder the client holds is never swept");
            Assert.Empty(scene.Qbit.Unanswered);
        }
        finally
        {
            await DisposeAsync(scene);
        }
    }
}
