using System.Net;
using System.Text;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

public sealed class TmdbMetadataProviderTests : IDisposable
{
    private readonly string _dataRoot;

    public TmdbMetadataProviderTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    [Fact]
    public async Task SearchAsync_uses_omdb_fallback_and_rewrites_artwork_to_local_cache()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                AppInstanceName: "Deluno Test",
                MovieRootPath: null,
                SeriesRootPath: null,
                DownloadsPath: null,
                IncompleteDownloadsPath: null,
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: false,
                CleanupEmptyFolders: true,
                RemoveCompletedDownloads: false,
                UnmonitorWhenCutoffMet: false,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1",
                HostPort: 5099,
                UrlBase: string.Empty,
                RequireAuthentication: true,
                UiTheme: "system",
                UiDensity: "comfortable",
                DefaultMovieView: "grid",
                DefaultShowView: "grid",
                MetadataNfoEnabled: false,
                MetadataArtworkEnabled: true,
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "direct",
                MetadataBrokerUrl: string.Empty,
                MetadataBrokerConfigured: false,
                MetadataTmdbApiKeyConfigured: false,
                MetadataOmdbApiKeyConfigured: true,
                ReleaseNeverGrabPatterns: string.Empty,
                SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30,
                UpdatedUtc: DateTimeOffset.UtcNow));
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync("omdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync("omdb-test-key");

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(factory, timeProvider);
        await new CacheSchemaInitializer(factory, migrator, NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.StartsWith("https://www.omdbapi.com/", StringComparison.OrdinalIgnoreCase) && uri.Contains("&s=", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                {
                  "Search": [
                    { "Title": "Inception", "Year": "2010", "imdbID": "tt1375666", "Poster": "https://img.deluno.test/inception.jpg" }
                  ],
                  "Response": "True"
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }

            if (uri.StartsWith("https://www.omdbapi.com/", StringComparison.OrdinalIgnoreCase) && uri.Contains("&i=tt1375666", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                {
                  "Plot": "A thief who steals corporate secrets through dream-sharing technology.",
                  "Genre": "Action, Sci-Fi, Thriller",
                  "imdbRating": "8.8",
                  "imdbVotes": "2200000",
                  "Metascore": "74",
                  "Ratings": [{ "Source": "Rotten Tomatoes", "Value": "87%" }]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }

            if (uri.Equals("https://img.deluno.test/inception.jpg", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4, 5])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new TmdbMetadataProvider(
            new HttpClient(handler),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            NullLogger<TmdbMetadataProvider>.Instance);

        var results = await provider.SearchAsync(
            new MetadataLookupRequest("Inception", "movies", 2010, null),
            CancellationToken.None);

        var first = Assert.Single(results);
        Assert.Equal("omdb", first.Provider);
        Assert.NotNull(first.PosterUrl);
        Assert.StartsWith("/api/metadata/artwork/", first.PosterUrl, StringComparison.Ordinal);

        var cacheKey = first.PosterUrl!.Split('/').Last();
        var cached = await provider.GetCachedArtworkAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(cached);
        Assert.True(File.Exists(cached!.FilePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup for test temp dirs
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class PassthroughResiliencePolicy : IIntegrationResiliencePolicy
    {
        public Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => ExecuteInternalAsync(operation, classifyResult, cancellationToken);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        private static async Task<IntegrationResilienceResult<T>> ExecuteInternalAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
        {
            var value = await operation(cancellationToken);
            _ = classifyResult(value);
            return new IntegrationResilienceResult<T>(value, false, false, 1, null, null);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
