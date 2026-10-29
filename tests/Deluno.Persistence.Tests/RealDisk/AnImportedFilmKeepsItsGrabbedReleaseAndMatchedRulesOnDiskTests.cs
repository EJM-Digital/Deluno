using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// An imported film is renamed to the library pattern, and the stored evidence for the file still names the release Deluno
/// grabbed, the rules it matched and the plan that judged it. Run through the real import on real files and the real database file.
/// </summary>
public sealed class AnImportedFilmKeepsItsGrabbedReleaseAndMatchedRulesOnDiskTests
{
    private const string Release = "Sintel.2010.1080p.NF.WEB-DL.x264-DELUNO";

    [Fact]
    public async Task A_renamed_film_has_a_stored_snapshot_naming_the_release_it_was_grabbed_as_the_rule_it_matched_and_the_plan_that_judged_it()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { renameOnImport = true })).IsSuccessStatusCode);
        var guide = GuidePackageCatalog.Current.CustomFormats.Single(item =>
            item.Name.Equals("NF", StringComparison.Ordinal) && GuideFormatConditions.AppliesTo(item, "movies"));
        var format = await app.Client.PostAsJsonAsync("/api/custom-formats", new
        {
            name = guide.Name, mediaType = "movies", score = guide.OriginalScore, trashId = guide.TrashId,
            conditionsJson = GuideFormatConditions.For(guide), isEnabled = true
        });
        Assert.True(format.IsSuccessStatusCode, await format.Content.ReadAsStringAsync());
        var formatId = JsonDocument.Parse(await format.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "1080p", mediaType = "movies", cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p", customFormatIds = formatId,
            upgradeUntilCutoff = true, upgradeUnknownItems = false, formatIntents = new Dictionary<string, string> { [formatId] = "prefer" }
        });
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        var profileId = JsonDocument.Parse(await profile.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await app.Client.PutAsJsonAsync($"/api/libraries/{world.Library.Id}/quality-profile", new { qualityProfileId = profileId })).IsSuccessStatusCode);
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv");
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "copy", dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        var file = result.Response!.Preview.DestinationPath;
        Assert.True(File.Exists(file));
        Assert.NotEqual(Release + ".mkv", Path.GetFileName(file));
        Assert.Equal(Release, (await world.ScalarAsync("movies", "SELECT release_name_evidence FROM media_preference_evaluations;"))?.ToString());
        Assert.Contains(formatId, (await world.ScalarAsync("movies", "SELECT matched_rule_ids_json FROM media_preference_evaluations;"))?.ToString());
        Assert.Equal(file, (await world.ScalarAsync("movies", "SELECT file_path FROM media_preference_evaluations;"))?.ToString());
        Assert.False(string.IsNullOrWhiteSpace((await world.ScalarAsync("movies", "SELECT plan_id FROM media_preference_evaluations;"))?.ToString()));
        Assert.False(string.IsNullOrWhiteSpace((await world.ScalarAsync("movies", "SELECT plan_version FROM media_preference_evaluations;"))?.ToString()));
        Assert.False(string.IsNullOrWhiteSpace((await world.ScalarAsync("movies", "SELECT plan_hash FROM media_preference_evaluations;"))?.ToString()));
        Assert.Contains("WEB 1080p", (await world.ScalarAsync("movies", "SELECT facts_json FROM media_preference_evaluations;"))?.ToString());
    }
}
