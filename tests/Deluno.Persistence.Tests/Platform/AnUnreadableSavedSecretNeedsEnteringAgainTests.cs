using System.Security.Cryptography;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1094: a secret saved on another computer (or under another key) cannot be
/// read here. That must never fail a list: the connection is listed with its
/// secret missing and a plain-words reason, and entering the secret again
/// clears it.
/// </summary>
public sealed class AnUnreadableSavedSecretNeedsEnteringAgainTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-01T10:00:00Z"));

    private static ISecretProtector Protector(TestStorage storage, string name)
        => new DataProtectionSecretProtector(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage.DataRoot, name))));

    private static async Task InitializeAsync(TestStorage storage)
        => await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

    [Fact]
    public void TryUnprotect_tells_no_value_from_a_value_from_one_saved_elsewhere()
    {
        using var storage = TestStorage.Create();
        var here = Protector(storage, "keys-here");
        var elsewhere = Protector(storage, "keys-elsewhere");
        var saved = elsewhere.Protect("purpose", "the-key");

        Assert.Equal(SecretReadResult.None, here.TryUnprotect("purpose", null));
        Assert.Equal(SecretReadResult.None, here.TryUnprotect("purpose", "  "));
        Assert.Equal(new SecretReadResult("the-key", false), elsewhere.TryUnprotect("purpose", saved));
        Assert.Equal(new SecretReadResult("plain", false), here.TryUnprotect("purpose", "plain"));
        Assert.Equal(SecretReadResult.CannotRead, here.TryUnprotect("purpose", saved));
    }

    [Fact]
    public void TryUnprotect_covers_a_protector_that_throws_a_crypto_failure()
    {
        ISecretProtector broken = new ThrowingProtector();

        var result = broken.TryUnprotect("purpose", "dpapi:v1:AAAA");

        Assert.True(result.Unreadable);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Indexers_download_clients_and_proxies_list_flagged_and_entering_again_clears_it()
    {
        using var storage = TestStorage.Create();
        await InitializeAsync(storage);
        var elsewhere = new SqliteConnectionsRepository(storage.Factory, Clock, Protector(storage, "keys-elsewhere"));
        var here = new SqliteConnectionsRepository(storage.Factory, Clock, Protector(storage, "keys-here"));

        var indexer = await elsewhere.CreateIndexerAsync(
            new CreateIndexerRequest("Geek", "newznab", "public", "https://indexer.example.test", "secret-key", 1, "2000", "", "movies", true),
            CancellationToken.None);
        var client = await elsewhere.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("qBit", "qbittorrent", "localhost", 8080, "admin", "password", null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var proxy = await elsewhere.CreateProxyAsync(
            new CreateProxyRequest("Proxy", "socks5", "socks5://192.168.1.1:1080", "user", "pass"),
            CancellationToken.None);

        var listedIndexer = Assert.Single(await here.ListIndexersAsync(CancellationToken.None));
        Assert.True(listedIndexer.SecretUnreadable);
        Assert.Null(listedIndexer.ApiKey);
        Assert.Equal("degraded", listedIndexer.HealthStatus);
        Assert.Equal("The saved API key was stored on another computer and can't be read here. Enter it again.", listedIndexer.LastHealthMessage);
        Assert.Equal(listedIndexer.LastHealthMessage, listedIndexer.SecretProblem);

        var listedClient = Assert.Single(await here.ListDownloadClientsAsync(CancellationToken.None));
        Assert.True(listedClient.SecretUnreadable);
        Assert.Null(listedClient.Secret);
        Assert.Equal("degraded", listedClient.HealthStatus);
        Assert.Contains("saved password", listedClient.SecretProblem);

        var listedProxy = Assert.Single(await here.ListProxiesAsync(CancellationToken.None));
        Assert.True(listedProxy.SecretUnreadable);
        Assert.False(listedProxy.HasPassword);

        // A saved secret that can be read is not flagged.
        Assert.False(Assert.Single(await elsewhere.ListIndexersAsync(CancellationToken.None)).SecretUnreadable);

        // Entering the key again clears the flag.
        await here.UpdateIndexerAsync(indexer.Id, new UpdateIndexerRequest(null, null, null, null, "new-key", null, null, null, null, null), CancellationToken.None);
        await here.UpdateDownloadClientAsync(client.Id, new UpdateDownloadClientRequest(null, null, null, null, null, "new-password", null, null, null, null, null, null), CancellationToken.None);
        await here.UpdateProxyAsync(proxy.Id, new UpdateProxyRequest(null, null, null, null, "new-pass", null, null), CancellationToken.None);

        var fixedIndexer = Assert.Single(await here.ListIndexersAsync(CancellationToken.None));
        Assert.False(fixedIndexer.SecretUnreadable);
        Assert.Equal("new-key", fixedIndexer.ApiKey);
        Assert.Null(fixedIndexer.SecretProblem);
        var fixedClient = Assert.Single(await here.ListDownloadClientsAsync(CancellationToken.None));
        Assert.False(fixedClient.SecretUnreadable);
        Assert.Equal("new-password", fixedClient.Secret);
        Assert.False(Assert.Single(await here.ListProxiesAsync(CancellationToken.None)).SecretUnreadable);
    }

    [Fact]
    public async Task Processors_list_flagged_and_entering_again_clears_it()
    {
        using var storage = TestStorage.Create();
        await InitializeAsync(storage);
        var elsewhere = new SqliteProcessorRepository(storage.Factory, Clock, Protector(storage, "keys-elsewhere"));
        var here = new SqliteProcessorRepository(storage.Factory, Clock, Protector(storage, "keys-here"));
        var created = await elsewhere.CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("FileFlows", "fileflows-webhook", "https://fileflows.example.test/webhook", "Authorization", "secret-token", IsEnabled: true),
            CancellationToken.None);

        var listed = Assert.Single(await here.ListProcessorConnectionsAsync(CancellationToken.None));
        Assert.True(listed.SecretUnreadable);
        Assert.Null(listed.Secret);
        Assert.True(listed.SecretConfigured);
        Assert.Equal("degraded", listed.HealthStatus);
        Assert.Contains("can't be read here", listed.LastHealthMessage);

        await here.UpdateProcessorConnectionAsync(
            created.Id,
            new UpdateProcessorConnectionRequest("FileFlows", "fileflows-webhook", "https://fileflows.example.test/webhook", "Authorization", "fresh-token", true),
            CancellationToken.None);

        var fixedItem = Assert.Single(await here.ListProcessorConnectionsAsync(CancellationToken.None));
        Assert.False(fixedItem.SecretUnreadable);
        Assert.Equal("fresh-token", fixedItem.Secret);
    }

    [Fact]
    public async Task Media_servers_and_seerr_list_flagged_and_entering_again_clears_it()
    {
        using var storage = TestStorage.Create();
        await InitializeAsync(storage);

        var serversElsewhere = new SqliteMediaServerRepository(storage.Factory, Clock, Protector(storage, "keys-elsewhere"));
        var serversHere = new SqliteMediaServerRepository(storage.Factory, Clock, Protector(storage, "keys-here"));
        var server = await serversElsewhere.SaveAsync(null, new SaveMediaServerRequest("plex", "Plex", "http://192.168.1.51:32400", "token", true), CancellationToken.None);
        var listedServer = Assert.Single(await serversHere.ListAsync(CancellationToken.None));
        Assert.True(listedServer.SecretUnreadable);
        Assert.False(listedServer.HasToken);
        Assert.Contains("access token", listedServer.SecretProblem);
        await serversHere.SaveAsync(server!.Id, new SaveMediaServerRequest("plex", "Plex", "http://192.168.1.51:32400", "new-token", true), CancellationToken.None);
        Assert.False(Assert.Single(await serversHere.ListAsync(CancellationToken.None)).SecretUnreadable);

        var seerrElsewhere = new SqliteSeerrRepository(storage.Factory, Clock, Protector(storage, "keys-elsewhere"));
        var seerrHere = new SqliteSeerrRepository(storage.Factory, Clock, Protector(storage, "keys-here"));
        var seerr = await seerrElsewhere.SaveAsync(null, new SaveSeerrRequest("Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        var listedSeerr = Assert.Single(await seerrHere.ListAsync(CancellationToken.None));
        Assert.True(listedSeerr.SecretUnreadable);
        Assert.False(listedSeerr.HasApiKey);
        await seerrHere.SaveAsync(seerr!.Id, new SaveSeerrRequest("Seerr", "http://127.0.0.1:5055", "key-2", true), CancellationToken.None);
        Assert.False(Assert.Single(await seerrHere.ListAsync(CancellationToken.None)).SecretUnreadable);
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => plaintext;

        public string? Unprotect(string purpose, string? protectedValue)
            => throw new CryptographicException("Key not valid for use in specified state.");

        public bool IsProtected(string? value) => true;
    }
}
