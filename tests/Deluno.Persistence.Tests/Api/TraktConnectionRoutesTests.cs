using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Import lists > Trakt, the part that never talks to Trakt: saving the app's Client ID and Secret, what is
/// stored on disk (protected, not the typed text), what the status route may say (flags only, never a secret or a token),
/// Disconnect, and that none of it is open to someone who is not signed in. The device flow itself needs Trakt's real
/// replies, which are not recorded yet (tests/fixtures/recorded/MISSING.md).
/// </summary>
public sealed class TraktConnectionRoutesTests
{
    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saving_the_app_credentials_stores_them_protected_and_the_status_says_only_that_they_are_set()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/trakt/status")).GetProperty("clientConfigured").GetBoolean());

        var saved = await app.Client.PostAsJsonAsync("/api/trakt/credentials", new { clientId = "client-id-1234", clientSecret = "client-secret-5678" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("clientConfigured").GetBoolean());
        Assert.False(status.GetProperty("connected").GetBoolean());
        var text = (await app.Client.GetStringAsync("/api/trakt/status")) + status;
        Assert.DoesNotContain("client-secret-5678", text);
        Assert.DoesNotContain("client-id-1234", text);
        var rawSecret = await StoredAsync(app, "SELECT client_secret FROM trakt_connection");
        var rawId = await StoredAsync(app, "SELECT client_id FROM trakt_connection");
        Assert.False(string.IsNullOrEmpty(rawSecret));
        Assert.NotEqual("client-secret-5678", rawSecret);
        Assert.NotEqual("client-id-1234", rawId);
        var credentials = await app.Services.GetRequiredService<ITraktConnectionRepository>().GetClientCredentialsAsync(CancellationToken.None);
        Assert.Equal(("client-id-1234", "client-secret-5678"), (credentials!.ClientId, credentials.ClientSecret));
    }

    [Fact]
    public async Task A_blank_client_id_or_secret_is_refused_in_plain_words_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var body in new object[] { new { clientId = "", clientSecret = "s" }, new { clientId = "c", clientSecret = "  " } })
        {
            var refused = await app.Client.PostAsJsonAsync("/api/trakt/credentials", body);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Enter the Client ID and Client Secret from your Trakt app.", await refused.Content.ReadAsStringAsync());
        }

        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM trakt_connection"));
    }

    [Fact]
    public async Task Connecting_before_the_app_credentials_are_saved_is_refused_and_asks_for_them_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var connect = await app.Client.PostAsync("/api/trakt/connect", null);
        var poll = await app.Client.PostAsJsonAsync("/api/trakt/connect/poll", new { deviceCode = "abc" });

        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
        Assert.Contains("Enter your Trakt app's Client ID and Client Secret first.", await connect.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, poll.StatusCode);
    }

    [Fact]
    public async Task A_connected_account_is_reported_without_its_tokens_and_Disconnect_clears_them_but_keeps_the_app_credentials()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PostAsJsonAsync("/api/trakt/credentials", new { clientId = "client-id-1234", clientSecret = "client-secret-5678" });
        var repository = app.Services.GetRequiredService<ITraktConnectionRepository>();
        await repository.SaveTokensAsync("access-token-aaaa", "refresh-token-bbbb", DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);

        var connected = await app.Client.GetStringAsync("/api/trakt/status");

        Assert.DoesNotContain("access-token-aaaa", connected);
        Assert.DoesNotContain("refresh-token-bbbb", connected);
        using (var document = JsonDocument.Parse(connected))
        {
            Assert.True(document.RootElement.GetProperty("connected").GetBoolean());
            Assert.False(document.RootElement.GetProperty("needsReconnect").GetBoolean());
        }
        Assert.NotEqual("access-token-aaaa", await StoredAsync(app, "SELECT access_token FROM trakt_connection"));

        var disconnected = await (await app.Client.PostAsync("/api/trakt/disconnect", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(disconnected.GetProperty("connected").GetBoolean());
        Assert.True(disconnected.GetProperty("clientConfigured").GetBoolean());
        Assert.True(string.IsNullOrEmpty(await StoredAsync(app, "SELECT access_token FROM trakt_connection")));
        Assert.True(string.IsNullOrEmpty(await StoredAsync(app, "SELECT refresh_token FROM trakt_connection")));
        Assert.False(string.IsNullOrEmpty(await StoredAsync(app, "SELECT client_secret FROM trakt_connection")));
    }

    [Fact]
    public async Task A_refresh_token_that_stopped_working_shows_as_needs_reconnect_in_the_status()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PostAsJsonAsync("/api/trakt/credentials", new { clientId = "c", clientSecret = "s" });
        var repository = app.Services.GetRequiredService<ITraktConnectionRepository>();
        await repository.SaveTokensAsync("a", "r", DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        await repository.MarkNeedsReconnectAsync(CancellationToken.None);

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/trakt/status");
        Assert.True(status.GetProperty("needsReconnect").GetBoolean());
        Assert.Equal("1", await StoredAsync(app, "SELECT needs_reconnect FROM trakt_connection"));
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_read_or_change_the_Trakt_connection()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/trakt/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/trakt/credentials", new { clientId = "c", clientSecret = "s" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/trakt/disconnect", null)).StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM trakt_connection"));
    }
}
