using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The download-health screens' own lists (coverage register, area api): the exemptions, the strike rules and the content blocklist. Each
/// is saved through its route and read back from the system settings in the database file, so what the screen shows is what a health
/// pass will read; a refusal stores nothing.
/// </summary>
public sealed class DownloadHealthSettingsAreKeptInTheDatabaseFileTests
{
    private static string? Stored(ApplicationTestHost app, string key)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return (string?)command.ExecuteScalar();
    }

    [Fact]
    public async Task Exempting_a_download_lists_it_and_stores_it_and_removing_the_exemption_takes_it_out_and_an_empty_exemption_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Null(Stored(app, "download-health.ignore-list.v1"));

        var added = await app.Client.PostAsJsonAsync("/api/download-health/ignore-list", new { hash = "ABCDEF0123456789", note = "Keep this one" });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var entry = Assert.Single((await added.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("ABCDEF0123456789", entry.GetProperty("hash").GetString());
        var id = entry.GetProperty("id").GetString()!;
        Assert.Contains("ABCDEF0123456789", Stored(app, "download-health.ignore-list.v1"));
        Assert.Equal(id, Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/ignore-list")).EnumerateArray()).GetProperty("id").GetString());

        var empty = await app.Client.PostAsJsonAsync("/api/download-health/ignore-list", new { note = "nothing to match on" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("Give at least a hash, a client category, a tag, or a tracker domain to ignore.", await empty.Content.ReadAsStringAsync());
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/ignore-list")).EnumerateArray());

        var removed = await app.Client.DeleteAsync($"/api/download-health/ignore-list/{id}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await removed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.DoesNotContain("ABCDEF0123456789", Stored(app, "download-health.ignore-list.v1"));
        var again = await app.Client.DeleteAsync($"/api/download-health/ignore-list/{id}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty((await again.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task A_strike_rule_is_saved_stored_and_removed_and_a_kind_nobody_knows_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var refused = await app.Client.PutAsJsonAsync("/api/download-health/rules/new", new
        {
            kind = "made-up-kind", enabled = true, strikeLimit = 3, timerMinutes = 60, minCompletionPercent = 0, maxCompletionPercent = 100,
            resetStrikesOnProgress = true, threshold = 0.0
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("That isn't a rule kind Deluno knows about.", await refused.Content.ReadAsStringAsync());

        var saved = await app.Client.PutAsJsonAsync("/api/download-health/rules/new", new
        {
            kind = "no-progress", enabled = true, strikeLimit = 4, timerMinutes = 90, minCompletionPercent = 0, maxCompletionPercent = 99,
            resetStrikesOnProgress = true, threshold = 0.0
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var rule = (await saved.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "no-progress" && item.GetProperty("strikeLimit").GetInt32() == 4);
        Assert.Equal(90, rule.GetProperty("timerMinutes").GetInt32());
        var id = rule.GetProperty("id").GetString()!;
        Assert.Contains(id, Stored(app, "download-health.rules.v1"));

        var removed = await app.Client.DeleteAsync($"/api/download-health/rules/{id}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.DoesNotContain(id, Stored(app, "download-health.rules.v1"));
        Assert.DoesNotContain((await removed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray(), item => item.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task The_content_blocklist_is_saved_in_the_database_file_and_a_mode_nobody_knows_is_refused_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await app.Client.PutAsJsonAsync("/api/download-health/content-blocklist", new
        {
            enabled = true, mode = "whitelist", useBuiltInPatterns = false, customPatterns = new[] { "*.mkv" }, remoteUrl = (string?)null,
            remotePatterns = Array.Empty<string>(), lastRefreshedUtc = (string?)null, updatedUtc = DateTimeOffset.MinValue
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist");
        Assert.True(read.GetProperty("enabled").GetBoolean());
        Assert.Equal("whitelist", read.GetProperty("mode").GetString());
        Assert.False(read.GetProperty("useBuiltInPatterns").GetBoolean());
        Assert.Equal("*.mkv", Assert.Single(read.GetProperty("customPatterns").EnumerateArray()).GetString());
        Assert.Contains("whitelist", Stored(app, "download-health.content-blocklist.v1"));

        var refused = await app.Client.PutAsJsonAsync("/api/download-health/content-blocklist", new
        {
            enabled = false, mode = "greylist", useBuiltInPatterns = true, customPatterns = Array.Empty<string>(), remoteUrl = (string?)null,
            remotePatterns = Array.Empty<string>(), lastRefreshedUtc = (string?)null, updatedUtc = DateTimeOffset.MinValue
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Choose blacklist or whitelist.", await refused.Content.ReadAsStringAsync());
        Assert.Equal("whitelist", (await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist")).GetProperty("mode").GetString());
        Assert.Contains("whitelist", Stored(app, "download-health.content-blocklist.v1"));
    }
}
