using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1060: after a clean install NZBGet has not created its download folders
/// yet (it does that on the first download), so it reports 0 free space and
/// Health showed "could not read free space". Deluno measures the nearest
/// folder that exists on the path instead, and never creates the folders.
/// </summary>
public sealed class DownloadFolderNotCreatedYetFreeSpaceTests
{
    [Fact]
    public void Nearest_existing_folder_is_measured_when_the_folder_is_not_there_yet()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deluno-nzb-" + Guid.NewGuid().ToString("N"), "complete", "movies");

        var free = DownloadClientHelpers.FreeSpaceOfNearestExistingFolder(missing);

        Assert.True(free is > 0);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/folder")]
    public void Nothing_is_measured_for_a_path_that_is_not_rooted_on_this_machine(string? path)
        => Assert.Null(DownloadClientHelpers.FreeSpaceOfNearestExistingFolder(path));

    [Fact]
    public async Task NzbGet_snapshot_reports_free_space_of_the_nearest_existing_folder_when_it_reports_zero()
    {
        var destDir = Path.Combine(Path.GetTempPath(), "deluno-nzb-" + Guid.NewGuid().ToString("N"), "complete");
        var handler = new Handler(destDir, mainDirOnly: false);

        var snapshot = await new NzbGetDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("localhost"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.FreeSpaceBytes is > 0);
        Assert.False(Directory.Exists(destDir));
    }

    [Fact]
    public async Task NzbGet_snapshot_resolves_MainDir_references_in_DestDir()
    {
        var main = Path.Combine(Path.GetTempPath(), "deluno-nzb-" + Guid.NewGuid().ToString("N"));
        var handler = new Handler(main, mainDirOnly: true);

        var snapshot = await new NzbGetDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("localhost"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.FreeSpaceBytes is > 0);
    }

    [Fact]
    public async Task NzbGet_snapshot_does_not_guess_for_a_client_on_another_machine()
    {
        var handler = new Handler(Path.Combine(Path.GetTempPath(), "x"), mainDirOnly: false);

        var snapshot = await new NzbGetDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("nas.example.invalid"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.FreeSpaceBytes);
    }

    private static DownloadClientItem Client(string host)
        => new(
            Id: "nzbget-1", Name: "NZBGet", Protocol: "nzbget", Host: host, Port: 6789,
            Username: null, Secret: null, EndpointUrl: $"http://{host}:6789/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(string dir, bool mainDirOnly) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string reply;
            if (body.Contains("\"status\"", StringComparison.Ordinal))
            {
                reply = """{"result":{"FreeDiskSpaceMB":0,"DownloadRate":0}}""";
            }
            else if (body.Contains("\"config\"", StringComparison.Ordinal))
            {
                var main = JsonSerializer.Serialize(dir);
                reply = mainDirOnly
                    ? $$"""{"result":[{"Name":"MainDir","Value":{{main}}},{"Name":"DestDir","Value":{{JsonSerializer.Serialize("${MainDir}" + Path.DirectorySeparatorChar + "complete")}}}]}"""
                    : $$"""{"result":[{"Name":"DestDir","Value":{{main}}}]}""";
            }
            else if (body.Contains("\"listgroups\"", StringComparison.Ordinal) || body.Contains("\"history\"", StringComparison.Ordinal))
            {
                reply = """{"result":[]}""";
            }
            else
            {
                reply = """{"result":"21.1"}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
