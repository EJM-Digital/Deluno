using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a person sees from the connections they set up, against the real replies of the apps (tests/fixtures/recorded) served over
/// a real loopback port: an indexer or a download client going unhealthy and then healthy tells the person "health restored" once;
/// a saved client password is stored protected, never listed, kept by a blank edit and sent when the client is next asked.
/// </summary>
public sealed class ConnectionSecretsAndHealthOnRecordedAppsTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private static object QbittorrentForm(RecordedAppServer server, string? password = "a-test-password") => new
    {
        protocol = "qbittorrent", host = server.Host, port = server.Port, username = "deluno", password,
        moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
    };

    [Fact]
    public async Task An_indexer_that_answers_with_a_wrong_key_and_then_answers_properly_is_announced_as_unhealthy_once_and_as_restored_once()
    {
        var outbound = new RecordingOutbound();
        var wrong = RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + TorznabPath, apiKey = "0123456789abcdef0123456789abcdef",
            priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        jackett.AnswerEverythingWith = wrong;
        var first = await (await app.Client.PostAsync($"/api/indexers/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("healthy", first.GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.Categories.Count(category => category == "health.degraded"));
        jackett.AnswerEverythingWith = null;
        Assert.DoesNotContain("health.restored", outbound.Categories);

        var second = await (await app.Client.PostAsync($"/api/indexers/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", second.GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.Categories.Count(category => category == "health.restored"));

        var third = await (await app.Client.PostAsync($"/api/indexers/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", third.GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.Categories.Count(category => category == "health.restored"));
    }

    [Fact]
    public async Task A_download_client_that_refuses_its_login_and_then_accepts_it_is_announced_as_unhealthy_once_and_as_restored_once()
    {
        var outbound = new RecordingOutbound();
        var refused = RecordedReplies.Qbittorrent("login-refused").Exchanges[0];
        var accepted = RecordedReplies.Qbittorrent("sign-in").Exchanges[0];
        var refusing = false;
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("auth/login", StringComparison.Ordinal) ? (refusing ? refused : accepted).Reply.ToResponse() : null);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", QbittorrentForm(qbit));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        refusing = true;
        var first = await (await app.Client.PostAsync($"/api/download-clients/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("healthy", first.GetProperty("healthStatus").GetString());
        Assert.Contains("health.degraded", outbound.Categories);
        Assert.DoesNotContain("health.restored", outbound.Categories);

        refusing = false;
        var second = await (await app.Client.PostAsync($"/api/download-clients/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", second.GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.Categories.Count(category => category == "health.restored"));

        await app.Client.PostAsync($"/api/download-clients/{id}/test", null);
        Assert.Equal(1, outbound.Categories.Count(category => category == "health.restored"));
    }

    [Fact]
    public async Task A_saved_client_password_is_stored_protected_never_listed_kept_by_a_blank_edit_replaced_by_a_typed_one_and_sent_when_the_client_is_asked()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", QbittorrentForm(qbit, "first-password-1"));
        var client = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = client.GetProperty("id").GetString()!;

        Assert.DoesNotContain("first-password-1", client.ToString());
        Assert.DoesNotContain("first-password-1", await app.Client.GetStringAsync("/api/download-clients"));
        var stored = await ApiScenes.TextAsync(app, "platform", $"SELECT secret FROM download_clients WHERE id = '{id}'");
        Assert.False(string.IsNullOrEmpty(stored));
        Assert.DoesNotContain("first-password-1", stored);

        // A password left out of an edit (as the screen does when the box is blank) keeps the saved one: the next sign-in still sends it.
        var blank = await app.Client.PutAsJsonAsync($"/api/download-clients/{id}", new { priority = 4 });
        Assert.True(blank.IsSuccessStatusCode);
        await app.Client.PostAsync($"/api/download-clients/{id}/test", null);
        Assert.Contains("password=first-password-1", qbit.Requests.Last(request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal)).Body, StringComparison.Ordinal);

        // A typed password replaces it.
        await app.Client.PutAsJsonAsync($"/api/download-clients/{id}", new { password = "second-password-2" });
        await app.Client.PostAsync($"/api/download-clients/{id}/test", null);
        Assert.Contains("password=second-password-2", qbit.Requests.Last(request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
    }

    [Fact]
    public async Task Testing_an_unsaved_edit_with_no_key_or_password_typed_uses_the_saved_one_for_an_indexer_and_for_a_client()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();
        var indexerId = (await (await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + TorznabPath, apiKey = "saved-key-aaaa1111",
            priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var clientId = (await (await app.Client.PostAsJsonAsync("/api/download-clients", QbittorrentForm(qbit, "saved-password-bbbb2222"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var indexerTest = await app.Client.PostAsJsonAsync($"/api/indexers/test?id={indexerId}", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + TorznabPath, apiKey = "", priority = 30, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true
        });
        var clientTest = await app.Client.PostAsJsonAsync($"/api/download-clients/test?id={clientId}", QbittorrentForm(qbit, password: null));

        Assert.True(indexerTest.IsSuccessStatusCode);
        Assert.True(clientTest.IsSuccessStatusCode);
        Assert.All(jackett.Requests, request => Assert.Contains("apikey=saved-key-aaaa1111", request, StringComparison.Ordinal));
        Assert.Contains("password=saved-password-bbbb2222", qbit.Requests.Last(request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    private sealed class RecordingOutbound : IOutboundNotificationService
    {
        public List<string> Categories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Categories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
