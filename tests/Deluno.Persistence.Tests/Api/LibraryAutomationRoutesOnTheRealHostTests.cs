using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A library's schedule (<c>PUT /api/libraries/{id}/automation</c>), on the real host and its real databases: every field is saved
/// and read back, the library's automation row is made in the jobs database, a bad number is refused with its own sentence and
/// saves nothing, and the row switch that pauses or resumes one library changes only that library's search switch.
/// </summary>
public sealed class LibraryAutomationRoutesOnTheRealHostTests
{
    [Fact]
    public async Task Saving_the_schedule_keeps_every_field_and_makes_the_librarys_automation_row_in_the_jobs_database()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "Films");

        using var response = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/automation", new
        {
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = 45,
            retryDelayHours = 12,
            maxItemsPerRun = 30,
            searchWindowStartHour = 1,
            searchWindowEndHour = 5,
            randomizeSearchOrder = true,
            maxMissingItemsPerRun = 20,
            maxUpgradeItemsPerRun = 10,
            searchWindowDays = 0b0111110
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        var read = await ReadAsync(app, libraryId);
        foreach (var item in new[] { saved, read })
        {
            Assert.True(item.GetProperty("autoSearchEnabled").GetBoolean());
            Assert.True(item.GetProperty("missingSearchEnabled").GetBoolean());
            Assert.False(item.GetProperty("upgradeSearchEnabled").GetBoolean());
            Assert.Equal(45, item.GetProperty("searchIntervalMinutes").GetInt32());
            Assert.Equal(12, item.GetProperty("retryDelayHours").GetInt32());
            Assert.Equal(1, item.GetProperty("searchWindowStartHour").GetInt32());
            Assert.Equal(5, item.GetProperty("searchWindowEndHour").GetInt32());
            Assert.True(item.GetProperty("randomizeSearchOrder").GetBoolean());
            Assert.Equal(20, item.GetProperty("maxMissingItemsPerRun").GetInt32());
            Assert.Equal(10, item.GetProperty("maxUpgradeItemsPerRun").GetInt32());
            Assert.Equal(0b0111110, item.GetProperty("searchWindowDays").GetInt32());
        }

        Assert.Equal(1L, Scalar(app, "jobs.db", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{libraryId}';"));
    }

    [Theory]
    [InlineData("searchIntervalMinutes", 0, "Choose how often Deluno should check this library.")]
    [InlineData("retryDelayHours", -1, "Choose how long Deluno should wait before trying again.")]
    [InlineData("maxItemsPerRun", 0, "Choose how many titles Deluno should work through at a time.")]
    [InlineData("maxMissingItemsPerRun", 0, "Choose how many missing titles Deluno should work through at a time.")]
    [InlineData("maxUpgradeItemsPerRun", -5, "Choose how many upgrade candidates Deluno should work through at a time.")]
    public async Task A_number_that_makes_no_sense_is_refused_with_its_own_sentence_and_nothing_is_saved(string field, int value, string sentence)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, "Films");
        var before = (await ReadAsync(app, libraryId)).GetRawText();
        var body = new Dictionary<string, object?>
        {
            ["autoSearchEnabled"] = true,
            ["missingSearchEnabled"] = true,
            ["upgradeSearchEnabled"] = true,
            ["searchIntervalMinutes"] = 30,
            ["retryDelayHours"] = 24,
            ["maxItemsPerRun"] = 25,
            [field] = value
        };

        using var response = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/automation", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(sentence, errors.GetProperty(field)[0].GetString());
        Assert.Equal(before, (await ReadAsync(app, libraryId)).GetRawText());
    }

    [Fact]
    public async Task Saving_the_schedule_of_a_library_that_is_not_there_is_404_and_makes_no_automation_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/automation", new
        {
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = true,
            searchIntervalMinutes = 30,
            retryDelayHours = 24,
            maxItemsPerRun = 25
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, Scalar(app, "jobs.db", "SELECT COUNT(*) FROM library_automation_state;"));
    }

    [Fact]
    public async Task The_row_switch_pauses_and_resumes_one_library_and_leaves_the_other_library_and_the_schedule_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await CreateLibraryAsync(app, "Films");
        var shows = await CreateLibraryAsync(app, "Shows", mediaType: "tv");
        await SaveAsync(app, films, autoSearch: true, interval: 90);
        await SaveAsync(app, shows, autoSearch: true, interval: 20);

        using var paused = await SaveAsync(app, films, autoSearch: false, interval: 90);

        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedFilms = await ReadAsync(app, films);
        var otherShows = await ReadAsync(app, shows);
        Assert.False(pausedFilms.GetProperty("autoSearchEnabled").GetBoolean());
        Assert.Equal(90, pausedFilms.GetProperty("searchIntervalMinutes").GetInt32());
        Assert.True(otherShows.GetProperty("autoSearchEnabled").GetBoolean());
        Assert.Equal(20, otherShows.GetProperty("searchIntervalMinutes").GetInt32());

        using var resumed = await SaveAsync(app, films, autoSearch: true, interval: 90);

        Assert.True((await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoSearchEnabled").GetBoolean());
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonElement> ReadAsync(ApplicationTestHost app, string libraryId)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries/")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == libraryId).Clone();

    private static Task<HttpResponseMessage> SaveAsync(ApplicationTestHost app, string libraryId, bool autoSearch, int interval)
        => app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/automation", new
        {
            autoSearchEnabled = autoSearch,
            missingSearchEnabled = true,
            upgradeSearchEnabled = true,
            searchIntervalMinutes = interval,
            retryDelayHours = 24,
            maxItemsPerRun = 25
        });

    private static long Scalar(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string name, string mediaType = "movies")
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath = root,
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
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
