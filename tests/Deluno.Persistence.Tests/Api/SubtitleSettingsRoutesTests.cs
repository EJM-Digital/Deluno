using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Subtitles on the whole application with its own database files: the library's subtitle settings as saved
/// and as stored, an unknown library, the live push that tells open screens, that saving queues no job (the library cycle
/// acts later), the list of languages the picker offers, and the Wanted list's episode fields for a series.
/// </summary>
public sealed class SubtitleSettingsRoutesTests
{
    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saving_a_librarys_subtitle_settings_stores_them_tells_open_screens_and_queues_no_job()
    {
        var realtime = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IRealtimeEventPublisher>(realtime));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var jobsBefore = await StoredAsync(app, DelunoDatabaseNames.Jobs, "SELECT COUNT(*) FROM job_queue");

        var response = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/subtitles", new
        {
            languages = new[] { "en", "ja" }, mode = "first", unknownLanguage = "en", embeddedCounts = false, pictureBasedCounts = false, omitLanguageCode = true,
            contentPolicy = new { stripHearingImpairedAnnotations = true, removeStyleTags = true, fixOcrErrors = true, cueColour = "yellow" },
            timingPolicy = new { enabled = true, syncOnlyBelow = "same-source", maxOffsetSeconds = 90, requiredPeakSigma = 4.5, repairFramerate = false },
            namePolicy = new { mustContain = new[] { "NTb" }, mustNotContain = new[] { "CAM" } },
            languageTypePreferences = new[] { new { language = "en", hearingImpaired = "plain-only", includeForced = true } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var library = (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == libraryId);
        Assert.Equal(["en", "ja"], library.GetProperty("subtitleLanguages").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("first", library.GetProperty("subtitleLanguageMode").GetString());
        Assert.Equal("en", library.GetProperty("subtitleUnknownLanguage").GetString());
        Assert.False(library.GetProperty("subtitleEmbeddedCounts").GetBoolean());
        Assert.False(library.GetProperty("subtitlePictureBasedCounts").GetBoolean());
        Assert.True(library.GetProperty("subtitleOmitLanguageCode").GetBoolean());
        Assert.Equal("yellow", library.GetProperty("subtitleContentPolicy").GetProperty("cueColour").GetString());
        Assert.Equal(90, library.GetProperty("subtitleTimingPolicy").GetProperty("maxOffsetSeconds").GetInt32());
        Assert.Equal("NTb", library.GetProperty("subtitleNamePolicy").GetProperty("mustContain")[0].GetString(), ignoreCase: true);
        Assert.Equal("plain-only", library.GetProperty("subtitleLanguageTypePreferences")[0].GetProperty("hearingImpaired").GetString());
        Assert.Contains("ja", await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT subtitle_languages FROM libraries WHERE id = '{libraryId}'"));
        Assert.Contains(realtime.Published, item => item.Type == "Library" && item.Id == libraryId);
        Assert.Equal(jobsBefore, await StoredAsync(app, DelunoDatabaseNames.Jobs, "SELECT COUNT(*) FROM job_queue"));
    }

    [Fact]
    public async Task Saving_the_subtitle_settings_of_a_library_that_does_not_exist_is_a_not_found_and_changes_nothing()
    {
        var realtime = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IRealtimeEventPublisher>(realtime));

        var response = await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/subtitles", new { languages = new[] { "en" }, mode = "all" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(realtime.Published, item => item.Type == "Library");
    }

    [Fact]
    public async Task The_picker_is_offered_the_languages_the_server_can_read_and_write_each_with_a_code_and_a_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var languages = await app.Client.GetFromJsonAsync<JsonElement>("/api/subtitle-languages");

        Assert.True(languages.GetArrayLength() > 10);
        var english = languages.EnumerateArray().Single(item => item.GetProperty("code").GetString() == "en");
        Assert.Equal("English", english.GetProperty("name").GetString());
        Assert.All(languages.EnumerateArray(), item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("name").GetString())));
    }

    [Fact]
    public async Task A_series_episode_missing_a_language_is_listed_with_its_season_episode_and_title_fields()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{show.LibraryId}/subtitles", new { languages = new[] { "en" }, mode = "all" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var wanted = await app.Client.GetFromJsonAsync<JsonElement>("/api/subtitles/wanted");

        var row = Assert.Single(wanted.EnumerateArray());
        Assert.Equal("Series", row.GetProperty("kind").GetString());
        Assert.Equal("The Wire", row.GetProperty("title").GetString());
        Assert.Equal(1, row.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, row.GetProperty("episodeNumber").GetInt32());
        Assert.Equal(show.LibraryId, row.GetProperty("libraryId").GetString());
        Assert.Contains("en", row.GetProperty("languagesMissing").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_change_a_librarys_subtitle_settings_or_read_the_wanted_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/libraries/{libraryId}/subtitles", new { languages = new[] { "en" }, mode = "all" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subtitles/wanted")).StatusCode);
    }
}
