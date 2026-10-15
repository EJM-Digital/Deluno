using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What is saved from Settings and the guided setup, on a real host and the real platform database: guided-setup progress,
/// answers and completion, how many Activity lines a page holds, which events notify, and the application settings themselves.
/// A value that cannot be used is refused in plain words and the saved value stays.
/// </summary>
public sealed class SettingsAndSetupRoutesTests
{
    [Fact]
    public async Task Guided_setup_progress_and_answers_are_saved_read_back_and_discarded_and_finishing_is_written_to_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var progress = await app.Client.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = 2 });
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        var saved = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");
        Assert.Equal(2, saved.GetProperty("lastCompletedStep").GetInt32());
        Assert.False(saved.GetProperty("isCompleted").GetBoolean());

        var draft = await app.Client.PutAsJsonAsync("/api/setup/draft", new { mode = "advanced", mediaIntent = "movies", movieRootPath = "/media/films", downloadsPath = "/media/downloads" });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/draft");
        Assert.Equal("advanced", read.GetProperty("mode").GetString());
        Assert.Equal("/media/films", read.GetProperty("movieRootPath").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/setup/draft")).StatusCode);
        var cleared = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/draft");
        Assert.NotEqual("/media/films", cleared.GetProperty("movieRootPath").GetString());

        var done = await app.Client.PostAsJsonAsync("/api/setup/completed", new { libraries = new[] { "Films" }, qualityProfiles = new[] { "HD" }, customFormatCount = 3, indexerName = "Jackett", clientName = "qBittorrent", firstTitle = "Sintel" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var finished = await app.Client.GetFromJsonAsync<JsonElement>("/api/setup/progress");
        Assert.True(finished.GetProperty("isCompleted").GetBoolean());
        Assert.Equal(4, finished.GetProperty("lastCompletedStep").GetInt32());
        Assert.Contains("Guided setup completed.", await app.Client.GetStringAsync("/api/activity?take=20"), StringComparison.Ordinal);

        var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/setup/progress", new { lastCompletedStep = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/setup/draft", new { mode = "simple" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/setup/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/setup/completed", new { customFormatCount = 0 })).StatusCode);
    }

    [Fact]
    public async Task The_number_of_Activity_lines_per_page_is_saved_in_a_file_in_the_data_folder_and_a_number_outside_the_limits_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = 250 });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(250, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pageSize").GetInt32());
        Assert.Contains(Directory.GetFiles(app.DataRoot, "*.json", SearchOption.AllDirectories), path => File.ReadAllText(path).Contains("250", StringComparison.Ordinal) && path.Contains("activity", StringComparison.OrdinalIgnoreCase));
        foreach (var bad in new[] { 0, 24, 2001, -5 })
        {
            var refused = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Page size must be between 25 and 2000.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var again = await app.Client.PutAsJsonAsync("/api/activity/display-settings", new { pageSize = 250 });
        Assert.Equal(250, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Which_events_notify_is_saved_and_read_back_and_needs_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-preferences");
        Assert.True(before.GetProperty("importCompletedEnabled").GetBoolean());

        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(before.GetRawText())!;
        var changed = body.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
        changed["importCompletedEnabled"] = false;
        var put = await app.Client.PutAsJsonAsync("/api/notification-preferences", changed);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/notification-preferences");
        Assert.False(after.GetProperty("importCompletedEnabled").GetBoolean());
        Assert.Equal(before.GetProperty("importFailedEnabled").GetBoolean(), after.GetProperty("importFailedEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PutAsJsonAsync("/api/notification-preferences", changed)).StatusCode);
    }

    [Fact]
    public async Task Saving_the_application_settings_keeps_what_was_sent_and_a_value_that_cannot_be_used_is_refused_in_plain_words_with_the_saved_value_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var current = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await app.Client.GetStringAsync("/api/settings"))!;
        object Copy(string key, object value)
        {
            var body = current.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
            body[key] = value;
            return body;
        }

        var refused = await app.Client.PutAsJsonAsync("/api/settings", Copy("libraryFreeSpacePausePercent", 99));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("libraryFreeSpacePausePercent", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PutAsJsonAsync("/api/settings", Copy("cloudflareHelperUrl", "not a web address"))).StatusCode);
        Assert.Equal(current["libraryFreeSpacePausePercent"].GetInt32(), (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("libraryFreeSpacePausePercent").GetInt32());

        var warn = current["libraryFreeSpaceWarnPercent"].GetInt32() == 20 ? 15 : 20;
        var saved = await app.Client.PutAsJsonAsync("/api/settings", Copy("libraryFreeSpaceWarnPercent", warn));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(warn, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraryFreeSpaceWarnPercent").GetInt32());
        Assert.Equal(warn, (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("libraryFreeSpaceWarnPercent").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PutAsJsonAsync("/api/settings", Copy("libraryFreeSpaceWarnPercent", warn))).StatusCode);
    }
}
