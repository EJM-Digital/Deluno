using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Which category a grab is actually sent with.
///
/// Found live: every grab arrived in qBittorrent under a category called
/// "movies" rather than the configured `deluno-movies`, because the grab
/// handler passed that literal as the request category. It looked like a
/// category and behaved like one, so the fallback below never ran and the
/// Movies/TV fields on the download client were dead settings. On a fresh
/// client the invented category does not exist, so the download saved to the
/// client's default folder instead of the one the library and its processor
/// watch - which stops refine-before-import dead.
/// </summary>
public sealed class CategoryResolutionTests
{
    private static DownloadClientItem Client(string? movies = "deluno-movies", string? tv = "deluno-tv", string? template = null) =>
        new(
            Id: "qb-1",
            Name: "qBittorrent",
            Protocol: "qbittorrent",
            Host: "localhost",
            Port: 8080,
            Username: null,
            Secret: null,
            EndpointUrl: null,
            MoviesCategory: movies,
            TvCategory: tv,
            CategoryTemplate: template,
            Priority: 1,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static string Resolve(DownloadClientItem client, string mediaType, string? requestCategory) =>
        DownloadClientHelpers.ResolveCategory(
            client,
            new DownloadClientGrabRequest("Release.2026.1080p", "https://fixture.invalid/r", mediaType, requestCategory, "Fixture"));

    [Fact]
    public void With_no_routing_override_a_movie_uses_the_clients_movies_category()
    {
        Assert.Equal("deluno-movies", Resolve(Client(), "movies", null));
    }

    [Fact]
    public void With_no_routing_override_an_episode_uses_the_clients_tv_category()
    {
        Assert.Equal("deluno-tv", Resolve(Client(), "tv", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_is_not_an_override(string requestCategory)
    {
        Assert.Equal("deluno-movies", Resolve(Client(), "movies", requestCategory));
    }

    [Fact]
    public void A_real_routing_override_wins()
    {
        Assert.Equal("family-movies", Resolve(Client(), "movies", "family-movies"));
    }

    [Fact]
    public void A_client_with_no_category_of_its_own_falls_back_to_its_template()
    {
        Assert.Equal("shared", Resolve(Client(movies: null, template: "shared"), "movies", null));
    }
}
