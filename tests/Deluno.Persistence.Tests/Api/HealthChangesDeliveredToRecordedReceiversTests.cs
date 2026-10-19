using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A connection going bad and coming back, told to a phone (#859): each Test click is the real route on the whole application with its own
/// databases, the other app is a loopback server that answers as the real one did (Jackett 0.24.2850's wrong-key reply, SABnzbd 5.1.3's refused
/// key, Jellyfin 12.2.0), and the phone is a push service answering as the real ntfy 2.32.0 did. A fault is reported on every Test; a recovery
/// once; and neither when the one health switch is off.
/// </summary>
public sealed class HealthChangesDeliveredToRecordedReceiversTests
{
    private sealed class Phone : IAsyncDisposable
    {
        public required RecordedAppServer Ntfy { get; init; }

        public IReadOnlyList<(string Title, string Body)> Told => Ntfy.Requests.Select(request => (request.Header("title"), request.Body)).ToArray();

        public async ValueTask DisposeAsync() => await Ntfy.DisposeAsync();
    }

    private static async Task<(ApplicationTestHost App, Phone Phone)> StartAsync(bool healthSwitchOn = true)
    {
        var ntfy = await RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? RecordedReplies.Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);
        var app = await ApplicationTestHost.StartAsync();
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Phone", url = ntfy.BaseUrl + "/deluno", eventFilters = "all", isEnabled = true, provider = "ntfy" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var preferences = (JsonObject)JsonNode.Parse(await app.Client.GetStringAsync("/api/notification-preferences"))!;
        preferences["providerHealthChangedEnabled"] = healthSwitchOn;
        Assert.True((await app.Client.PutAsJsonAsync("/api/notification-preferences", preferences)).IsSuccessStatusCode);
        return (app, new Phone { Ntfy = ntfy });
    }

    private static async Task<string> TestAsync(ApplicationTestHost app, string route)
    {
        var response = await app.Client.PostAsync(route, null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.TryGetProperty("healthStatus", out var status) ? status : body.GetProperty("status")).GetString()!;
    }

    [Fact]
    public async Task An_indexer_that_goes_bad_is_told_on_every_test_and_its_recovery_once_and_a_second_good_test_says_nothing()
    {
        await using var indexer = await RecordedIndexerServer.StartAsync(RecordedReplies.Nzbhydra2("caps"), RecordedReplies.Nzbhydra2("search-feed"));
        var (app, phone) = await StartAsync();
        await using var _ = app;
        await using var __ = phone;
        var made = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Flaky", "newznab", "private", indexer.BaseUrl, "fixture-key", 10, "2000", "", "both", true), CancellationToken.None);
        var route = $"/api/indexers/{made.Id}/test";

        indexer.AnswerEverythingWith = RecordedReplies.Nzbhydra2("caps-wrong-api-key").Exchanges[0].Reply;
        Assert.NotEqual("healthy", await TestAsync(app, route));
        Assert.NotEqual("healthy", await TestAsync(app, route));
        Assert.Equal(2, phone.Told.Count(told => told.Title.StartsWith("Indexer degraded:", StringComparison.Ordinal)));

        indexer.AnswerEverythingWith = null;
        Assert.Equal("healthy", await TestAsync(app, route));
        var restored = Assert.Single(phone.Told, told => told.Title.EndsWith("is healthy again", StringComparison.Ordinal));
        Assert.StartsWith("Indexer ", restored.Title, StringComparison.Ordinal);

        Assert.Equal("healthy", await TestAsync(app, route));
        Assert.Single(phone.Told, told => told.Title.EndsWith("is healthy again", StringComparison.Ordinal));
        Assert.Equal(3, phone.Told.Count);
    }

    [Fact]
    public async Task A_download_client_that_refuses_its_key_is_told_and_its_recovery_once()
    {
        var healthy = false;
        await using var sab = await RecordedAppServer.StartAsync("sabnzbd", new[] { "version", "settings", "idle" }.Select(RecordedReplies.Sabnzbd),
            (_, _) => healthy ? null : RecordedReplies.Sabnzbd("api-key-refused").Exchanges[0].Reply.ToResponse());
        var (app, phone) = await StartAsync();
        await using var _ = app;
        await using var __ = phone;
        var made = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "sabnzbd", host = sab.Host, port = sab.Port, password = "0123456789abcdef0123456789abcdef", moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var route = $"/api/download-clients/{(await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}/test";

        Assert.NotEqual("healthy", await TestAsync(app, route));
        var degraded = Assert.Single(phone.Told);
        Assert.EndsWith(" degraded", degraded.Title.Split(':')[0].Trim(), StringComparison.Ordinal);
        Assert.Contains("SABnzbd refused Deluno's API key", degraded.Body, StringComparison.Ordinal);

        healthy = true;
        Assert.Equal("healthy", await TestAsync(app, route));
        Assert.Single(phone.Told, told => told.Title.EndsWith("is healthy again", StringComparison.Ordinal));
        Assert.Equal("healthy", await TestAsync(app, route));
        Assert.Single(phone.Told, told => told.Title.EndsWith("is healthy again", StringComparison.Ordinal));
        Assert.Equal(2, phone.Told.Count);
    }

    [Fact]
    public async Task A_media_server_that_does_not_answer_is_told_and_its_recovery_once_and_a_server_that_was_already_healthy_says_nothing()
    {
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        var (app, phone) = await StartAsync();
        await using var _ = app;
        await using var __ = phone;
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var made = await app.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = $"http://127.0.0.1:{deadPort}", token = "jf-secret-token-1", isEnabled = true });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var route = $"/api/media-servers/{id}/test";

        Assert.NotEqual("healthy", await TestAsync(app, route));
        Assert.Single(phone.Told, told => told.Title.EndsWith(" degraded", StringComparison.Ordinal));

        var back = await app.Client.PutAsJsonAsync($"/api/media-servers/{id}", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "", isEnabled = true });
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());
        Assert.Equal("healthy", await TestAsync(app, route));
        Assert.Single(phone.Told, told => told.Title.EndsWith("is healthy again", StringComparison.Ordinal));

        Assert.Equal("healthy", await TestAsync(app, route));
        Assert.Equal(2, phone.Told.Count);
    }

    [Fact]
    public async Task With_the_health_switch_off_a_fault_and_its_recovery_are_both_kept_off_the_phone()
    {
        await using var indexer = await RecordedIndexerServer.StartAsync(RecordedReplies.Nzbhydra2("caps"), RecordedReplies.Nzbhydra2("search-feed"));
        var (app, phone) = await StartAsync(healthSwitchOn: false);
        await using var _ = app;
        await using var __ = phone;
        var made = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Flaky", "newznab", "private", indexer.BaseUrl, "fixture-key", 10, "2000", "", "both", true), CancellationToken.None);
        var route = $"/api/indexers/{made.Id}/test";

        indexer.AnswerEverythingWith = RecordedReplies.Nzbhydra2("caps-wrong-api-key").Exchanges[0].Reply;
        Assert.NotEqual("healthy", await TestAsync(app, route));
        indexer.AnswerEverythingWith = null;
        Assert.Equal("healthy", await TestAsync(app, route));

        Assert.Empty(phone.Told);
    }

    [Fact]
    public async Task A_failing_health_check_is_told_when_it_starts_and_again_when_it_is_fine_and_a_pass_that_changes_nothing_says_nothing()
    {
        const string torznabPath = "/api/v2.0/indexers/yts/results/torznab/api";
        var wrong = RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        var (app, phone) = await StartAsync();
        await using var _ = app;
        await using var __ = phone;
        Assert.True((await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Anything", url = phone.Ntfy.BaseUrl + "/generic", eventFilters = "health", isEnabled = true, provider = "generic" })).IsSuccessStatusCode);
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name = "YTS", protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + torznabPath, apiKey = "0123456789abcdef0123456789abcdef",
            priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        async Task PassAsync()
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Deluno.Worker.HealthChecks.HealthCheckRunner>().RunCategoriesAsync([Deluno.Contracts.HealthCheckCategories.Indexer], CancellationToken.None);
        }

        string[] Said() => phone.Ntfy.Requests.Where(request => request.PathAndQuery == "/generic").Select(request => JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>()).ToArray();

        jackett.AnswerEverythingWith = wrong;
        await app.Client.PostAsync($"/api/indexers/{id}/test", null);
        await PassAsync();
        await PassAsync();
        Assert.Equal(["health.problem"], Said().Where(said => said.StartsWith("health.problem", StringComparison.Ordinal)));

        jackett.AnswerEverythingWith = null;
        await app.Client.PostAsync($"/api/indexers/{id}/test", null);
        await PassAsync();
        await PassAsync();
        var resolved = Assert.Single(phone.Ntfy.Requests, request => request.PathAndQuery == "/generic" && JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>().StartsWith("health.resolved", StringComparison.Ordinal));
        Assert.EndsWith("is fine again", JsonNode.Parse(resolved.Body)!["title"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
