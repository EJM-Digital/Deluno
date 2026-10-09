using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>#1199: a hand-off names only the files at or above the processor's own minimum size; the rest stay in the download.</summary>
public sealed class AHandoffLeavesOutFilesBelowTheProcessorsMinimumTests : IDisposable
{
    private const long Minimum = 1000;

    private static readonly ProcessorConnectionItem Connection = new(
        "connection-1", "Processor", "generic-webhook", "http://processor.example.test/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", "secret", true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-1199-" + Guid.NewGuid().ToString("N"), "Release.Name");

    public AHandoffLeavesOutFilesBelowTheProcessorsMinimumTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Extras"));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_folder)!, true);

    private string Write(string relative, int size)
    {
        var path = Path.Combine(_folder, relative);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private ProcessorHandoffItem Handoff() => new(
        "handoff-1", "lib-1", "movies", "client-1", "queue-1", "Release.Name", _folder, "Processor", "waiting",
        null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static LibraryItem Library()
        => new("lib-1", "Films", "movies", "main", "/films", null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static Mock<IProcessorConnectionService> Processor(long? minimum)
    {
        var processor = new Mock<IProcessorConnectionService>();
        processor.Setup(p => p.GetLibraryFoldersAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorLibraryFolder("lib-1", "/watch", true, null, minimum)]);
        return processor;
    }

    private static async Task<(ProcessorHandoffItem Outbound, List<string> Notes)> Run(ProcessorHandoffItem handoff, long? minimum, IReadOnlyList<ProcessorPathMappingItem>? mappings = null)
    {
        var notes = new List<string>();
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(a => a.RecordActivityOnChangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((category, message, _, _, _, _, _) => notes.Add(category + ": " + message))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem?)null);
        var outbound = await HandoffFileSelection.ForProcessorAsync(handoff, Library(), Connection, mappings ?? [], Processor(minimum).Object, activity.Object, CancellationToken.None);
        return (outbound, notes);
    }

    [Fact]
    public async Task A_small_extra_is_left_out_untouched_and_listed_once_as_left_in_the_download()
    {
        var movie = Write("Movie.mkv", 5000);
        var extra = Write(Path.Combine("Extras", "Featurette.mkv"), 200);

        var (outbound, notes) = await Run(Handoff(), Minimum);

        Assert.Equal([movie], outbound.OutputFiles);
        Assert.Equal(_folder, outbound.SourcePath);
        Assert.True(File.Exists(extra));
        Assert.Equal(200, new FileInfo(extra).Length);
        var note = Assert.Single(notes);
        Assert.StartsWith("processing.left-in-download:", note);
        Assert.Contains("left in the download", note);
        Assert.Contains("Extras/Featurette.mkv", note);
        Assert.True(File.Exists(movie));
    }

    [Fact]
    public async Task A_file_exactly_the_minimum_is_included()
    {
        var exact = Write("Exact.mkv", (int)Minimum);
        var below = Write("Below.mkv", (int)Minimum - 1);
        var movie = Write("Movie.mkv", 5000);

        var (outbound, _) = await Run(Handoff(), Minimum);

        Assert.Equal(new[] { exact, movie }.Order(StringComparer.Ordinal).ToList(), outbound.OutputFiles);
        Assert.True(File.Exists(below));
    }

    [Fact]
    public async Task With_no_minimum_advertised_the_whole_folder_is_handed_over()
    {
        Write("Movie.mkv", 5000);
        Write("Extra.mkv", 10);

        var (outbound, notes) = await Run(Handoff(), minimum: null);

        Assert.Null(outbound.OutputFiles);
        Assert.Equal(_folder, outbound.SourcePath);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task When_nothing_is_small_nothing_is_narrowed_and_nothing_is_noted()
    {
        Write("Movie.mkv", 5000);

        var (outbound, notes) = await Run(Handoff(), Minimum);

        Assert.Null(outbound.OutputFiles);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task File_names_go_over_in_the_processors_paths()
    {
        var movie = Write("Movie.mkv", 5000);
        Write("Extra.mkv", 10);
        var mapping = new ProcessorPathMappingItem("m-1", "connection-1", Path.GetDirectoryName(_folder)!, "/processor", true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var (outbound, _) = await Run(Handoff(), Minimum, [mapping]);

        Assert.StartsWith("/processor", outbound.SourcePath);
        Assert.All(outbound.OutputFiles!, file => Assert.StartsWith("/processor", file));
        Assert.Single(outbound.OutputFiles!);
        Assert.EndsWith("Movie.mkv", outbound.OutputFiles![0]);
        Assert.True(File.Exists(movie));
    }

    [Fact]
    public async Task Handing_off_never_deletes_so_removing_the_download_takes_everything()
    {
        var files = new[] { Write("Movie.mkv", 5000), Write("Extra.mkv", 10), Write(Path.Combine("Extras", "Trailer.mkv"), 20) };

        await Run(Handoff(), Minimum);

        // The hand-off removed nothing; the whole folder is still there, and the download client's
        // removal after seeding (a delete of the download's folder) takes all of it.
        Assert.All(files, file => Assert.True(File.Exists(file)));
        Directory.Delete(_folder, true);
        Assert.All(files, file => Assert.False(File.Exists(file)));
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task The_wire_payload_names_output_files_only_when_there_are_some()
    {
        string? body = null;
        var service = new ProcessorConnectionService(new Factory(new HttpClient(new Handler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }))));

        await service.SubmitAsync(Connection, Handoff() with { OutputFiles = ["/a/Movie.mkv"] }, CancellationToken.None);
        Assert.Equal("/a/Movie.mkv", JsonDocument.Parse(body!).RootElement.GetProperty("outputFiles")[0].GetString());

        await service.SubmitAsync(Connection, Handoff(), CancellationToken.None);
        Assert.False(JsonDocument.Parse(body!).RootElement.TryGetProperty("outputFiles", out _));
    }

    [Theory]
    [InlineData("""[{"watched_folder":"/w","manager_library_key":"lib-1","minimum_file_size_bytes":52428800}]""", 52428800L)]
    [InlineData("""[{"watched_folder":"/w","manager_library_key":"lib-1","minimum_file_size_bytes":null}]""", null)]
    [InlineData("""[{"watched_folder":"/w","manager_library_key":"lib-1"}]""", null)]
    [InlineData("""[{"watched_folder":"/w","manager_library_key":"lib-1","minimum_file_size_bytes":"big"}]""", null)]
    public void The_minimum_is_read_as_bytes_and_absent_means_none(string json, long? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, Assert.Single(ProcessorConnectionService.ParseLibraryFolders(document.RootElement)).MinimumFileSizeBytes);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
