using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #822: the content-blocklist sync writes only qBittorrent's own
/// <c>excluded_file_names</c> preference through <c>app/setPreferences</c> -
/// never <c>excluded_file_names_enabled</c>, which stays whatever the person
/// set it to in qBittorrent itself.
/// </summary>
public sealed class QbittorrentExcludedFileNamesPushTests
{
    [Fact]
    public async Task Pushing_patterns_sets_only_excluded_file_names()
    {
        var handler = new RecordingHandler();

        var (succeeded, message) = await new QbittorrentDownloadClient(() => handler)
            .PushExcludedFileNamesAsync(Client(), ["*.exe", "*.scr"], CancellationToken.None);

        Assert.True(succeeded, message);
        Assert.NotNull(handler.SetPreferencesBody);
        var decoded = Uri.UnescapeDataString(handler.SetPreferencesBody!);
        Assert.Contains("excluded_file_names", decoded);
        Assert.Contains("*.exe", decoded);
        Assert.Contains("*.scr", decoded);
        Assert.DoesNotContain("excluded_file_names_enabled", decoded);
    }

    private static DownloadClientItem Client()
        => new(
            Id: "qbit-1", Name: "qBittorrent", Protocol: "qbittorrent", Host: "localhost", Port: 8080,
            Username: null, Secret: null, EndpointUrl: "http://localhost:8080/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? SetPreferencesBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("auth/login", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.", Encoding.UTF8, "text/plain") };
            }

            if (path.Contains("app/setPreferences", StringComparison.Ordinal))
            {
                SetPreferencesBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
