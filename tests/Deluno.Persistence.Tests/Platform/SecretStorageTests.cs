using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class SecretStorageTests
{
    [Fact]
    public async Task Metadata_provider_secrets_are_encrypted_at_rest_and_read_back_for_internal_use()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T07:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);

        await repository.SaveAsync(
            new UpdatePlatformSettingsRequest(
                AppInstanceName: "Deluno",
                MovieRootPath: null,
                SeriesRootPath: null,
                DownloadsPath: null,
                IncompleteDownloadsPath: null,
                AutoStartJobs: false,
                EnableNotifications: false,
                RenameOnImport: true,
                UseHardlinks: true,
                CleanupEmptyFolders: false,
                RemoveCompletedDownloads: false,
                UnmonitorWhenCutoffMet: false,
                MovieFolderFormat: null,
                SeriesFolderFormat: null,
                EpisodeFileFormat: null,
                HostBindAddress: null,
                HostPort: 5099,
                UrlBase: null,
                RequireAuthentication: true,
                UiTheme: "system",
                UiDensity: "comfortable",
                DefaultMovieView: "grid",
                DefaultShowView: "grid",
                MetadataNfoEnabled: false,
                MetadataArtworkEnabled: false,
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "broker",
                MetadataBrokerUrl: null,
                MetadataTmdbApiKey: "tmdb-secret-value",
                MetadataOmdbApiKey: "omdb-secret-value",
                ReleaseNeverGrabPatterns: null),
            CancellationToken.None);

        Assert.Equal("tmdb-secret-value", await repository.GetMetadataProviderSecretAsync("tmdb", CancellationToken.None));
        Assert.Equal("omdb-secret-value", await repository.GetMetadataProviderSecretAsync("omdb", CancellationToken.None));

        AssertSecretIsEncrypted(await ReadSettingAsync(storage, "metadata.tmdbApiKey"));
        AssertSecretIsEncrypted(await ReadSettingAsync(storage, "metadata.omdbApiKey"));
    }

    [Fact]
    public async Task Indexer_and_download_client_secrets_are_encrypted_at_rest_but_available_to_internal_services()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T07:00:00Z"));
        await InitializePlatformAsync(storage, timeProvider);
        var repository = CreateRepository(storage, timeProvider);

        var indexer = await repository.CreateIndexerAsync(
            new CreateIndexerRequest(
                Name: "Indexer",
                Protocol: "torznab",
                Privacy: "private",
                BaseUrl: "https://indexer.example.test",
                ApiKey: "indexer-secret-value",
                Priority: 1,
                Categories: "2000",
                Tags: "movies",
                MediaScope: "both",
                IsEnabled: true),
            CancellationToken.None);
        var client = await repository.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                Name: "qBittorrent",
                Protocol: "qbittorrent",
                Host: "localhost",
                Port: 8080,
                Username: "deluno",
                Password: "client-secret-value",
                EndpointUrl: null,
                MoviesCategory: "movies",
                TvCategory: "tv",
                CategoryTemplate: null,
                Priority: 1,
                IsEnabled: true),
            CancellationToken.None);

        Assert.Equal("indexer-secret-value", indexer.ApiKey);
        Assert.Equal("client-secret-value", client.Secret);
        Assert.Equal("indexer-secret-value", Assert.Single(await repository.ListIndexersAsync(CancellationToken.None)).ApiKey);
        Assert.Equal("client-secret-value", Assert.Single(await repository.ListDownloadClientsAsync(CancellationToken.None)).Secret);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        AssertSecretIsEncrypted(await ReadScalarAsync<string>(
            connection,
            "SELECT api_key FROM indexer_sources LIMIT 1;"));
        AssertSecretIsEncrypted(await ReadScalarAsync<string>(
            connection,
            "SELECT secret FROM download_clients LIMIT 1;"));
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static SqlitePlatformSettingsRepository CreateRepository(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

    private static async Task<string?> ReadSettingAsync(TestStorage storage, string key)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        return await ReadScalarAsync<string>(
            connection,
            "SELECT setting_value FROM system_settings WHERE setting_key = @key;",
            ("@key", key));
    }

    private static async Task<T?> ReadScalarAsync<T>(
        System.Data.Common.DbConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        if (result is null || result is DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }

    private static void AssertSecretIsEncrypted(string? stored)
    {
        Assert.NotNull(stored);
        Assert.StartsWith("dp:v1:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", stored, StringComparison.OrdinalIgnoreCase);
    }
}
