using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1510: a manual import with transfer mode move said "Source kept because this workflow is configured to retain completed
/// downloads" although the file had been moved. The message is read from what the import did, never from the setting.
/// </summary>
public sealed class ManualImportMessageSaysWhatHappenedToTheSourceTests
{
    [Fact]
    public async Task A_move_import_says_the_file_was_moved_and_the_source_is_gone()
    {
        var (message, sourceExists) = await ImportAsync("move");

        Assert.False(sourceExists);
        Assert.Contains("moved", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kept", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_copy_import_says_the_copy_was_made_and_the_source_state_matches_the_disk()
    {
        var (message, sourceExists) = await ImportAsync("copy");

        // Copy clears the original once the copy is verified, so it is gone and the message says so.
        Assert.False(sourceExists);
        Assert.Contains("removed", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kept", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_hardlink_import_says_the_source_was_kept_as_a_second_name_and_it_is_still_there()
    {
        var (message, sourceExists) = await ImportAsync("hardlink");

        Assert.True(sourceExists);
        Assert.Contains("hardlink", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string Message, bool SourceExists)> ImportAsync(string transferMode)
    {
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>()));
        var library = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(downloads);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = library, downloadsPath = downloads,
            qualityProfileId = (string?)null, importWorkflow = "standard", processorName = (string?)null,
            processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null, processorFailureMode = (string?)null,
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var source = Path.Combine(downloads, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));

        var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/execute-batch", new
        {
            items = new[]
            {
                new
                {
                    preview = new { sourcePath = source, fileName = (string?)null, mediaType = "movies", title = "Arrival", year = 2016, genres = (string[]?)null, tags = (string[]?)null, studio = (string?)null, originalLanguage = (string?)null, imdbId = "tt2543164" },
                    transferMode, overwrite = false, allowCopyFallback = false, forceReplacement = false
                }
            }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("results").EnumerateArray().Single();
        Assert.True(row.GetProperty("succeeded").GetBoolean(), row.ToString());
        Assert.Single(Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories));
        return (row.GetProperty("message").GetString()!, File.Exists(source));
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
