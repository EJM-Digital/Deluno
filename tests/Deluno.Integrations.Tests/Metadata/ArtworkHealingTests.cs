using System.Net;
using System.Security.Cryptography;
using System.Text;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// #1089: artwork heals itself. A missing file comes back from the address the
/// row remembers, a body that is not an image is never kept, a failed first
/// fetch is tried again, and cleanup leaves what a cached search still shows.
/// </summary>
public sealed class ArtworkHealingTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private const string PosterUrl = "https://images.deluno.test/poster.jpg";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-artwork-healing", Guid.NewGuid().ToString("N"));
    private readonly IOptions<StoragePathOptions> _storage;
    private readonly SqliteDatabaseConnectionFactory _factory;
    private readonly FakeClock _clock = new(Now);
    private int _requests;

    public ArtworkHealingTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _storage = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        _factory = new SqliteDatabaseConnectionFactory(_storage);
        new CacheSchemaInitializer(_factory, new SqliteDatabaseMigrator(_factory, _clock), NullLogger<CacheSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private string ArtworkRoot => Path.Combine(_dataRoot, "artwork-cache");

    private static string KeyOf(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

    private TmdbMetadataProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(
            new HttpClient(new Stub(request =>
            {
                Interlocked.Increment(ref _requests);
                return respond(request);
            })),
            new ConfigurationBuilder().Build(),
            new Mock<IPlatformSettingsRepository>().Object,
            _factory,
            _storage,
            _clock,
            new Passthrough(),
            new OutboundRequestThrottle(_clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);

    private static HttpResponseMessage Image(byte[]? body = null, string type = "image/jpeg")
    {
        var content = new ByteArrayContent(body ?? Jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private async Task InsertRowAsync(string key, string url, string? localPath, DateTimeOffset fetched)
    {
        await using var connection = await _factory.OpenConnectionAsync(DelunoDatabaseNames.Cache);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO artwork_cache (cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc)
            VALUES (@k, 'movies', @u, @l, @f, NULL);
            """;
        Add(command, "@k", key);
        Add(command, "@u", url);
        Add(command, "@l", localPath);
        Add(command, "@f", fetched.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    [Fact]
    public async Task A_missing_file_is_fetched_again_from_the_row_and_served_once_for_many_viewers()
    {
        var key = KeyOf(PosterUrl);
        await InsertRowAsync(key, PosterUrl, Path.Combine(ArtworkRoot, $"{key}.jpg"), Now.AddDays(-1));
        var provider = CreateProvider(_ => Image());

        // A restore where the row is here and the file is not; five tiles ask at once.
        var assets = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetCachedArtworkAsync(key, CancellationToken.None)));

        Assert.All(assets, asset =>
        {
            Assert.NotNull(asset);
            Assert.Equal("image/jpeg", asset!.ContentType);
            Assert.Equal(Jpeg, File.ReadAllBytes(asset.FilePath));
        });
        Assert.Equal(1, _requests);
        Assert.Empty(Directory.GetFiles(ArtworkRoot, "*.tmp"));

        // Second view is served from disk.
        Assert.NotNull(await provider.GetCachedArtworkAsync(key, CancellationToken.None));
        Assert.Equal(1, _requests);
    }

    [Fact]
    public async Task A_file_with_no_row_is_found_by_its_name_without_any_request()
    {
        var key = KeyOf(PosterUrl);
        Directory.CreateDirectory(ArtworkRoot);
        await File.WriteAllBytesAsync(Path.Combine(ArtworkRoot, $"{key}.png"), [0x89, 0x50], CancellationToken.None);
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var asset = await provider.GetCachedArtworkAsync(key, CancellationToken.None);

        Assert.NotNull(asset);
        Assert.Equal("image/png", asset!.ContentType);
        Assert.Equal(0, _requests);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("")]
    public async Task A_key_that_is_not_64_hex_characters_is_refused_without_a_request(string key)
    {
        var provider = CreateProvider(_ => Image());

        Assert.Null(await provider.GetCachedArtworkAsync(key, CancellationToken.None));
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task A_row_whose_address_is_not_the_one_its_key_names_or_is_on_this_machine_is_never_fetched()
    {
        var mismatched = new string('a', 64);
        await InsertRowAsync(mismatched, PosterUrl, null, Now);
        var local = KeyOf("http://127.0.0.1/secret.jpg");
        await InsertRowAsync(local, "http://127.0.0.1/secret.jpg", null, Now);
        var provider = CreateProvider(_ => Image());

        Assert.Null(await provider.GetCachedArtworkAsync(mismatched, CancellationToken.None));
        Assert.Null(await provider.GetCachedArtworkAsync(local, CancellationToken.None));
        Assert.Equal(0, _requests);
    }

    [Theory]
    [InlineData("text/html", "<html>Sign in to the network</html>")]
    [InlineData("image/jpeg", "<html>Sign in to the network</html>")]
    [InlineData("image/jpeg", "")]
    public async Task A_body_that_is_not_an_image_is_never_cached(string contentType, string body)
    {
        var provider = CreateProvider(_ => Image(Encoding.UTF8.GetBytes(body), contentType));

        var local = await provider.CacheArtworkUrlAsync("movies", PosterUrl, CancellationToken.None);

        Assert.Equal(PosterUrl, local);
        Assert.False(Directory.Exists(ArtworkRoot) && Directory.GetFiles(ArtworkRoot).Length > 0);
    }

    [Fact]
    public async Task A_poster_that_failed_first_is_saved_the_next_time_it_is_asked_for()
    {
        var healthy = false;
        var provider = CreateProvider(_ => healthy ? Image() : new HttpResponseMessage(HttpStatusCode.BadGateway));

        Assert.Equal(PosterUrl, await provider.CacheArtworkUrlAsync("movies", PosterUrl, CancellationToken.None));

        healthy = true;
        var local = await provider.CacheArtworkUrlAsync("movies", PosterUrl, CancellationToken.None);

        Assert.Equal($"/api/metadata/artwork/{KeyOf(PosterUrl)}", local);
        Assert.True(File.Exists(Path.Combine(ArtworkRoot, $"{KeyOf(PosterUrl)}.jpg")));
    }

    [Fact]
    public async Task A_failed_fetch_leaves_the_address_on_the_row_so_the_first_view_can_repair_it()
    {
        var healthy = false;
        var provider = CreateProvider(_ => healthy ? Image() : new HttpResponseMessage(HttpStatusCode.BadGateway));
        await provider.CacheArtworkUrlAsync("movies", PosterUrl, CancellationToken.None);

        healthy = true;
        Assert.NotNull(await provider.GetCachedArtworkAsync(KeyOf(PosterUrl), CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_keeps_artwork_a_cached_search_can_still_return()
    {
        var keptKey = KeyOf("https://images.deluno.test/kept.jpg");
        var orphanKey = KeyOf("https://images.deluno.test/orphan.jpg");
        Directory.CreateDirectory(ArtworkRoot);
        foreach (var key in new[] { keptKey, orphanKey })
        {
            var path = Path.Combine(ArtworkRoot, $"{key}.jpg");
            await File.WriteAllBytesAsync(path, Jpeg, CancellationToken.None);
            File.SetLastWriteTimeUtc(path, Now.AddDays(-3).UtcDateTime);
            await InsertRowAsync(key, "https://images.deluno.test/x.jpg", path, Now.AddDays(-3));
        }

        await using (var connection = await _factory.OpenConnectionAsync(DelunoDatabaseNames.Cache))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO search_result_cache (cache_key, media_type, query_text, result_json, created_utc, expires_utc)
                VALUES ('s', 'movies', 'heat', @json, @created, @expires);
                """;
            Add(command, "@json", $"[{{\"posterUrl\":\"/api/metadata/artwork/{keptKey}\"}}]");
            Add(command, "@created", Now.AddDays(-2).ToString("O"));
            Add(command, "@expires", Now.AddDays(-1).ToString("O")); // past its life, still served when the provider is down
            await command.ExecuteNonQueryAsync();
        }

        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await provider.CleanupArtworkCacheAsync(new HashSet<string>(), Now.AddHours(-24), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(ArtworkRoot, $"{keptKey}.jpg")));
        Assert.False(File.Exists(Path.Combine(ArtworkRoot, $"{orphanKey}.jpg")));
        Assert.Equal(1, result.DeletedCount);
    }

    public void Dispose()
    {
        _factory.ClearPools();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup for test temp dirs
        }
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class FakeClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class Passthrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
        {
            var value = await operation(cancellationToken);
            _ = classifyResult(value);
            return new IntegrationResilienceResult<T>(value, false, false, 1, null, null);
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
