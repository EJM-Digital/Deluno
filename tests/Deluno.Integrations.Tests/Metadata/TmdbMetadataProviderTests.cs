using System.Net;
using System.Text;
using Deluno.Contracts;
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
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1",
                HostPort: 5099,
                UrlBase: string.Empty,
                RequireAuthentication: true,
                UiTheme: "system",
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
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
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

    [Fact]
    public async Task CleanupArtworkCacheAsync_removes_only_old_unreferenced_artwork()
    {
        var now = DateTimeOffset.Parse("2026-05-14T00:00:00Z");
        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(now);
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        var artworkRoot = Path.Combine(_dataRoot, "artwork-cache");
        Directory.CreateDirectory(artworkRoot);
        var referencedKey = new string('a', 64);
        var orphanKey = new string('b', 64);
        var missingKey = new string('c', 64);
        var referencedPath = Path.Combine(artworkRoot, $"{referencedKey}.jpg");
        var orphanPath = Path.Combine(artworkRoot, $"{orphanKey}.jpg");
        var missingPath = Path.Combine(artworkRoot, $"{missingKey}.jpg");
        await File.WriteAllBytesAsync(referencedPath, [1, 2, 3], CancellationToken.None);
        await File.WriteAllBytesAsync(orphanPath, [4, 5, 6, 7, 8], CancellationToken.None);
        File.SetLastWriteTimeUtc(referencedPath, now.AddDays(-3).UtcDateTime);
        File.SetLastWriteTimeUtc(orphanPath, now.AddDays(-3).UtcDateTime);

        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Cache))
        {
            async Task InsertAsync(string key, string? localPath)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO artwork_cache (
                        cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc)
                    VALUES (@cacheKey, 'movies', 'https://images.deluno.test/art.jpg', @localPath, @fetchedUtc, NULL);
                    """;

                var cacheKeyParameter = command.CreateParameter();
                cacheKeyParameter.ParameterName = "@cacheKey";
                cacheKeyParameter.Value = key;
                command.Parameters.Add(cacheKeyParameter);
                var localPathParameter = command.CreateParameter();
                localPathParameter.ParameterName = "@localPath";
                localPathParameter.Value = localPath is null ? DBNull.Value : localPath;
                command.Parameters.Add(localPathParameter);
                var fetchedParameter = command.CreateParameter();
                fetchedParameter.ParameterName = "@fetchedUtc";
                fetchedParameter.Value = now.AddDays(-3).ToString("O");
                command.Parameters.Add(fetchedParameter);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await InsertAsync(referencedKey, referencedPath);
            await InsertAsync(orphanKey, orphanPath);
            await InsertAsync(missingKey, missingPath);
        }

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new ConfigurationBuilder().Build(),
            new Mock<IPlatformSettingsRepository>().Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = await provider.CleanupArtworkCacheAsync(
            new HashSet<string>([referencedKey], StringComparer.OrdinalIgnoreCase),
            now.AddHours(-24),
            CancellationToken.None);

        Assert.Equal(3, result.ScannedCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(5, result.ReclaimedBytes);
        Assert.Equal(1, result.SkippedReferencedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(File.Exists(referencedPath));
        Assert.False(File.Exists(orphanPath));

        await using var verifyConnection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Cache);
        using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM artwork_cache WHERE cache_key IN (@referenced, @orphan, @missing);";
        var referencedParameter = verifyCommand.CreateParameter();
        referencedParameter.ParameterName = "@referenced";
        referencedParameter.Value = referencedKey;
        verifyCommand.Parameters.Add(referencedParameter);
        var orphanParameter = verifyCommand.CreateParameter();
        orphanParameter.ParameterName = "@orphan";
        orphanParameter.Value = orphanKey;
        verifyCommand.Parameters.Add(orphanParameter);
        var missingParameter = verifyCommand.CreateParameter();
        missingParameter.ParameterName = "@missing";
        missingParameter.Value = missingKey;
        verifyCommand.Parameters.Add(missingParameter);
        Assert.Equal(1L, (long)(await verifyCommand.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task GetDirectStatusAsync_prefers_host_configuration_before_legacy_install_secret()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync("tmdb", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Legacy secret should not be read when the host config is present."));
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync("omdb", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "host-managed-key"
                })
                .Build(),
            settings.Object,
            new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot })),
            Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
            TimeProvider.System,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(TimeProvider.System, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var status = await provider.GetDirectStatusAsync(CancellationToken.None);

        Assert.True(status.IsConfigured);
        Assert.Equal("direct", status.Mode);
    }

    [Fact]
    public async Task SearchAsync_uses_the_managed_broker_without_a_direct_provider_key()
    {
        var settings = new Mock<IPlatformSettingsRepository>(MockBehavior.Strict);
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("broker", "https://metadata.deluno.test"));

        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(
                "https://metadata.deluno.test/metadata/search?mediaType=movies&query=Interstellar&year=2014",
                request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "provider": "deluno-broker",
                      "mode": "broker",
                      "resultCount": 1,
                      "results": [{
                        "provider": "tmdb",
                        "providerId": "157336",
                        "mediaType": "movies",
                        "title": "Interstellar",
                        "year": 2014,
                        "overview": "A controlled broker result.",
                        "posterUrl": null,
                        "backdropUrl": null,
                        "rating": 8.5,
                        "ratings": [],
                        "genres": ["Adventure"],
                        "imdbId": "tt0816692",
                        "externalUrl": "https://www.themoviedb.org/movie/157336",
                        "cast": []
                      }]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, TimeProvider.System),
            NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var provider = new TmdbMetadataProvider(
            new HttpClient(handler),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            TimeProvider.System,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(TimeProvider.System, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest("Interstellar", "movies", 2014, null),
            CancellationToken.None));

        Assert.Equal("Interstellar", result.Title);
        Assert.Equal("tmdb", result.Provider);
        settings.Verify(repo => repo.GetMetadataProviderSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A provider id reaches the broker, and what comes back is the detail
    /// record rather than a page of candidates.
    ///
    /// <para><b>This is the wire contract the "enrich" step depends on.</b> The
    /// managed gateway answers a lookup carrying a <c>providerId</c> with one
    /// record holding cast, crew, runtime and certification; without it, cards
    /// with none of that. Deluno's own <c>/api/metadata/search</c> spent a long
    /// time binding the parameter nowhere and passing <c>null</c>, so the two
    /// calls were the same call and every chosen title was stored with card
    /// metadata. The endpoint is held to this elsewhere; this holds the half
    /// that has to travel over the wire.</para>
    /// </summary>
    [Theory]
    [InlineData("movies", "329865")]
    [InlineData("tv", "95396")]
    public async Task SearchAsync_asks_the_broker_for_one_record_when_it_is_given_a_provider_id(
        string mediaType,
        string providerId)
    {
        var settings = new Mock<IPlatformSettingsRepository>(MockBehavior.Strict);
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("broker", "https://metadata.deluno.test"));

        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(
                $"https://metadata.deluno.test/metadata/search?mediaType={mediaType}&query=Anything&providerId={providerId}",
                request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "provider": "deluno-broker",
                      "mode": "broker",
                      "resultCount": 1,
                      "results": [{
                        "provider": "tmdb",
                        "providerId": "{{providerId}}",
                        "mediaType": "{{mediaType}}",
                        "title": "The detail record",
                        "year": 2016,
                        "overview": "Everything a search card does not carry.",
                        "posterUrl": null,
                        "backdropUrl": null,
                        "rating": 7.6,
                        "ratings": [],
                        "genres": ["Drama"],
                        "imdbId": "tt2543164",
                        "externalUrl": "https://www.themoviedb.org/movie/329865",
                        "runtimeMinutes": 116,
                        "certification": "PG-13",
                        "cast": [{ "name": "Amy Adams", "character": "Louise Banks" }],
                        "crew": [{ "name": "Denis Villeneuve", "job": "Director" }]
                      }]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, TimeProvider.System),
            NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var provider = new TmdbMetadataProvider(
            new HttpClient(handler),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            TimeProvider.System,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(TimeProvider.System, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest("Anything", mediaType, null, providerId),
            CancellationToken.None));

        Assert.Equal(providerId, result.ProviderId);
        Assert.Equal(116, result.RuntimeMinutes);
        Assert.Equal("PG-13", result.Certification);
        Assert.NotNull(result.Cast);
        Assert.NotEmpty(result.Cast);
        Assert.NotNull(result.Crew);
        Assert.NotEmpty(result.Crew);
    }

    /// <summary>
    /// A `tmdb:<id>` prefix (#792) is the same identity assertion the add
    /// dialog already sends once a title is selected from search results -
    /// it goes straight to the detail lookup, never through `search/movie`.
    /// </summary>
    [Fact]
    public async Task SearchAsync_resolves_a_tmdb_prefixed_id_directly()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var requestedUrls = new List<string>();
        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!.ToString();
                requestedUrls.Add(uri);
                if (uri.Contains("/movie/157336", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{ "id": 157336, "title": "Interstellar", "release_date": "2014-11-05" }""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Metadata:TMDbApiKey"] = "test-key" })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest(null, "movies", null, "157336", "tmdb"),
            CancellationToken.None));

        Assert.Equal("Interstellar", result.Title);
        Assert.Equal("157336", result.ProviderId);
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/search/", StringComparison.Ordinal));
    }

    /// <summary>
    /// An `imdb:<id>` or `tvdb:<id>` prefix (#792) has no TMDb numeric id to
    /// look up directly, so it goes through TMDb's `/find` endpoint first and
    /// then fetches the full detail record for whichever bucket answered.
    /// </summary>
    [Theory]
    [InlineData("imdb", "tt0816692", "imdb_id", "movies", "movie_results")]
    [InlineData("tvdb", "121361", "tvdb_id", "tv", "tv_results")]
    public async Task SearchAsync_resolves_an_external_id_prefix_via_the_find_endpoint(
        string idSource,
        string externalId,
        string expectedExternalSource,
        string mediaType,
        string resultsBucket)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains($"/find/{externalId}", StringComparison.Ordinal))
                {
                    Assert.Contains($"external_source={expectedExternalSource}", uri, StringComparison.Ordinal);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $$"""{ "{{resultsBucket}}": [{ "id": 42 }] }""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (uri.Contains(mediaType == "tv" ? "/tv/42" : "/movie/42", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            mediaType == "tv"
                                ? """{ "id": 42, "name": "Doctor Who", "first_air_date": "2005-03-26" }"""
                                : """{ "id": 42, "title": "Interstellar", "release_date": "2014-11-05" }""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Metadata:TMDbApiKey"] = "test-key" })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest(null, mediaType, null, externalId, idSource),
            CancellationToken.None));

        Assert.Equal("42", result.ProviderId);
    }

    /// <summary>An id prefix that TMDb has never heard of returns nothing - never a fuzzy fallback list.</summary>
    [Fact]
    public async Task SearchAsync_returns_no_results_for_an_id_prefix_tmdb_does_not_recognize()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains("/find/tt9999999", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{ "movie_results": [], "tv_results": [] }""", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Metadata:TMDbApiKey"] = "test-key" })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var results = await provider.SearchAsync(
            new MetadataLookupRequest(null, "movies", null, "tt9999999", "imdb"),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_retains_a_typed_provider_failure_in_status()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, TimeProvider.System),
            NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            TimeProvider.System,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(TimeProvider.System, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        Assert.Empty(await provider.SearchAsync(
            new MetadataLookupRequest("The Matrix", "movies", 1999, null),
            CancellationToken.None));

        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal(IntegrationFailureKind.Authentication, status.LastFailure!.Kind);
        Assert.Equal("metadata.tmdb.search", status.LastFailure.Operation);
        Assert.Equal(401, status.LastFailure.HttpStatus);
        Assert.Equal(IntegrationRetryState.ManualAction, status.LastFailure.RetryState);
    }

    [Fact]
    public async Task ResolveProviderRecordAsync_reports_confirmed_404_without_fuzzy_search()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var requestedUrls = new List<string>();
        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("State of Siege: Temple Attack", "movies", 2021, "1603343"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Missing, lookup.Status);
        Assert.Equal(404, lookup.Failure?.HttpStatus);
        Assert.Single(requestedUrls);
        Assert.Contains("/movie/1603343", requestedUrls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("/search/", requestedUrls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveProviderRecordAsync_retries_a_transient_503_and_never_calls_it_a_deletion()
    {
        // The dangerous confusion in #357 is the one that looks harmless: a
        // provider that is merely down answers with a status code too, and if
        // that were read the way a 404 is read, an outage would tell the owner
        // their title had been removed from TMDb. Only 404 is a deletion.
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var requestedUrls = new List<string>();
        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                requestedUrls.Add(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            // The real policy, not the passthrough one: the retry and the
            // backoff are half of what this line of the issue asks for, and a
            // stub that runs the call once would prove neither.
            new IntegrationResiliencePolicy(
                timeProvider,
                factory,
                NullLogger<IntegrationResiliencePolicy>.Instance),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("State of Siege: Temple Attack", "movies", 2021, "1603343"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Unavailable, lookup.Status);
        Assert.NotEqual(MetadataProviderRecordStatus.Missing, lookup.Status);
        Assert.Equal(503, lookup.Failure?.HttpStatus);

        // Retried rather than believed the first time, and paced between the
        // attempts instead of hammering a provider that is already struggling.
        Assert.Equal(3, requestedUrls.Count);
        Assert.All(requestedUrls, url => Assert.Contains("/movie/1603343", url, StringComparison.Ordinal));
        Assert.Equal(3, lookup.Failure?.Attempts);
        Assert.NotEqual(IntegrationRetryState.NotRetryable, lookup.Failure!.RetryState);

        // And an outage still must not become a fuzzy re-match: guessing a new
        // identity while the provider is down is how a library silently
        // relinks itself to the wrong titles.
        Assert.DoesNotContain(requestedUrls, url => url.Contains("/search/", StringComparison.Ordinal));
    }

    /// <summary>
    /// #509: TMDb matches <c>query</c> against titles only, so a typed year has
    /// to arrive as the year filter. The fake below behaves that way: a query
    /// carrying the year finds nothing, exactly as the rig measured.
    /// </summary>
    [Theory]
    [InlineData("movies", "Night of the Living Dead 1968", "Night of the Living Dead", "1968", "Night of the Living Dead", "1968-10-04")]
    [InlineData("movies", "Night of the Living Dead (1968)", "Night of the Living Dead", "1968", "Night of the Living Dead", "1968-10-04")]
    [InlineData("movies", "1917", "1917", null, "1917", "2019-12-25")]
    [InlineData("movies", "Blade Runner 2049", "Blade Runner 2049", null, "Blade Runner 2049", "2017-10-04")]
    [InlineData("movies", "Wonder Woman 1984", "Wonder Woman 1984", null, "Wonder Woman 1984", "2020-12-16")]
    [InlineData("tv", "Battlestar Galactica 2004", "Battlestar Galactica", "2004", "Battlestar Galactica", "2004-10-18")]
    public async Task SearchAsync_sends_a_typed_year_as_the_year_filter_and_keeps_numbers_that_are_titles(
        string mediaType,
        string typed,
        string expectedQuery,
        string? expectedYear,
        string title,
        string releaseDate)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var searches = new List<(string Query, string? Year)>();
        var yearParameter = mediaType == "tv" ? "first_air_date_year" : "year";
        var dateField = mediaType == "tv" ? "first_air_date" : "release_date";
        var titleField = mediaType == "tv" ? "name" : "title";
        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (!uri.AbsolutePath.Contains("/search/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var query = parameters["query"]!;
                var year = parameters[yearParameter];
                searches.Add((query, year));

                // Title-only matching, and the year filter narrowing it.
                var matches = string.Equals(query, title, StringComparison.OrdinalIgnoreCase) &&
                              (year is null || releaseDate.StartsWith(year, StringComparison.Ordinal));
                var body = matches
                    ? $$"""{ "results": [{ "id": 42, "{{titleField}}": "{{title}}", "{{dateField}}": "{{releaseDate}}" }] }"""
                    : """{ "results": [] }""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest(typed, mediaType, null, null),
            CancellationToken.None));

        Assert.Equal(title, result.Title);
        Assert.Equal(int.Parse(releaseDate[..4], System.Globalization.CultureInfo.InvariantCulture), result.Year);
        Assert.Contains((expectedQuery, expectedYear), searches);
    }

    /// <summary>#736: a direct TMDb search/detail request must carry the saved
    /// details language, and an unset language must not force one TMDb would
    /// not otherwise have applied.</summary>
    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("", null)]
    public async Task SearchAsync_sends_the_saved_language_as_the_tmdb_language_parameter(
        string savedLanguage,
        string? expectedLanguageParameter)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty, language: savedLanguage));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        string? capturedLanguage = null;
        var sawLanguageParameter = false;
        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (!uri.AbsolutePath.Contains("/search/movie", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
                sawLanguageParameter = parameters["language"] is not null;
                capturedLanguage = parameters["language"];
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "results": [{ "id": 603, "title": "The Matrix", "release_date": "1999-03-30" }] }""",
                        Encoding.UTF8,
                        "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var result = Assert.Single(await provider.SearchAsync(
            new MetadataLookupRequest("The Matrix", "movies", null, null),
            CancellationToken.None));

        Assert.Equal("The Matrix", result.Title);
        if (expectedLanguageParameter is null)
        {
            Assert.False(sawLanguageParameter);
        }
        else
        {
            Assert.Equal(expectedLanguageParameter, capturedLanguage);
        }
    }

    /// <summary>#736: the certification a movie or show carries is the one the
    /// saved ratings-region country reports, and a country TMDb has nothing
    /// for falls back to the US board — the certification Deluno showed
    /// before a region setting existed.</summary>
    [Theory]
    [InlineData("movies", "AU", "M")]
    [InlineData("movies", "DE", "PG-13")]
    [InlineData("tv", "AU", "M")]
    [InlineData("tv", "DE", "TV-14")]
    public async Task ResolveProviderRecordAsync_picks_the_certification_for_the_saved_region(
        string mediaType,
        string certificationCountry,
        string expectedCertification)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty, certificationCountry: certificationCountry));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                string payload;
                if (uri.AbsolutePath.Contains("/movie/", StringComparison.Ordinal))
                {
                    payload = """
                    {
                      "id": 603,
                      "title": "The Matrix",
                      "release_date": "1999-03-30",
                      "release_dates": {
                        "results": [
                          { "iso_3166_1": "US", "release_dates": [{ "type": 3, "release_date": "1999-03-30T00:00:00.000Z", "certification": "PG-13" }] },
                          { "iso_3166_1": "AU", "release_dates": [{ "type": 3, "release_date": "1999-04-08T00:00:00.000Z", "certification": "M" }] }
                        ]
                      }
                    }
                    """;
                }
                else if (uri.AbsolutePath.Contains("/tv/", StringComparison.Ordinal))
                {
                    payload = """
                    {
                      "id": 1399,
                      "name": "Game of Thrones",
                      "first_air_date": "2011-04-17",
                      "content_ratings": {
                        "results": [
                          { "iso_3166_1": "US", "rating": "TV-14" },
                          { "iso_3166_1": "AU", "rating": "M" }
                        ]
                      }
                    }
                    """;
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var providerId = mediaType == "tv" ? "1399" : "603";
        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(null, mediaType, null, providerId),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Equal(expectedCertification, lookup.Result?.Certification);
    }

    /// <summary>
    /// #861: a release can be filed under a region-specific alternate title
    /// TMDb's alternative_titles carries beyond Title/OriginalTitle. The
    /// primary and original titles are excluded from the stored list — they
    /// are matched already — and the same spelling reported for more than one
    /// country is kept only once.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public async Task ResolveProviderRecordAsync_stores_alternate_titles_without_the_primary_or_duplicates(string mediaType)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                string payload;
                if (uri.AbsolutePath.Contains("/movie/", StringComparison.Ordinal))
                {
                    payload = """
                    {
                      "id": 603,
                      "title": "The Matrix",
                      "original_title": "The Matrix",
                      "release_date": "1999-03-30",
                      "alternative_titles": {
                        "titles": [
                          { "iso_3166_1": "US", "title": "The Matrix" },
                          { "iso_3166_1": "FR", "title": "Matrix" },
                          { "iso_3166_1": "DE", "title": "Matrix" }
                        ]
                      }
                    }
                    """;
                }
                else if (uri.AbsolutePath.Contains("/tv/", StringComparison.Ordinal))
                {
                    payload = """
                    {
                      "id": 1399,
                      "name": "Game of Thrones",
                      "original_name": "Game of Thrones",
                      "first_air_date": "2011-04-17",
                      "alternative_titles": {
                        "results": [
                          { "iso_3166_1": "US", "title": "Game of Thrones" },
                          { "iso_3166_1": "FR", "title": "Le Trone de Fer" },
                          { "iso_3166_1": "DE", "title": "Le Trone de Fer" }
                        ]
                      }
                    }
                    """;
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var providerId = mediaType == "tv" ? "1399" : "603";
        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(null, mediaType, null, providerId),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        var expected = mediaType == "tv" ? "Le Trone de Fer" : "Matrix";
        Assert.Equal([expected], lookup.Result?.AlternateTitles);
    }

    /// <summary>
    /// #864: TMDb's images append gives a logo and extra backdrops beyond the
    /// single poster/backdrop pair Deluno already stores. The primary
    /// backdrop must not be repeated in the gallery, and a title with none of
    /// this must leave both fields null rather than empty placeholders.
    /// </summary>
    [Fact]
    public async Task ResolveProviderRecordAsync_maps_logo_and_extra_backdrops_when_tmdb_has_them()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (!uri.AbsolutePath.Contains("/movie/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                const string payload = """
                {
                  "id": 603,
                  "title": "The Matrix",
                  "release_date": "1999-03-30",
                  "backdrop_path": "/primary-backdrop.jpg",
                  "images": {
                    "backdrops": [
                      { "file_path": "/primary-backdrop.jpg" },
                      { "file_path": "/extra-backdrop-1.jpg" },
                      { "file_path": "/extra-backdrop-2.jpg" }
                    ],
                    "logos": [
                      { "file_path": "/matrix-logo.png" }
                    ]
                  }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(null, "movies", null, "603"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Equal("https://image.tmdb.org/t/p/w300/matrix-logo.png", lookup.Result?.LogoUrl);
        Assert.Equal(
            [
                "https://image.tmdb.org/t/p/original/extra-backdrop-1.jpg",
                "https://image.tmdb.org/t/p/original/extra-backdrop-2.jpg"
            ],
            lookup.Result?.Screenshots);
        Assert.Null(lookup.Result?.BannerUrl);
    }

    [Fact]
    public async Task ResolveProviderRecordAsync_leaves_logo_and_screenshots_null_when_tmdb_has_none()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (!uri.AbsolutePath.Contains("/movie/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                const string payload = """
                {
                  "id": 603,
                  "title": "The Matrix",
                  "release_date": "1999-03-30"
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(null, "movies", null, "603"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Null(lookup.Result?.LogoUrl);
        Assert.Null(lookup.Result?.Screenshots);
        Assert.Null(lookup.Result?.BannerUrl);
    }

    /// <summary>
    /// #860: TMDb only gives a calendar date per episode, so every episode
    /// used to collapse to exactly midnight UTC. TVmaze's airstamp is the
    /// network's real broadcast instant; an episode it has data for should
    /// use that instead, and an episode it does not match should keep the
    /// existing midnight-UTC fallback rather than fail the whole catalogue.
    /// </summary>
    [Fact]
    public async Task GetSeriesCatalogueAsync_uses_tvmazes_real_broadcast_time_and_falls_back_to_midnight()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSettingsSnapshot("direct", string.Empty));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                string payload;
                if (uri.AbsolutePath.Contains("/external_ids", StringComparison.Ordinal))
                {
                    payload = """{ "imdb_id": "tt0944947" }""";
                }
                else if (uri.AbsolutePath.Contains("/season/", StringComparison.Ordinal))
                {
                    payload = """
                    {
                      "episodes": [
                        { "episode_number": 1, "name": "Winter Is Coming", "air_date": "2011-04-17" },
                        { "episode_number": 2, "name": "The Kingsroad", "air_date": "2011-04-24" }
                      ]
                    }
                    """;
                }
                else if (uri.Host.Equals("api.tvmaze.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/lookup/shows", StringComparison.Ordinal))
                {
                    payload = """{ "id": 82 }""";
                }
                else if (uri.Host.Equals("api.tvmaze.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/episodes", StringComparison.Ordinal))
                {
                    // Only episode 1 has a TVmaze match; episode 2 must fall back.
                    payload = """[ { "season": 1, "number": 1, "airstamp": "2011-04-17T21:00:00-04:00" } ]""";
                }
                else if (uri.AbsolutePath.Contains("/tv/", StringComparison.Ordinal))
                {
                    payload = """{ "seasons": [ { "season_number": 1, "name": "Season 1", "air_date": "2011-04-17", "episode_count": 2 } ] }""";
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            })),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deluno:Metadata:TMDbApiKey"] = "test-key"
                })
                .Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughResiliencePolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

        var seasons = await provider.GetSeriesCatalogueAsync("1399", CancellationToken.None);

        var season = Assert.Single(seasons);
        var episode1 = season.Episodes.Single(episode => episode.EpisodeNumber == 1);
        var episode2 = season.Episodes.Single(episode => episode.EpisodeNumber == 2);
        Assert.Equal(DateTimeOffset.Parse("2011-04-17T21:00:00-04:00"), episode1.AirDateUtc);
        Assert.Equal(new DateTimeOffset(2011, 4, 24, 0, 0, 0, TimeSpan.Zero), episode2.AirDateUtc);
    }

    private static PlatformSettingsSnapshot CreateSettingsSnapshot(
        string providerMode,
        string brokerUrl,
        string language = "en",
        string certificationCountry = "US")
        => new(
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
            MovieFolderFormat: "{Movie Title} ({Release Year})",
            SeriesFolderFormat: "{Series Title} ({Series Year})",
            MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
            EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            HostBindAddress: "127.0.0.1",
            HostPort: 5099,
            UrlBase: string.Empty,
            RequireAuthentication: true,
            UiTheme: "system",
            DefaultMovieView: "grid",
            DefaultShowView: "grid",
            MetadataNfoEnabled: false,
            MetadataArtworkEnabled: true,
            MetadataCertificationCountry: certificationCountry,
            MetadataLanguage: language,
            MetadataProviderMode: providerMode,
            MetadataBrokerUrl: brokerUrl,
            MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(brokerUrl),
            MetadataTmdbApiKeyConfigured: false,
            MetadataOmdbApiKeyConfigured: false,
            ReleaseNeverGrabPatterns: string.Empty,
            SearchScoringMode: SearchScoringModes.Hybrid,
            ImportRecoveryRetentionDays: 30,
            UpdatedUtc: DateTimeOffset.UtcNow);

    public void Dispose()
    {
        // Same reason as TestStorage: the connection pool holds the file open,
        // so a plain delete fails and leaves the folder behind. 967 of these
        // had accumulated by 3 September.
        // Only this test's databases; ClearAllPools() also closed the pooled
        // connections of tests running alongside it.
        new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot })).ClearPools();

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

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;

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
