using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The pushes a finished connection test sends to every open screen: HealthChanged with the connection's name and verdict, and the
/// change of the indexer or client itself. The apps are Jackett and qBittorrent answering with the replies they really gave
/// (tests/fixtures/recorded), over a real loopback port; a test that finds the app down is the recorded connection error.
/// </summary>
public sealed class AConnectionTestPushesItsResultOnRecordedAppsTests
{
    private sealed class Pushes : NullRealtimeEventPublisher
    {
        public List<(string Source, string Status)> Health { get; } = [];

        public List<(string Type, string Id)> Changed { get; } = [];

        public override Task PublishHealthChangedAsync(string source, string status, string message, CancellationToken cancellationToken)
        {
            lock (Health) Health.Add((source, status));
            return Task.CompletedTask;
        }

        public override Task PublishEntityChangedAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            lock (Changed) Changed.Add((entityType, entityId));
            return Task.CompletedTask;
        }
    }

    private static async Task<(ApplicationTestHost App, Pushes Pushes)> StartAsync()
    {
        var pushes = new Pushes();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(pushes)));
        return (app, pushes);
    }

    [Fact]
    public async Task Testing_a_saved_indexer_pushes_its_verdict_and_its_change_once_for_a_working_one_and_for_one_that_does_not_answer()
    {
        await using var jackett = await RecordedAppServer.StartAsync("jackett", new[] { "caps" }.Select(RecordedReplies.Jackett));
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name = "Recorded Jackett", protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + "/api/v2.0/indexers/yts/results/torznab/api",
            apiKey = "0123456789abcdef0123456789abcdef", priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        lock (pushes.Health) pushes.Health.Clear();
        lock (pushes.Changed) pushes.Changed.Clear();

        var tested = await app.Client.PostAsync($"/api/indexers/{id}/test", content: null);

        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());
        Assert.Equal(new[] { ("Recorded Jackett", "healthy") }, pushes.Health.ToArray());
        Assert.Equal(new[] { ("Indexer", id) }, pushes.Changed.ToArray());

        // The same indexer once Jackett is gone: the verdict pushed is "degraded", and the indexer is announced as changed again.
        await jackett.DisposeAsync();
        pushes.Health.Clear();
        pushes.Changed.Clear();
        var down = await app.Client.PostAsync($"/api/indexers/{id}/test", content: null);
        Assert.True(down.IsSuccessStatusCode, await down.Content.ReadAsStringAsync());
        Assert.Equal(new[] { ("Recorded Jackett", "degraded") }, pushes.Health.ToArray());
        Assert.Equal(new[] { ("Indexer", id) }, pushes.Changed.ToArray());
    }

    [Fact]
    public async Task Testing_a_saved_download_client_pushes_its_verdict_and_its_change()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version" }.Select(RecordedReplies.Qbittorrent));
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name = "Recorded qBittorrent", protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        pushes.Health.Clear();
        pushes.Changed.Clear();

        var tested = await app.Client.PostAsync($"/api/download-clients/{id}/test", content: null);

        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());
        var health = Assert.Single(pushes.Health);
        Assert.Equal("healthy", health.Status);
        Assert.StartsWith("qBittorrent", health.Source, StringComparison.Ordinal);
        Assert.Equal(new[] { ("DownloadClient", id) }, pushes.Changed.ToArray());
    }
}
