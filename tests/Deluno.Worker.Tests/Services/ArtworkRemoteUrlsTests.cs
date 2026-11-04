using Deluno.Infrastructure.Storage;
using Deluno.Worker.Services;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1104: a cached artwork file remembers the address it came from, so a missing file can be fetched again.</summary>
public sealed class ArtworkRemoteUrlsTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-artwork-urls", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabaseConnectionFactory _factory;

    public ArtworkRemoteUrlsTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot }));
        using var connection = _factory.OpenConnectionAsync(DelunoDatabaseNames.Cache, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE artwork_cache (cache_key TEXT PRIMARY KEY, media_type TEXT NOT NULL, remote_url TEXT NOT NULL, local_path TEXT NULL, fetched_utc TEXT NOT NULL, expires_utc TEXT NULL);
            INSERT INTO artwork_cache VALUES ('abc', 'movie', 'https://images.example.test/poster.jpg', NULL, '2026-10-01T00:00:00Z', NULL);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_cached_file_gives_back_the_address_it_was_downloaded_from()
    {
        var url = await new ArtworkRemoteUrls(_factory).FindRemoteUrlAsync("abc", CancellationToken.None);

        Assert.Equal("https://images.example.test/poster.jpg", url);
    }

    [Fact]
    public async Task A_file_that_was_never_cached_has_no_address()
    {
        var url = await new ArtworkRemoteUrls(_factory).FindRemoteUrlAsync("missing", CancellationToken.None);

        Assert.Null(url);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, true); } catch (IOException) { }
    }
}
