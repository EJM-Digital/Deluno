using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What a quality profile keeps in its real SQLite file when it is edited: an ordinary rename leaves every plain
/// answer alone, and an answer is cleared only when an empty value is sent for it.
/// </summary>
public sealed class QualityProfileAnswersOnDiskTests
{
    private static JsonElement StoredProfile(ApplicationTestHost app, string id)
    {
        var path = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().GetDatabasePath("platform");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM quality_profiles WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        var name = (string?)command.ExecuteScalar();
        return JsonSerializer.SerializeToElement(new { name });
    }

    private static async Task<JsonElement> ProfileAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles"))
            .EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    private static async Task<string> CreateProfileWithEveryAnswerAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Everything",
            mediaType = "movies",
            cutoffQuality = "WEB 2160p",
            allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "WEB 1080p", minGb = 2.0, maxGb = 12.0, minMb = 100.0, maxMb = 2000.0 } },
            upgradeStop = new { stopWhenCutoffMet = true, requireCustomFormatGainForSameQuality = true },
            acquisition = new { preferredProtocol = "usenet", usenetDelayMinutes = 15, torrentDelayMinutes = 30, mustContain = "REMUX", mustNotContain = "CAM" },
            scoreLimits = new { minFormatScore = 50, stopUpgradingAtScore = 800 },
            languagePreference = new { preferredLanguages = new[] { "ja", "en" }, requireLanguage = true }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Renaming_a_profile_without_sending_its_other_answers_leaves_every_one_of_them_as_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateProfileWithEveryAnswerAsync(app);
        var before = await ProfileAsync(app, id);

        var renamed = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", new
        {
            name = "Everything, renamed",
            cutoffQuality = "WEB 2160p",
            allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var after = await ProfileAsync(app, id);
        Assert.Equal("Everything, renamed", after.GetProperty("name").GetString());
        foreach (var answer in new[] { "sizeRules", "upgradeStop", "acquisition", "scoreLimits", "languagePreference" })
        {
            Assert.Equal(before.GetProperty(answer).GetRawText(), after.GetProperty(answer).GetRawText());
        }

        Assert.Equal("Everything, renamed", StoredProfile(app, id).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sending_an_empty_value_clears_just_that_answer_and_nothing_else()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateProfileWithEveryAnswerAsync(app);
        var before = await ProfileAsync(app, id);

        var cleared = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", new
        {
            name = "Everything",
            cutoffQuality = "WEB 2160p",
            allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false,
            sizeRules = Array.Empty<object>(),
            languagePreference = new { preferredLanguages = Array.Empty<string>(), requireLanguage = false }
        });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = await ProfileAsync(app, id);
        Assert.Equal(0, after.GetProperty("sizeRules").GetArrayLength());
        var language = after.GetProperty("languagePreference");
        Assert.True(language.ValueKind == JsonValueKind.Null || language.GetProperty("preferredLanguages").GetArrayLength() == 0, "the language answer is cleared: " + language.GetRawText());
        foreach (var answer in new[] { "upgradeStop", "acquisition", "scoreLimits" })
        {
            Assert.Equal(before.GetProperty(answer).GetRawText(), after.GetProperty(answer).GetRawText());
        }
    }
}
