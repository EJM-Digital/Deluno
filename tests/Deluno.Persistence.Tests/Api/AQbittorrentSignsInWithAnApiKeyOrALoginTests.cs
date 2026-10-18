using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A qBittorrent client is saved with a blank username and its API key in the password field, or with a username and password,
/// and Test connection signs in the way the person set it up: the key goes out as a bearer token on every call and no sign-in is
/// made; a login signs in once and sends no bearer token. A key the client refuses (qBittorrent 5.2.1 answers 403) is said
/// plainly. qBittorrent is the real app's recorded replies over a real loopback port, the application is the whole host.
/// </summary>
public sealed class AQbittorrentSignsInWithAnApiKeyOrALoginTests
{
    private const string Key = "deluno-test-api-key";

    private static async Task<JsonElement> TestAsync(ApplicationTestHost app, RecordedAppServer qbit, string? username, string password)
    {
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username, password,
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var tested = await app.Client.PostAsync($"/api/download-clients/{id}/test", content: null);
        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());
        return await tested.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_blank_username_with_the_key_in_the_password_field_is_tested_with_a_bearer_token_and_never_signs_in()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();

        var answer = await TestAsync(app, qbit, username: null, Key);

        Assert.Equal("healthy", answer.GetProperty("healthStatus").GetString());
        Assert.NotEmpty(qbit.Requests);
        Assert.All(qbit.Requests, request => Assert.Equal($"Bearer {Key}", request.Authorization));
        Assert.DoesNotContain(qbit.Requests, request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal));
        Assert.DoesNotContain(Key, answer.GetRawText());
    }

    [Fact]
    public async Task A_username_and_password_sign_in_once_and_send_no_bearer_token()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync();

        var answer = await TestAsync(app, qbit, "deluno", "a-test-password");

        Assert.Equal("healthy", answer.GetProperty("healthStatus").GetString());
        var login = Assert.Single(qbit.Requests, request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal));
        Assert.Contains("username=deluno", login.Body, StringComparison.Ordinal);
        Assert.All(qbit.Requests, request => Assert.Equal(string.Empty, request.Authorization));
    }

    [Fact]
    public async Task A_key_qBittorrent_refuses_is_degraded_and_says_the_key_was_refused_without_repeating_it()
    {
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent),
            (_, _) => RecordedReplies.Qbittorrent("api-key-refused").Find("torrents/info").Reply.ToResponse());
        await using var app = await ApplicationTestHost.StartAsync();

        var answer = await TestAsync(app, qbit, username: null, "a-wrong-key");

        Assert.NotEqual("healthy", answer.GetProperty("healthStatus").GetString());
        Assert.Contains("refused", answer.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a-wrong-key", answer.GetRawText());
    }
}
