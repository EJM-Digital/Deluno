using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A file that a download client is still seeding, with qBittorrent's real reply as the client: the recorded torrent that finished over
/// one film in its save folder (tests/fixtures/recorded/qbittorrent, "finished-single-file"), listed by the client as it really lists
/// it. The only value changed is the folder the torrent says it holds, set to the real file on this machine's disk (the recording's own
/// is a path on the machine it was made on). Real files, real stores, the real client adapter over a real loopback port.
/// </summary>
public sealed class AFileARecordedTorrentIsSeedingIsKeptByAnImportTests
{
    private const string Seeding = "Recorded.Seeding.Film.2020.1080p.WEB-DL-DELUNO.mkv";
    private const string Unrelated = "Arrival.2016.WEB.1080p.mkv";

    // ffprobe is another program: here it answers "a readable two-hour film", so the pipeline is tested on real files.
    private static void ReadableProbe(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>());

    private sealed record Scene(ApplicationTestHost App, RecordedAppServer Qbit, string Library, string Downloads, string SeedingFile, string OtherFile) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Qbit.DisposeAsync();
        }
    }

    private static async Task<Scene> StartAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);
        var library = Directory.CreateDirectory(Path.Combine(app.DataRoot, "movies")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads")).FullName;
        var seeding = Path.Combine(downloads, Seeding);
        var other = Path.Combine(downloads, Unrelated);
        foreach (var file in new[] { seeding, other })
        {
            await File.WriteAllBytesAsync(file, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        }

        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = library, downloadsPath = downloads,
            importWorkflow = "copy", autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        created.EnsureSuccessStatusCode();

        var listing = RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info", ("0.content_path", seeding));
        var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "finished-single-file" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(listing) : null);
        var client = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(client.IsSuccessStatusCode, await client.Content.ReadAsStringAsync());
        return new Scene(app, qbit, library, downloads, seeding, other);
    }

    private static ImportPreviewRequest PreviewOf(string source, string? dispatchId = null)
        => new(source, null, "movies", "Arrival", 2016, null, null, null, null, DispatchId: dispatchId);

    private static async Task<JsonElement> PreviewAsync(ApplicationTestHost app, string route, ImportPreviewRequest request)
    {
        using var response = await app.Client.PostAsJsonAsync(route, request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("/api/filesystem/import/preview")]
    [InlineData("/api/integrations/external/import-preview")]
    public async Task The_preview_says_a_client_still_holds_a_file_it_lists_as_its_own_and_not_one_it_does_not_or_one_that_has_a_download_behind_it(string route)
    {
        await using var scene = await StartAsync();

        var held = await PreviewAsync(scene.App, route, PreviewOf(scene.SeedingFile));
        var notHeld = await PreviewAsync(scene.App, route, PreviewOf(scene.OtherFile));
        var withDownload = await PreviewAsync(scene.App, route, PreviewOf(scene.SeedingFile, dispatchId: "a-dispatch-of-our-own"));

        Assert.True(held.GetProperty("clientHoldsSource").GetBoolean());
        Assert.False(notHeld.GetProperty("clientHoldsSource").GetBoolean());
        Assert.False(withDownload.GetProperty("clientHoldsSource").GetBoolean());
        Assert.Contains(scene.Qbit.Requests, request => request.PathAndQuery.Contains("torrents/info", StringComparison.Ordinal));
        Assert.True(File.Exists(scene.SeedingFile) && File.Exists(scene.OtherFile), "a preview places nothing and moves nothing");
    }

    [Fact]
    public async Task A_manual_copy_import_keeps_the_original_a_client_is_seeding_and_deletes_the_original_of_a_file_nobody_holds()
    {
        await using var scene = await StartAsync();
        var pipeline = scene.App.Services.GetRequiredService<IImportPipelineService>();

        var seeded = await pipeline.ExecuteAsync(new ImportExecuteRequest(PreviewOf(scene.SeedingFile), TransferMode: "copy", Overwrite: false, AllowCopyFallback: true), CancellationToken.None);
        var unheld = await pipeline.ExecuteAsync(new ImportExecuteRequest(PreviewOf(scene.OtherFile), TransferMode: "copy", Overwrite: false, AllowCopyFallback: true), CancellationToken.None);

        Assert.True(seeded.Succeeded, seeded.Message);
        Assert.True(File.Exists(scene.SeedingFile), "the torrent the client is seeding keeps its data");
        Assert.True(unheld.Succeeded, unheld.Message);
        Assert.False(File.Exists(scene.OtherFile), "a copy import deletes its verified original by default");
        Assert.Equal(2, Directory.GetFiles(scene.Library, "*.mkv", SearchOption.AllDirectories).Length);
    }

    private sealed class ReadableVideoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
                VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
                AudioStreams: [], SubtitleStreams: []));
    }
}
