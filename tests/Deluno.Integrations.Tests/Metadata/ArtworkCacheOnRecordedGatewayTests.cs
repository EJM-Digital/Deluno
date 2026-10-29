using System.Net;
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
/// The artwork cache of Deluno's metadata provider with the real images the project's deployed metadata gateway served
/// (tests/fixtures/recorded/tmdb: artwork-poster and artwork-backdrop, the two images of the Sintel search result, real JPEG
/// files). A search saves each image under its address's key in a real artwork-cache folder with a row in the cache database;
/// what is then done to the row or the file is what a restore, a move or a refresh does.
/// </summary>
public sealed class ArtworkCacheOnRecordedGatewayTests : IDisposable
{
    private const string PosterUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w185/l98Zns4oQe1BSvbF4hJ8oL6VSf0.jpg";
    private const string BackdropUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w342/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-artwork-recorded-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _artworkRequests = [];

    public ArtworkCacheOnRecordedGatewayTests() => Directory.CreateDirectory(_dataRoot);

    private static byte[] PosterBytes => RecordedReplies.Tmdb("artwork-poster").Single.Reply.Bytes!;

    private static byte[] BackdropBytes => RecordedReplies.Tmdb("artwork-backdrop").Single.Reply.Bytes!;

    private static string KeyOf(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(url).ToString()))).ToLowerInvariant();

    private string ArtworkRoot => Path.Combine(_dataRoot, "artwork-cache");

    private HttpResponseMessage Gateway(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/metadata/search")
        {
            // The recorded Sintel search; its two image addresses are the two images recorded in the small sizes.
            var body = RecordedReplies.Tmdb("search-movie").Body("", ("results.0.posterUrl", PosterUrl), ("results.0.backdropUrl", BackdropUrl));
            return RecordedReplies.Json(body);
        }

        if (path.StartsWith("/artwork/", StringComparison.Ordinal))
        {
            _artworkRequests.Add(path);
            return path switch
            {
                "/artwork/w185/l98Zns4oQe1BSvbF4hJ8oL6VSf0.jpg" => RecordedReplies.Tmdb("artwork-poster").Single.Reply.ToResponse(),
                "/artwork/w342/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg" => RecordedReplies.Tmdb("artwork-backdrop").Single.Reply.ToResponse(),
                _ => RecordedReplies.Tmdb("artwork-not-found").Single.Reply.ToResponse()
            };
        }

        throw new InvalidOperationException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
    }

    private async Task<(TmdbMetadataProvider Provider, SqliteDatabaseConnectionFactory Factory)> RigAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
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
        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHandler(request => (respond ?? Gateway)(request))),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance),
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
        return (provider, factory);
    }

    private static async Task ExecuteAsync(SqliteDatabaseConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Cache);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(SqliteDatabaseConnectionFactory factory, string key)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Cache);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM artwork_cache WHERE cache_key = '{key}';";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>A search for Sintel: both images are downloaded from the gateway and saved.</summary>
    private static async Task SearchAsync(TmdbMetadataProvider provider)
    {
        var result = Assert.Single(await provider.SearchAsync(new MetadataLookupRequest("Sintel", "movies", 2010, null), CancellationToken.None));
        Assert.Equal($"/api/metadata/artwork/{KeyOf(PosterUrl)}", result.PosterUrl);
        Assert.Equal($"/api/metadata/artwork/{KeyOf(BackdropUrl)}", result.BackdropUrl);
    }

    [Fact]
    public async Task A_search_saves_each_image_the_gateway_served_byte_for_byte_under_its_keys_name()
    {
        var (provider, factory) = await RigAsync();

        await SearchAsync(provider);

        Assert.Equal(PosterBytes, await File.ReadAllBytesAsync(Path.Combine(ArtworkRoot, KeyOf(PosterUrl) + ".jpg")));
        Assert.Equal(BackdropBytes, await File.ReadAllBytesAsync(Path.Combine(ArtworkRoot, KeyOf(BackdropUrl) + ".jpg")));
        Assert.Equal(1, await CountRowsAsync(factory, KeyOf(PosterUrl)));
        var served = await provider.GetCachedArtworkAsync(KeyOf(PosterUrl), CancellationToken.None);
        Assert.Equal("image/jpeg", served!.ContentType);
    }

    [Fact]
    public async Task A_file_with_no_row_is_found_by_its_own_name_without_asking_the_gateway_and_a_row_from_another_data_folder_is_served_and_cleaned_from_this_one()
    {
        var (provider, factory) = await RigAsync();
        await SearchAsync(provider);
        var posterKey = KeyOf(PosterUrl);
        var backdropKey = KeyOf(BackdropUrl);
        var asked = _artworkRequests.Count;

        // The poster's row is lost; the backdrop's row names the folder of the computer the backup came from.
        await ExecuteAsync(factory, $"DELETE FROM artwork_cache WHERE cache_key = '{posterKey}';");
        await ExecuteAsync(factory, $@"UPDATE artwork_cache SET local_path = 'C:\Deluno\Data\artwork-cache\{backdropKey}.jpg' WHERE cache_key = '{backdropKey}';");

        var poster = await provider.GetCachedArtworkAsync(posterKey, CancellationToken.None);
        var backdrop = await provider.GetCachedArtworkAsync(backdropKey, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(Path.Combine(ArtworkRoot, posterKey + ".jpg")), poster!.FilePath);
        Assert.Equal(Path.GetFullPath(Path.Combine(ArtworkRoot, backdropKey + ".jpg")), backdrop!.FilePath);
        Assert.Equal(asked, _artworkRequests.Count);

        await ExecuteAsync(factory, "DELETE FROM search_result_cache;");

        // Cleaned from this folder: the file here goes and its bytes are counted, the other computer's path is never touched.
        var cleaned = await provider.CleanupArtworkCacheAsync(new HashSet<string>(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Equal(1, cleaned.DeletedCount);
        Assert.Equal(BackdropBytes.Length, cleaned.ReclaimedBytes);
        Assert.False(File.Exists(Path.Combine(ArtworkRoot, backdropKey + ".jpg")));
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, posterKey + ".jpg")), "the poster has no row, so cleanup does not know it");
        Assert.Equal(0, await CountRowsAsync(factory, backdropKey));
    }

    [Fact]
    public async Task A_row_naming_a_file_that_is_not_its_key_is_not_trusted_served_or_deleted()
    {
        var (provider, factory) = await RigAsync();
        await SearchAsync(provider);
        var posterKey = KeyOf(PosterUrl);
        var backdropKey = KeyOf(BackdropUrl);
        await ExecuteAsync(factory, $@"UPDATE artwork_cache SET local_path = 'C:\Deluno\Data\artwork-cache\{posterKey}.jpg' WHERE cache_key = '{backdropKey}';");

        await ExecuteAsync(factory, "DELETE FROM search_result_cache;");

        var cleaned = await provider.CleanupArtworkCacheAsync(new HashSet<string>([posterKey]), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Equal(1, cleaned.FailedCount);
        Assert.Equal(0, cleaned.DeletedCount);
        Assert.Equal(1, cleaned.SkippedReferencedCount);
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, posterKey + ".jpg")), "the other image's file is not taken for this row's");
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, backdropKey + ".jpg")));
        Assert.Equal(1, await CountRowsAsync(factory, backdropKey));
    }

    [Fact]
    public async Task Cleanup_removes_only_unreferenced_artwork_older_than_the_grace_window_and_reports_what_it_scanned_deleted_reclaimed_skipped_and_failed()
    {
        var (provider, factory) = await RigAsync();
        await SearchAsync(provider);
        var posterKey = KeyOf(PosterUrl);
        var backdropKey = KeyOf(BackdropUrl);

        // The search just made is still kept for an outage, and its images with it: nothing is deleted while a cached search names them.
        var kept = await provider.CleanupArtworkCacheAsync(new HashSet<string>(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Equal(new ArtworkCacheCleanupResult(2, 0, 0, 2, 0), kept);
        await ExecuteAsync(factory, "DELETE FROM search_result_cache;");

        // Inside the grace window nothing is old enough, referenced or not.
        var early = await provider.CleanupArtworkCacheAsync(new HashSet<string>(), DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None);
        Assert.Equal(new ArtworkCacheCleanupResult(0, 0, 0, 0, 0), early);

        var result = await provider.CleanupArtworkCacheAsync(new HashSet<string>([posterKey]), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Equal(new ArtworkCacheCleanupResult(2, 1, BackdropBytes.Length, 1, 0), result);
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, posterKey + ".jpg")));
        Assert.False(File.Exists(Path.Combine(ArtworkRoot, backdropKey + ".jpg")));
        Assert.Equal(1, await CountRowsAsync(factory, posterKey));
        Assert.Equal(0, await CountRowsAsync(factory, backdropKey));
    }

    [Fact]
    public async Task Cleanup_never_touches_a_file_written_after_the_cut_off_a_row_with_an_unknown_time_or_a_file_outside_the_managed_folder()
    {
        var (provider, factory) = await RigAsync();
        await SearchAsync(provider);
        var posterKey = KeyOf(PosterUrl);
        var backdropKey = KeyOf(BackdropUrl);
        var unknownTimeKey = new string('a', 64);
        var outsideKey = new string('b', 64);
        var outsideFolder = Directory.CreateDirectory(Path.Combine(_dataRoot, "somewhere-else")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(ArtworkRoot, unknownTimeKey + ".jpg"), PosterBytes);
        await File.WriteAllBytesAsync(Path.Combine(outsideFolder, "keep-me.jpg"), PosterBytes);
        await ExecuteAsync(factory, $"INSERT INTO artwork_cache (cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc) VALUES ('{unknownTimeKey}', 'movies', '{PosterUrl}', '{Path.Combine(ArtworkRoot, unknownTimeKey + ".jpg")}', '0000-not-a-time', NULL);");
        await ExecuteAsync(factory, $"INSERT INTO artwork_cache (cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc) VALUES ('{outsideKey}', 'movies', '{PosterUrl}', '{Path.Combine(outsideFolder, "keep-me.jpg")}', '0001-01-01T00:00:00.0000000+00:00', NULL);");
        // The poster's row is old enough to be a candidate, but its file was written a moment ago, after the cut-off.
        var cutOff = DateTimeOffset.UtcNow.AddMinutes(-30);
        await ExecuteAsync(factory, $"UPDATE artwork_cache SET fetched_utc = '{cutOff.AddHours(-1):O}' WHERE cache_key = '{posterKey}';");
        await ExecuteAsync(factory, $"DELETE FROM artwork_cache WHERE cache_key = '{backdropKey}';");
        await ExecuteAsync(factory, "DELETE FROM search_result_cache;");

        var result = await provider.CleanupArtworkCacheAsync(new HashSet<string>(), cutOff, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(ArtworkRoot, posterKey + ".jpg")), "written after the cut-off: a refresh may be writing it");
        Assert.Equal(1, await CountRowsAsync(factory, posterKey));
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, unknownTimeKey + ".jpg")), "an unknown time is not safe to age out");
        Assert.Equal(1, await CountRowsAsync(factory, unknownTimeKey));
        Assert.True(File.Exists(Path.Combine(outsideFolder, "keep-me.jpg")));
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.ReclaimedBytes);
    }

    [Fact]
    public async Task An_image_the_gateway_does_not_hold_or_a_size_it_does_not_serve_leaves_the_result_pointing_at_the_remote_address_with_nothing_saved_and_the_miss_remembered()
    {
        const string unservedSize = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w300/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg";
        const string unknownFile = "https://deluno-metadata-gateway.ejmdigital.workers.dev/artwork/w185/zzzzzzzzzzzzzzzzzzzzzzzzzzz.jpg";
        var (provider, factory) = await RigAsync(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/metadata/search")
            {
                return RecordedReplies.Json(RecordedReplies.Tmdb("search-movie").Body("", ("results.0.posterUrl", unservedSize), ("results.0.backdropUrl", unknownFile)));
            }

            return path.Contains("/w300/", StringComparison.Ordinal)
                ? RecordedReplies.Tmdb("artwork-size-not-served").Single.Reply.ToResponse()
                : RecordedReplies.Tmdb("artwork-not-found").Single.Reply.ToResponse();
        });

        var result = Assert.Single(await provider.SearchAsync(new MetadataLookupRequest("Sintel", "movies", 2010, null), CancellationToken.None));

        Assert.Equal(unservedSize, result.PosterUrl);
        Assert.Equal(unknownFile, result.BackdropUrl);
        Assert.Empty(Directory.Exists(ArtworkRoot) ? Directory.GetFiles(ArtworkRoot) : []);
        Assert.Equal(1, await CountRowsAsync(factory, KeyOf(unservedSize)));
        Assert.Null(await provider.GetCachedArtworkAsync(KeyOf(unknownFile), CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public void Dispose()
    {
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
}
