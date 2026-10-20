using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Seerr;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A Seerr key saved on another computer (a restored backup, a copied data folder) cannot be read here. The Test says so and asks for the key
/// again, and Seerr is not asked with a key Deluno does not have; once the key is entered again the same connection tests healthy against the
/// answers the real Seerr 3.5.0 gave (tests/fixtures/recorded/seerr: status and settings).
/// </summary>
public sealed class ASeerrKeySavedOnAnotherComputerAsksToBeEnteredAgainTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-20T09:00:00Z"));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_here_is_never_sent_and_the_test_says_to_enter_it_again_and_entering_it_makes_the_same_connection_healthy()
    {
        await new PlatformSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var elsewhere = new SqliteSeerrRepository(_storage.Factory, _time, new Deluno.Security.DataProtectionSecretProtector(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_storage.DataRoot, "keys-elsewhere")))));
        var saved = (await elsewhere.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", IsEnabled: true), CancellationToken.None))!;
        var here = new SqliteSeerrRepository(_storage.Factory, _time, TestSecretProtection.Create(_storage));
        var asked = new List<string>();
        var client = new SeerrClient(new Factory(RecordedReplies.Handler("seerr", [RecordedReplies.Seerr("status"), RecordedReplies.Seerr("settings-main")], (request, _) =>
        {
            asked.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return null;
        })));

        var unreadable = (await here.GetAsync(saved.Id, CancellationToken.None))!;
        var refused = await client.TestAsync(unreadable, CancellationToken.None);

        Assert.True(unreadable.SecretUnreadable);
        Assert.False(refused.Ok);
        Assert.Equal("degraded", refused.Status);
        Assert.Equal("The saved API key was stored on another computer and can't be read here. Enter it again.", refused.Message);
        Assert.Empty(asked);

        var entered = (await here.SaveAsync(saved.Id, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-2", IsEnabled: true), CancellationToken.None))!;
        var healthy = await client.TestAsync((await here.GetAsync(entered.Id, CancellationToken.None))!, CancellationToken.None);

        Assert.False((await here.GetAsync(saved.Id, CancellationToken.None))!.SecretUnreadable);
        Assert.True(healthy.Ok);
        Assert.Equal("3.5.0", healthy.Version);
        Assert.Equal(["GET /api/v1/status", "GET /api/v1/settings/main"], asked);
    }

    public void Dispose() => _storage.Dispose();
}
