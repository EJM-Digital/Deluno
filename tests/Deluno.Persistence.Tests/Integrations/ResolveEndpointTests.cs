using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #804: Deluno used to guess the scheme from whether the saved host string
/// happened to start with "http", with no way to say "this host, but over
/// SSL" or add a reverse-proxy URL base. ResolveEndpoint now reads UseSsl and
/// UrlBase directly instead of guessing.
/// </summary>
public sealed class ResolveEndpointTests
{
    private static DownloadClientItem Client(
        string? host = "localhost",
        int? port = 8080,
        bool useSsl = false,
        string? urlBase = null,
        string? endpointUrl = null) =>
        new(
            Id: "client-1",
            Name: "qBittorrent",
            Protocol: "qbittorrent",
            Host: host,
            Port: port,
            Username: null,
            Secret: null,
            EndpointUrl: endpointUrl,
            UseSsl: useSsl,
            UrlBase: urlBase,
            MoviesCategory: null,
            TvCategory: null,
            CategoryTemplate: null,
            Priority: 1,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void UseSsl_off_resolves_to_plain_http()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(Client(useSsl: false));

        Assert.NotNull(endpoint);
        Assert.Equal("http", endpoint!.Scheme);
        Assert.Equal("localhost", endpoint.Host);
        Assert.Equal(8080, endpoint.Port);
    }

    [Fact]
    public void UseSsl_on_resolves_to_https()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(Client(useSsl: true));

        Assert.NotNull(endpoint);
        Assert.Equal("https", endpoint!.Scheme);
        Assert.Equal("localhost", endpoint.Host);
        Assert.Equal(8080, endpoint.Port);
    }

    [Fact]
    public void UrlBase_is_appended_to_the_resolved_path()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(Client(useSsl: true, urlBase: "sabnzbd"));

        Assert.NotNull(endpoint);
        Assert.Equal("https", endpoint!.Scheme);
        Assert.Equal("/sabnzbd/", endpoint.AbsolutePath);
    }

    [Fact]
    public void UrlBase_with_leading_and_trailing_slashes_is_normalized()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(Client(urlBase: "/sabnzbd/"));

        Assert.NotNull(endpoint);
        Assert.Equal("/sabnzbd/", endpoint!.AbsolutePath);
    }

    [Fact]
    public void A_host_already_written_as_a_full_url_keeps_its_own_scheme_regardless_of_UseSsl()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(Client(host: "http://plain-host", port: null, useSsl: true));

        Assert.NotNull(endpoint);
        Assert.Equal("http", endpoint!.Scheme);
    }

    [Fact]
    public void An_explicit_EndpointUrl_wins_over_Host_UseSsl_and_UrlBase()
    {
        var endpoint = DownloadClientHelpers.ResolveEndpoint(
            Client(useSsl: true, urlBase: "sabnzbd", endpointUrl: "http://saved-endpoint:9999"));

        Assert.NotNull(endpoint);
        Assert.Equal("http", endpoint!.Scheme);
        Assert.Equal("saved-endpoint", endpoint.Host);
        Assert.Equal(9999, endpoint.Port);
    }
}
