using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The "Import failed" notification (#1282), on a real library folder and real files: an import whose source file has gone
/// fails for real, is written down as a recovery case, and sends its notification while that switch is on and stays quiet while
/// it is off. ffprobe is another program, so here it answers "a readable film".
/// </summary>
public sealed class ImportFailedNotificationOnRealFilesTests
{
    private static async Task<(ApplicationTestHost App, FakeOutboundNotificationService Outbound, string Source, string Library)> StartAsync(bool importFailedOn)
    {
        var outbound = new FakeOutboundNotificationService();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>());
            services.AddSingleton<IOutboundNotificationService>(outbound);
        });
        var preferences = app.Services.GetRequiredService<INotificationService>();
        var current = await preferences.GetPreferencesAsync();
        current.ImportFailedEnabled = importFailedOn;
        await preferences.UpdatePreferencesAsync(current);

        var library = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(downloads);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Movies", mediaType = "movies", purpose = "collection", rootPath = library, downloadsPath = downloads, importWorkflow = "copy" });
        created.EnsureSuccessStatusCode();
        var source = Path.Combine(downloads, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        return (app, outbound, source, library);
    }

    private static ImportExecuteRequest RequestFor(string source)
        => new(new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, null, null, null, null), TransferMode: "copy", Overwrite: false, AllowCopyFallback: true);

    [Fact]
    public async Task An_import_that_fails_sends_the_import_failed_notification_while_that_switch_is_on()
    {
        var (app, outbound, source, library) = await StartAsync(importFailedOn: true);
        await using var _ = app;
        var request = RequestFor(source);
        File.Delete(source);

        var result = await app.Services.GetRequiredService<IImportPipelineService>().ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded, result.Message);
        Assert.Contains("import.failed", outbound.DispatchedCategories);
        Assert.Empty(Directory.GetFiles(library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_import_that_fails_sends_no_import_failed_notification_while_that_switch_is_off_and_is_still_a_failure()
    {
        var (app, outbound, source, library) = await StartAsync(importFailedOn: false);
        await using var _ = app;
        var request = RequestFor(source);
        File.Delete(source);

        var result = await app.Services.GetRequiredService<IImportPipelineService>().ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded, result.Message);
        Assert.DoesNotContain("import.failed", outbound.DispatchedCategories);
        Assert.Empty(Directory.GetFiles(library, "*", SearchOption.AllDirectories));
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
