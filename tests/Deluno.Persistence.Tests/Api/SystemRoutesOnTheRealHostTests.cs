using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// System-screen routes against the real application and real data files: the account password, API keys, the security
/// settings, the activity clear and "Run now" for a library search. Each test starts a whole host on its own data folder.
/// </summary>
public sealed class SystemRoutesOnTheRealHostTests
{
    private const string OwnerName = "owner";
    private const string OwnerPassword = "Deluno-Test-2026!";

    // ---------------------------------------------------------------- change password

    [Fact]
    public async Task A_wrong_current_password_is_refused_with_its_own_words_and_the_old_password_still_signs_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PutAsJsonAsync("/api/auth/password", new { currentPassword = "not-my-password", newPassword = "A-brand-new-one-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Current password is not correct.", FirstError(await response.Content.ReadFromJsonAsync<JsonElement>(), "currentPassword"));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(app, OwnerPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(app, "A-brand-new-one-1")).StatusCode);
    }

    [Theory]
    [InlineData(null, "A-brand-new-one-1", "currentPassword", "Enter your current password.")]
    [InlineData(OwnerPassword, null, "newPassword", "Enter a new password.")]
    [InlineData(OwnerPassword, "short", "newPassword", "Use at least 8 characters for the new password.")]
    [InlineData(OwnerPassword, OwnerPassword, "newPassword", "Choose a password that is different from your current password.")]
    public async Task A_password_change_that_breaks_a_rule_says_which_rule_and_changes_nothing(string? current, string? next, string field, string message)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PutAsJsonAsync("/api/auth/password", new { currentPassword = current, newPassword = next });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, FirstError(await response.Content.ReadFromJsonAsync<JsonElement>(), field));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(app, OwnerPassword)).StatusCode);
    }

    [Fact]
    public async Task A_correct_change_signs_in_with_the_new_password_and_no_longer_with_the_old_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PutAsJsonAsync("/api/auth/password", new { currentPassword = OwnerPassword, newPassword = "A-brand-new-one-1" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(app, "A-brand-new-one-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(app, OwnerPassword)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_does_not_sign_in_and_keeping_me_signed_in_lasts_longer()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var wrong = await LoginAsync(app, "not-my-password");
        using var short1 = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = OwnerName, password = OwnerPassword, keepSignedIn = false });
        using var long1 = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = OwnerName, password = OwnerPassword, keepSignedIn = true });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var shortExpiry = ExpiryOf(await short1.Content.ReadFromJsonAsync<JsonElement>());
        var longExpiry = ExpiryOf(await long1.Content.ReadFromJsonAsync<JsonElement>());
        Assert.True(longExpiry > shortExpiry.AddHours(12), $"kept sign-in {longExpiry:u} should outlast a normal one {shortExpiry:u}");
    }

    // ---------------------------------------------------------------- API keys

    [Fact]
    public async Task A_new_key_is_shown_once_and_only_a_hash_is_kept_anywhere_on_disk()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(key));

        var list = await app.Client.GetStringAsync("/api/api-keys/");
        Assert.Contains("Reader", list);
        Assert.DoesNotContain(key, list);

        // Not in any file under the data folder either: databases are read while Deluno has them open.
        foreach (var file in Directory.EnumerateFiles(app.DataRoot, "*", SearchOption.AllDirectories))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.False(System.Text.Encoding.UTF8.GetString(memory.ToArray()).Contains(key, StringComparison.Ordinal), $"{Path.GetFileName(file)} holds the key in plain text");
        }
    }

    [Fact]
    public async Task A_revoked_key_stops_signing_in_at_once_and_revoking_an_unknown_id_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var key = body.GetProperty("apiKey").GetString()!;
        var id = body.GetProperty("item").GetProperty("id").GetString()!;
        using var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/libraries/")).StatusCode);

        using var revoked = await app.Client.DeleteAsync($"/api/api-keys/{id}");
        using var unknown = await app.Client.DeleteAsync("/api/api-keys/no-such-key");

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/libraries/")).StatusCode);
        Assert.DoesNotContain(id, await app.Client.GetStringAsync("/api/api-keys/"));
    }

    // ---------------------------------------------------------------- security settings

    [Fact]
    public async Task Allowed_hosts_saves_and_reads_back_and_refuses_a_list_that_would_lock_the_person_out()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var saved = await app.Client.PutAsJsonAsync("/api/security/allowed-hosts", new { enabled = true, allowedHosts = new[] { "nas.example", "deluno.example" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/security/allowed-hosts");
        Assert.True(read.GetProperty("enabled").GetBoolean());
        Assert.Equal(["nas.example", "deluno.example"], read.GetProperty("allowedHosts").EnumerateArray().Select(h => h.GetString()!).ToArray());

        // Asked from an address that is not on the new list, the save is refused and the old list stays.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts")
        {
            Content = JsonContent.Create(new { enabled = true, allowedHosts = new[] { "other.example" } })
        };
        request.Headers.Host = "nas.example";
        using var refused = await app.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("is not on the list, and saving would lock you out", FirstError(await refused.Content.ReadFromJsonAsync<JsonElement>(), "allowedHosts"));
        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/security/allowed-hosts");
        Assert.Equal(2, after.GetProperty("allowedHosts").GetArrayLength());
    }

    [Fact]
    public async Task Local_network_sign_in_skipping_saves_reads_back_and_refuses_an_address_that_is_not_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var saved = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new { enabled = true, additionalLocalNetworks = new[] { "192.168.50.0/24" }, allowList = Array.Empty<string>(), trustedProxies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/security/local-network-auth");
        Assert.True(read.GetProperty("enabled").GetBoolean());
        Assert.Equal("192.168.50.0/24", Assert.Single(read.GetProperty("additionalLocalNetworks").EnumerateArray()).GetString());

        using var bad = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new { enabled = true, additionalLocalNetworks = new[] { "not-an-address" }, allowList = Array.Empty<string>(), trustedProxies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("is not a valid address or subnet", FirstError(await bad.Content.ReadFromJsonAsync<JsonElement>(), "additionalLocalNetworks"));
        var unchanged = await app.Client.GetFromJsonAsync<JsonElement>("/api/security/local-network-auth");
        Assert.Equal("192.168.50.0/24", Assert.Single(unchanged.GetProperty("additionalLocalNetworks").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task The_security_settings_need_the_system_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        var key = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);
        using var anonymous = app.CreateAnonymousClient();

        foreach (var path in new[] { "/api/security/allowed-hosts", "/api/security/local-network-auth" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(path, new { enabled = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/security/allowed-hosts")).GetProperty("enabled").GetBoolean());
    }

    // ---------------------------------------------------------------- activity and library search

    [Fact]
    public async Task Clearing_activity_removes_every_stored_row_and_leaves_one_line_saying_it_happened()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        await activity.RecordActivityAsync("test.one", "First thing happened.", null, null, null, null, CancellationToken.None);
        await activity.RecordActivityAsync("test.two", "Second thing happened.", null, null, null, null, CancellationToken.None);

        using var response = await app.Client.PostAsync("/api/activity/clear", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var removed = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32();
        Assert.True(removed >= 2);
        var feed = await app.Client.GetStringAsync("/api/activity");
        Assert.DoesNotContain("First thing happened.", feed);
        Assert.DoesNotContain("Second thing happened.", feed);
        Assert.Contains($"Activity history was cleared ({removed} entries removed).", feed);
        // And in the jobs database file itself: only the one line about the clear is left.
        Assert.True(File.Exists(Path.Combine(app.DataRoot, "jobs.db")));
        Assert.Equal(1L, CountActivityRows(app.DataRoot));
    }

    [Fact]
    public async Task Clearing_activity_needs_the_write_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        await activity.RecordActivityAsync("test.one", "Kept for a read-only key.", null, null, null, null, CancellationToken.None);
        var made = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Reader", scopes = "read" });
        var key = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var refused = await reader.PostAsync("/api/activity/clear", content: null);

        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Contains("Kept for a read-only key.", await app.Client.GetStringAsync("/api/activity"));
        Assert.True(File.Exists(Path.Combine(app.DataRoot, "jobs.db")));
        Assert.True(CountActivityRows(app.DataRoot) >= 1L);
    }

    [Fact]
    public async Task Run_now_asks_for_a_search_of_that_library_and_an_unknown_library_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateMovieLibraryAsync(app);

        using var accepted = await app.Client.PostAsync($"/api/libraries/{libraryId}/search-now", content: null);
        using var unknown = await app.Client.PostAsync("/api/libraries/no-such-library/search-now", content: null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<HttpResponseMessage> LoginAsync(ApplicationTestHost app, string password)
    {
        using var anonymous = app.CreateAnonymousClient();
        return await anonymous.PostAsJsonAsync("/api/auth/login", new { username = OwnerName, password });
    }

    private static long CountActivityRows(string dataRoot)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM activity_events;";
        return (long)command.ExecuteScalar()!;
    }

    private static string FirstError(JsonElement problem, string field)
        => problem.GetProperty("errors").GetProperty(field)[0].GetString()!;

    private static DateTimeOffset ExpiryOf(JsonElement login)
    {
        foreach (var name in new[] { "expiresUtc", "expiresAtUtc", "expiresAt", "expires" })
        {
            if (login.TryGetProperty(name, out var value))
            {
                return value.GetDateTimeOffset();
            }
        }

        throw new InvalidOperationException("The sign-in answer carries no expiry: " + login);
    }

    private static async Task<string> CreateMovieLibraryAsync(ApplicationTestHost app)
    {
        var rootPath = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(rootPath);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath,
            downloadsPath = (string?)null,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
