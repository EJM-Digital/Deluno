using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// #1522: Add movie search waited for every result's poster and backdrop before showing anything (8.5 s for one search).
/// The metadata answer is the recorded Sintel search; the image host is slow.
/// </summary>
public sealed class ASearchDoesNotWaitForArtworkTests : IDisposable
{
    private const string PosterUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w185/l98Zns4oQe1BSvbF4hJ8oL6VSf0.jpg";
    private const string BackdropUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w342/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-search-speed-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _imageRequests = [];

    private static string KeyOf(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(url).ToString()))).ToLowerInvariant();

    private async Task<TmdbMetadataProvider> RigAsync(TimeSpan imageDelay)
    {
        Directory.CreateDirectory(_dataRoot);
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null, IncompleteDownloadsPath: null,
                AutoStartJobs: true, EnableNotifications: true, RenameOnImport: true, UseHardlinks: false,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1", HostPort: 5099, UrlBase: string.Empty,
                RequireAuthentication: true, UiTheme: "system", DefaultMovieView: "grid", DefaultShowView: "grid",
                MetadataCertificationCountry: "US", MetadataLanguage: "en", MetadataProviderMode: "broker",
                MetadataBrokerUrl: "https://metadata.deluno.test", MetadataBrokerConfigured: true,
                MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));
        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var clock = TimeProvider.System;
        await new CacheSchemaInitializer(factory, new SqliteDatabaseMigrator(factory, clock), NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new TmdbMetadataProvider(
            new HttpClient(new SlowImageHost(Answer, imageDelay)),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance),
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/metadata/search")
        {
            return RecordedReplies.Json(RecordedReplies.Tmdb("search-movie").Body("", ("results.0.posterUrl", PosterUrl), ("results.0.backdropUrl", BackdropUrl)));
        }

        lock (_imageRequests)
        {
            _imageRequests.Add(path);
        }

        return path.Contains("w185", StringComparison.Ordinal)
            ? RecordedReplies.Tmdb("artwork-poster").Single.Reply.ToResponse()
            : RecordedReplies.Tmdb("artwork-backdrop").Single.Reply.ToResponse();
    }

    [Fact]
    public async Task A_slow_image_host_does_not_delay_the_search_answer_and_the_poster_is_saved_afterwards_but_the_backdrop_is_not_fetched()
    {
        var provider = await RigAsync(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();

        var result = Assert.Single(await provider.SearchAsync(new MetadataLookupRequest("Sintel", "movies", 2010, null), CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the search waited {clock.Elapsed} for images");
        // Never an address that will 404: the remote one until the poster is saved.
        Assert.Equal(PosterUrl, result.PosterUrl);

        await TmdbMetadataProvider.WaitForPosterSavesAsync();
        Assert.True(File.Exists(Path.Combine(_dataRoot, "artwork-cache", KeyOf(PosterUrl) + ".jpg")));
        Assert.DoesNotContain(_imageRequests, path => path.Contains("w342", StringComparison.Ordinal));

        // The same search again (from the search cache) now carries Deluno's own address for the poster.
        var again = Assert.Single(await provider.SearchAsync(new MetadataLookupRequest("Sintel", "movies", 2010, null), CancellationToken.None));
        Assert.Equal($"/api/metadata/artwork/{KeyOf(PosterUrl)}", again.PosterUrl);
        Assert.Equal(BackdropUrl, again.BackdropUrl);
    }

    [Fact]
    public async Task Adding_a_title_still_stores_its_poster_and_backdrop()
    {
        var provider = await RigAsync(TimeSpan.Zero);
        var result = Assert.Single(await provider.SearchAsync(new MetadataLookupRequest("Sintel", "movies", 2010, null), CancellationToken.None));

        var poster = await provider.CacheArtworkUrlAsync("movies", result.PosterUrl, CancellationToken.None);
        var backdrop = await provider.CacheArtworkUrlAsync("movies", result.BackdropUrl, CancellationToken.None);

        Assert.Equal($"/api/metadata/artwork/{KeyOf(PosterUrl)}", poster);
        Assert.Equal($"/api/metadata/artwork/{KeyOf(BackdropUrl)}", backdrop);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "artwork-cache", KeyOf(BackdropUrl) + ".jpg")));
    }

    private sealed class SlowImageHost(Func<HttpRequestMessage, HttpResponseMessage> send, TimeSpan imageDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/artwork/", StringComparison.Ordinal) && imageDelay > TimeSpan.Zero)
            {
                await Task.Delay(imageDelay, cancellationToken); // reason: the slow image host is the thing under test
            }

            return send(request);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, true); } catch (IOException) { }
    }
}
