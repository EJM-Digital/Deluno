using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Deleting a quality profile that a library, a film, a show and a media plan still name, on the real databases (platform,
/// movies and series): the delete answers 204 and every one of those columns is empty afterwards, so nothing points at a
/// profile that is gone. Refs #1296.
/// </summary>
public sealed class DeletingAQualityProfileClearsEveryReferenceOnRealDatabasesTests
{
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    [Fact]
    public async Task A_profile_a_library_a_film_a_show_and_a_media_plan_use_is_deleted_and_none_of_them_keeps_pointing_at_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Shared", mediaType = "movies", cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var profileId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var tvProfile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Shared TV", mediaType = "tv", cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(tvProfile.IsSuccessStatusCode, await tvProfile.Content.ReadAsStringAsync());
        var tvProfileId = (await tvProfile.ReadJson()).GetProperty("id").GetString()!;

        Assert.True((await app.Client.PutAsJsonAsync($"/api/libraries/{film.LibraryId}/quality-profile", new { qualityProfileId = profileId })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { film.MovieId }, qualityProfileId = profileId })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/series/bulk/quality-profile", new { seriesIds = new[] { show.SeriesId }, qualityProfileId = tvProfileId })).IsSuccessStatusCode);
        var plan = await app.Client.PostAsJsonAsync("/api/policy-sets", new
        {
            name = "Plan", mediaType = "movies", qualityProfileId = profileId, destinationRuleId = (string?)null, customFormatIds = (string?)null,
            searchIntervalOverrideHours = (int?)null, retryDelayOverrideHours = (int?)null, upgradeUntilCutoff = true, isEnabled = true, notes = (string?)null
        });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        var planId = (await plan.ReadJson()).GetProperty("id").GetString()!;
        Assert.Equal(profileId, await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT quality_profile_id FROM libraries WHERE id = '{film.LibraryId}'"));
        Assert.Equal(profileId, await StoredAsync(app, DelunoDatabaseNames.Movies, $"SELECT quality_profile_id FROM movie_entries WHERE id = '{film.MovieId}'"));
        Assert.Equal(tvProfileId, await StoredAsync(app, DelunoDatabaseNames.Series, $"SELECT quality_profile_id FROM series_entries WHERE id = '{show.SeriesId}'"));
        Assert.Equal(profileId, await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT quality_profile_id FROM policy_sets WHERE id = '{planId}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{profileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{tvProfileId}")).StatusCode);

        Assert.Null(await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT quality_profile_id FROM libraries WHERE id = '{film.LibraryId}'"));
        Assert.Null(await StoredAsync(app, DelunoDatabaseNames.Movies, $"SELECT quality_profile_id FROM movie_entries WHERE id = '{film.MovieId}'"));
        Assert.Null(await StoredAsync(app, DelunoDatabaseNames.Series, $"SELECT quality_profile_id FROM series_entries WHERE id = '{show.SeriesId}'"));
        Assert.Null(await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT quality_profile_id FROM policy_sets WHERE id = '{planId}'"));
        Assert.Equal("0", await StoredAsync(app, DelunoDatabaseNames.Platform, $"SELECT COUNT(*) FROM quality_profiles WHERE id IN ('{profileId}', '{tvProfileId}')"));
        Assert.True(File.Exists(film.FilePath), "the film's file is untouched");
    }

    [Fact]
    public async Task Deleting_a_profile_that_is_not_there_answers_not_found_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/quality-profiles/does-not-exist")).StatusCode);
        Assert.True(File.Exists(film.FilePath));
    }
}

internal static class HttpJson
{
    public static Task<JsonElement> ReadJson(this HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>();
}
