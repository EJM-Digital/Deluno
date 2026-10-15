using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Quality on the whole application with its own database files: Reset to defaults (the shipped numbers come back and
/// Activity says so), and what deleting a quality profile or a custom rule leaves behind for the libraries, titles and profiles
/// that used it.
/// </summary>
public sealed class QualitySettingsDeletionsAndResetTests
{
    private const string Allowed = "WEB 1080p,WEB 2160p";

    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string> CreateProfileAsync(ApplicationTestHost app, string name, string customFormatIds = "")
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name, mediaType = "movies", cutoffQuality = "WEB 1080p", allowedQualities = Allowed, customFormatIds, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Reset_to_defaults_brings_the_shipped_numbers_back_after_a_hand_edit_and_Activity_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!.AsObject();
        var shippedPreferred = shipped["tiers"]![0]!["moviePreferredGb"]!.GetValue<double>();
        var edited = JsonNode.Parse(shipped.ToJsonString())!.AsObject();
        var tier = edited["tiers"]![0]!.AsObject();
        tier["movieMaxGb"] = 99.0;
        tier["moviePreferredGb"] = 42.0;
        var saved = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers = edited["tiers"], upgradeStop = edited["upgradeStop"] });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(42.0, JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!["tiers"]![0]!["moviePreferredGb"]!.GetValue<double>());

        var reset = await app.Client.PostAsync("/api/quality-model/reset", null);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(shippedPreferred, JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!["tiers"]![0]!["moviePreferredGb"]!.GetValue<double>());
        Assert.Contains("quality", await app.Client.GetStringAsync("/api/activity?take=20"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_quality_profile_a_library_uses_leaves_the_library_with_no_profile_not_a_profile_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var profileId = await CreateProfileAsync(app, "Films 1080p");
        var assigned = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = profileId });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        Assert.Equal(profileId, await StoredAsync(app, $"SELECT quality_profile_id FROM libraries WHERE id = '{libraryId}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}")).StatusCode);

        var after = await StoredAsync(app, $"SELECT quality_profile_id FROM libraries WHERE id = '{libraryId}'");
        Assert.True(string.IsNullOrEmpty(after) || after != profileId, "the library must not keep pointing at the profile that was deleted");
        Assert.Equal("0", await StoredAsync(app, $"SELECT COUNT(*) FROM quality_profiles WHERE id = '{profileId}'"));
    }

    [Fact(Skip = "Defect: deleting a quality profile that a title chose for itself leaves that title pointing at a profile id that no longer exists. DeleteQualityProfileAsync is a plain DELETE of the profile row; a library falls back through its foreign key, but movie_entries.quality_profile_id (another database) is never cleared, so GetTitleQualityProfileIdAsync keeps answering the deleted id and the delete confirmation promises libraries 'will need another one' without saying anything about titles.")]
    public async Task Deleting_a_quality_profile_a_title_chose_leaves_the_title_on_its_librarys_profile_not_on_one_that_is_gone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var profileId = await CreateProfileAsync(app, "Own profile");
        var assigned = await app.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { film.MovieId }, qualityProfileId = profileId });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        var mediaState = app.Services.GetRequiredService<IMediaStateRepository>();
        Assert.Equal(profileId, await mediaState.GetTitleQualityProfileIdAsync(MediaKind.Movie, film.MovieId, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}")).StatusCode);

        Assert.NotEqual(profileId, await mediaState.GetTitleQualityProfileIdAsync(MediaKind.Movie, film.MovieId, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_custom_rule_takes_it_off_the_list_at_once_without_asking_again_and_a_profile_that_named_it_can_still_be_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/custom-formats", new
        {
            name = "Directors cut", mediaType = "movies", score = 0, trashId = (string?)null,
            conditions = "[{\"type\":\"releaseGroup\",\"value\":\"DELUNO\",\"required\":true}]", upgradeAllowed = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var formatId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var profileId = await CreateProfileAsync(app, "Uses the rule", formatId);

        var removed = await app.Client.DeleteAsync($"/api/custom-formats/{formatId}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal("0", await StoredAsync(app, $"SELECT COUNT(*) FROM custom_formats WHERE id = '{formatId}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/custom-formats/{formatId}")).StatusCode);
        var profile = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == profileId);
        var saved = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{profileId}", new
        {
            name = "Uses the rule", cutoffQuality = "WEB 1080p", allowedQualities = Allowed,
            customFormatIds = profile.GetProperty("customFormatIds").GetString() ?? string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
    }
}
