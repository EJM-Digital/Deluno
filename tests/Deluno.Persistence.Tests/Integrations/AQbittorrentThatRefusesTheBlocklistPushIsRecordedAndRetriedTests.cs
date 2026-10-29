using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The hourly content-blocklist push (coverage register, engines) when qBittorrent refuses Deluno's login. The real qBittorrent client
/// answers from the recorded real reply of qBittorrent 5.2.1; the real SQLite stores keep the result. The refusal is recorded as failed
/// with the client's message, a warning is logged, and the next pass tries that client again instead of treating it as done.
/// </summary>
public sealed class AQbittorrentThatRefusesTheBlocklistPushIsRecordedAndRetriedTests
{
    private sealed class Warnings : ILogger<QbittorrentBlocklistSyncService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed class SingleScopeFactory(IServiceProvider services) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(services);

        private sealed class Scope(IServiceProvider services) : IServiceScope
        {
            public IServiceProvider ServiceProvider => services;

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task A_client_that_refuses_the_login_is_recorded_as_failed_with_its_message_and_a_warning_and_is_pushed_to_again_on_the_next_pass()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var health = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        await health.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, SyncToQbittorrentEnabled = true, UseBuiltInPatterns = false, CustomPatterns = ["*.exe"] },
            CancellationToken.None);
        var created = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("qB", "qbittorrent", "localhost", 8080, "deluno", "a-test-password", null, "movies", "tv", null, 1, true), CancellationToken.None);
        var logins = 0;
        var refused = RecordedReplies.Qbittorrent("login-refused").Single.Reply;
        var client = new QbittorrentDownloadClient(() =>
        {
            logins++;
            return RecordedReplies.Handler("qbittorrent", [], (_, _) => refused.ToResponse());
        });
        var services = new ServiceCollection();
        services.AddSingleton<IDownloadHealthRepository>(health);
        services.AddSingleton<IConnectionsRepository>(connections);
        services.AddSingleton<IDownloadClientRegistry>(new DownloadClientRegistry([client]));
        var log = new Warnings();
        var service = new QbittorrentBlocklistSyncService(log, new SingleScopeFactory(services.BuildServiceProvider()), TimeProvider.System);

        await service.SyncAsync(CancellationToken.None);

        var state = Assert.Single((await health.ListQbittorrentBlocklistSyncStatesAsync(CancellationToken.None)).Values);
        Assert.False(state.Succeeded);
        Assert.Equal(created.Id, state.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(state.Message));
        Assert.Contains(log.Lines, line => line.Contains("Could not push the content blocklist into qB", StringComparison.Ordinal));
        var loginsAfterFirst = logins;
        Assert.True(loginsAfterFirst > 0);

        await service.SyncAsync(CancellationToken.None);

        Assert.True(logins > loginsAfterFirst, "a failed push is tried again on the next pass");
    }
}
