using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The settings the Interface, Metadata and General screens save through PATCH /api/settings, on the whole application
/// with its own database files: each one is stored under its own key, read back as saved, an untouched setting is left
/// alone by a partial PATCH, and a value that is not one of the choices is stored as the default, not as typed.
/// </summary>
public sealed class SettingsPatchPersistsOnDiskTests
{
    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> SettingAsync(ApplicationTestHost app, string key)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key";
        command.Parameters.AddWithValue("@key", key);
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<JsonElement> PatchAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PatchAsJsonAsync("/api/settings", body);
        Assert.True(response.IsSuccessStatusCode, $"PATCH /api/settings answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Every_interface_choice_is_stored_under_its_own_key_and_read_back_as_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await PatchAsync(app, new
        {
            uiTheme = "dark", uiColorMode = "impaired", uiLanguage = "en-US", calendarFirstDayOfWeek = "sunday", calendarWeekHeaderFormat = "ddd m/d",
            runtimeFormat = "minutes", shortDateFormat = "iso", longDateFormat = "mdy", timeFormat = "24", showRelativeDates = false,
            defaultMovieView = "list", defaultShowView = "list"
        });

        var reread = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        foreach (var settings in new[] { saved, reread })
        {
            Assert.Equal("dark", settings.GetProperty("uiTheme").GetString());
            Assert.Equal("impaired", settings.GetProperty("uiColorMode").GetString());
            Assert.Equal("en-US", settings.GetProperty("uiLanguage").GetString());
            Assert.Equal("sunday", settings.GetProperty("calendarFirstDayOfWeek").GetString());
            Assert.Equal("ddd m/d", settings.GetProperty("calendarWeekHeaderFormat").GetString());
            Assert.False(settings.GetProperty("showRelativeDates").GetBoolean());
            Assert.Equal("list", settings.GetProperty("defaultMovieView").GetString());
            Assert.Equal("list", settings.GetProperty("defaultShowView").GetString());
        }
        Assert.Equal("dark", await SettingAsync(app, "ui.theme"));
        Assert.Equal("impaired", await SettingAsync(app, "ui.colorMode"));
        Assert.Equal("en-US", await SettingAsync(app, "ui.language"));
        Assert.Equal("sunday", await SettingAsync(app, "ui.calendarFirstDay"));
        Assert.Equal("false", await SettingAsync(app, "ui.relativeDates"));
        Assert.Equal("list", await SettingAsync(app, "ui.defaultMovieView"));
        Assert.Equal("list", await SettingAsync(app, "ui.defaultShowView"));
        Assert.Equal("24", await SettingAsync(app, "ui.timeFormat"));
    }

    [Fact]
    public async Task A_value_that_is_not_one_of_the_choices_is_stored_as_the_default_rather_than_as_typed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PatchAsync(app, new { uiTheme = "neon", uiColorMode = "rainbow", defaultMovieView = "carousel" });

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("system", settings.GetProperty("uiTheme").GetString());
        Assert.Equal("standard", settings.GetProperty("uiColorMode").GetString());
        Assert.Equal("grid", settings.GetProperty("defaultMovieView").GetString());
        Assert.NotEqual("neon", await SettingAsync(app, "ui.theme"));
    }

    [Fact]
    public async Task The_details_language_and_ratings_region_are_stored_and_a_patch_that_leaves_them_out_keeps_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PatchAsync(app, new { metadataLanguage = "fr", metadataCertificationCountry = "NZ" });
        await PatchAsync(app, new { uiTheme = "light" });

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("fr", settings.GetProperty("metadataLanguage").GetString());
        Assert.Equal("NZ", settings.GetProperty("metadataCertificationCountry").GetString());
        Assert.Equal("light", settings.GetProperty("uiTheme").GetString());
        Assert.Equal("fr", await SettingAsync(app, "metadata.language"));
        Assert.Equal("NZ", await SettingAsync(app, "metadata.certificationCountry"));
    }

    [Fact]
    public async Task The_cloudflare_helper_address_is_trimmed_of_a_trailing_slash_a_timeout_outside_1_to_180_is_refused_and_a_blank_address_clears_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var tooLong = await app.Client.PatchAsJsonAsync("/api/settings", new { cloudflareHelperTimeoutSeconds = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("Choose a timeout between 1 and 180 seconds.", await tooLong.Content.ReadAsStringAsync());
        Assert.NotEqual("999", await SettingAsync(app, "search.cloudflareHelperTimeoutSeconds"));

        await PatchAsync(app, new { cloudflareHelperUrl = "http://127.0.0.1:8191/", cloudflareHelperTimeoutSeconds = 180, cloudflareHelperKind = "byparr" });

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("http://127.0.0.1:8191", settings.GetProperty("cloudflareHelperUrl").GetString());
        Assert.Equal(180, settings.GetProperty("cloudflareHelperTimeoutSeconds").GetInt32());
        Assert.Equal("byparr", settings.GetProperty("cloudflareHelperKind").GetString());
        Assert.Equal("http://127.0.0.1:8191", await SettingAsync(app, "search.cloudflareHelperUrl"));

        await PatchAsync(app, new { cloudflareHelperUrl = "" });

        var cleared = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(string.IsNullOrEmpty(cleared.GetProperty("cloudflareHelperUrl").GetString()));
        Assert.Equal(180, cleared.GetProperty("cloudflareHelperTimeoutSeconds").GetInt32());
        Assert.True(string.IsNullOrEmpty(await SettingAsync(app, "search.cloudflareHelperUrl")));
    }

    [Fact]
    public async Task The_import_policy_fields_are_stored_under_their_own_keys_within_their_bounds_and_read_back_as_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PatchAsync(app, new
        {
            renameOnImport = true, renameFoldersOnImport = true, useHardlinks = true, minimumFreeSpaceMb = 2048, skipFreeSpaceCheck = true,
            setPermissionsOnImport = true, folderChmod = "755", chownGroup = "media",
            leftoverDownloadCleanupEnabled = true, leftoverDownloadCleanupAgeHours = 72, leftoverExcludePatterns = "*.nfo, Thumbs.db", leftoverMinAgeHours = 5,
            forceImportEnabled = true, forceImportMaxTries = 4, forceImportMinimumMegabytes = 250, unmatchedImportPolicy = "give-up", unmatchedImportGiveUpDays = 14
        });

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("renameOnImport").GetBoolean());
        Assert.True(settings.GetProperty("renameFoldersOnImport").GetBoolean());
        Assert.True(settings.GetProperty("useHardlinks").GetBoolean());
        Assert.Equal(2048, settings.GetProperty("minimumFreeSpaceMb").GetInt32());
        Assert.Equal(72, settings.GetProperty("leftoverDownloadCleanupAgeHours").GetInt32());
        Assert.Equal("*.nfo, Thumbs.db", settings.GetProperty("leftoverExcludePatterns").GetString());
        Assert.Equal(4, settings.GetProperty("forceImportMaxTries").GetInt32());
        Assert.Equal(14, settings.GetProperty("unmatchedImportGiveUpDays").GetInt32());
        Assert.Equal("true", await SettingAsync(app, "media.renameOnImport"));
        Assert.Equal("true", await SettingAsync(app, "media.renameFoldersOnImport"));
        Assert.Equal("true", await SettingAsync(app, "media.useHardlinks"));
        Assert.Equal("2048", await SettingAsync(app, "import.minimumFreeSpaceMb"));
        Assert.Equal("true", await SettingAsync(app, "downloads.leftoverCleanup.enabled"));
        Assert.Equal("72", await SettingAsync(app, "downloads.leftoverCleanup.ageHours"));
        Assert.Equal("755", await SettingAsync(app, "media.folderChmod"));
        Assert.Equal("true", await SettingAsync(app, "imports.forceImport.enabled"));
        Assert.Equal("250", await SettingAsync(app, "imports.forceImport.minimumMegabytes"));
        Assert.Equal("14", await SettingAsync(app, "import.unmatchedGiveUpDays"));

        // A value outside what the scheduler honours is held to its bounds rather than stored as typed.
        await PatchAsync(app, new { leftoverDownloadCleanupAgeHours = 99999, forceImportMaxTries = 99, unmatchedImportGiveUpDays = 9999 });
        Assert.Equal("8760", await SettingAsync(app, "downloads.leftoverCleanup.ageHours"));
        Assert.Equal("25", await SettingAsync(app, "imports.forceImport.maxTries"));
        Assert.Equal("365", await SettingAsync(app, "import.unmatchedGiveUpDays"));
    }

    [Fact]
    public async Task The_naming_fields_and_the_safeguards_are_stored_under_their_own_keys_and_read_back_as_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        await PatchAsync(app, new
        {
            movieFolderFormat = "{Movie Title}", seriesFolderFormat = "{Series Title}", movieFileFormat = "{Movie Title} ({Release Year})", episodeFileFormat = "{Series Title} S{season:00}E{episode:00}",
            replaceIllegalCharacters = false, colonReplacementMode = "dash", seasonFolderFormat = "Season {season:00}", specialsFolderFormat = "Specials",
            searchScoringMode = "rules-only", releaseNeverGrabPatterns = "cam, hdcam"
        });

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("{Movie Title}", settings.GetProperty("movieFolderFormat").GetString());
        Assert.False(settings.GetProperty("replaceIllegalCharacters").GetBoolean());
        Assert.Equal("Season {season:00}", settings.GetProperty("seasonFolderFormat").GetString());
        Assert.Equal("{Movie Title}", await SettingAsync(app, "media.movieFolderFormat"));
        Assert.Equal("false", await SettingAsync(app, "media.replaceIllegalCharacters"));
        Assert.Equal("Specials", await SettingAsync(app, "media.specialsFolderFormat"));
        Assert.Equal("rules-only", await SettingAsync(app, "search.scoringMode"));
        Assert.Contains("cam", await SettingAsync(app, "search.neverGrabPatterns"));
        Assert.Equal("rules-only", settings.GetProperty("searchScoringMode").GetString());
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_change_a_setting()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        var refused = await anonymous.PatchAsJsonAsync("/api/settings", new { uiTheme = "dark" });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.NotEqual("dark", await SettingAsync(app, "ui.theme"));
    }
}
