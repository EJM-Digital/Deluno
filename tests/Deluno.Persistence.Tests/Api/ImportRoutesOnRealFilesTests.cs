using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Filesystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The manual-import routes (<c>/api/filesystem/import/preview</c> and <c>/import/jobs</c>) over HTTP, against the real
/// application, a real library folder and a real file. The pipeline itself is proven in its own tests; these show the routes
/// hand it the right thing and refuse what must not be queued.
/// </summary>
public sealed class ImportRoutesOnRealFilesTests
{
    [Fact]
    public async Task The_preview_resolves_where_the_file_would_land_inside_the_library_and_places_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);
        var (library, source) = await SeedAsync(app);

        var preview = await PreviewAsync(app, source);

        Assert.True(preview.GetProperty("sourceExists").GetBoolean());
        Assert.True(preview.GetProperty("isSupportedMediaFile").GetBoolean());
        var destination = preview.GetProperty("destinationPath").GetString()!;
        Assert.StartsWith(library, destination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Arrival", destination);
        Assert.False(File.Exists(destination), "a preview must place nothing");
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_preview_says_so_when_the_source_file_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);
        var (_, source) = await SeedAsync(app);
        File.Delete(source);

        var preview = await PreviewAsync(app, source);

        Assert.False(preview.GetProperty("sourceExists").GetBoolean());
    }

    [Fact]
    public async Task Queuing_an_import_without_a_preview_is_a_plain_400_and_queues_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/jobs", new { transferMode = "auto" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("An import needs a preview.", body.GetProperty("message").GetString());
        Assert.Empty(Directory.GetFiles(app.DataRoot, "*.mkv", SearchOption.AllDirectories));
        Assert.Equal(0, await ImportJobCountAsync(app));
    }

    [Fact]
    public async Task Queuing_an_import_of_a_file_that_is_not_there_is_refused_and_queues_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);
        var (_, source) = await SeedAsync(app);
        var request = RequestFor(source);
        File.Delete(source);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/jobs", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Import cannot be queued until the preview is valid.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal(0, await ImportJobCountAsync(app));
    }

    [Fact]
    public async Task A_queued_import_leaves_the_file_where_it_is_until_the_job_runs_and_then_the_file_lands_in_the_library()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: ReadableProbe);
        var (library, source) = await SeedAsync(app);
        var bytes = await File.ReadAllBytesAsync(source);
        var request = RequestFor(source);

        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/jobs", request);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var queued = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(queued.GetProperty("jobId").GetString()));
        Assert.Equal("filesystem.import.execute", queued.GetProperty("job").GetProperty("jobType").GetString());
        Assert.Equal(1, await ImportJobCountAsync(app));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(library, "*", SearchOption.AllDirectories));

        // What the worker does with the queued job: run its payload through the pipeline.
        var payload = JsonSerializer.Deserialize<ImportExecuteRequest>(queued.GetProperty("job").GetProperty("payloadJson").GetString()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await app.Services.GetRequiredService<IImportPipelineService>().ExecuteAsync(payload, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var placed = Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories);
        var landed = Assert.Single(placed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(landed));
    }

    // ---------------------------------------------------------------- helpers

    // ffprobe is another program: here it answers "a readable two-hour film", so the pipeline is tested on real files, not on whether this machine has it.
    private static void ReadableProbe(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IMediaProbeService, ReadableVideoProbe>());

    private sealed class ReadableVideoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
                VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
                AudioStreams: [], SubtitleStreams: []));
    }

    private static ImportExecuteRequest RequestFor(string source)
        => new(
            new ImportPreviewRequest(source, null, "movies", "Arrival", 2016, null, null, null, null),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true);

    private static async Task<JsonElement> PreviewAsync(ApplicationTestHost app, string source)
    {
        using var response = await app.Client.PostAsJsonAsync("/api/filesystem/import/preview", RequestFor(source).Preview);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> ImportJobCountAsync(ApplicationTestHost app)
    {
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobs?pageSize=100");
        var items = page.TryGetProperty("items", out var list) ? list : page;
        return items.EnumerateArray().Count(job => job.GetProperty("jobType").GetString() == "filesystem.import.execute");
    }

    private static async Task<(string Library, string Source)> SeedAsync(ApplicationTestHost app)
    {
        var library = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(downloads);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath = library,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        response.EnsureSuccessStatusCode();

        var source = Path.Combine(downloads, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        return (library, source);
    }
}
