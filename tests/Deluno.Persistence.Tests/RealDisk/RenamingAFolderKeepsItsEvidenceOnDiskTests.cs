using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The evidence stored for an imported film (the release it was grabbed as, and the preferences it matched) must follow
/// the file when its folder is renamed on disk. Probed on a real library with a real folder rename and the real database file.
/// </summary>
public sealed class RenamingAFolderKeepsItsEvidenceOnDiskTests
{
    // DEFECT (the register's "suspected defect", now shown on a real library): the bulk rename moves the folder and rewrites the
    // title's tracked path, but media_preference_evaluations keeps the old file_path, so the stored release name and matched rules
    // no longer belong to any file. Observed: tracked path is the new one, evidence path is still ...\Sintel (2010)\Sintel.2010...mkv.
    [Fact(Skip = "Defect: renaming a film's folder (bulk rename-apply) leaves media_preference_evaluations.file_path on the old path, so the stored release evidence is orphaned.")]
    public async Task Renaming_a_films_folder_leaves_the_stored_evidence_pointing_at_the_file_where_it_now_is()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "1080p", mediaType = "movies", cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p", customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        var profileId = JsonDocument.Parse(await profile.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await app.Client.PutAsJsonAsync($"/api/libraries/{world.Library.Id}/quality-profile", new { qualityProfileId = profileId })).IsSuccessStatusCode);
        var (movie, file) = await world.ImportFirstCopyAsync("Sintel", 2010, "Sintel.2010.WEB.1080p.x264-GRP");
        var before = (await world.ScalarAsync("movies", "SELECT COUNT(*) FROM media_preference_evaluations WHERE file_path IS NOT NULL;"))?.ToString();
        Assert.Equal("1", before);

        var renamed = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-apply", new { movieIds = new[] { movie.Id }, template = "Sintel Renamed ({Release Year})" });
        var body = await renamed.Content.ReadAsStringAsync();

        var newFile = Path.Combine(world.Root, "Sintel Renamed (2010)", Path.GetFileName(file));
        Assert.True(File.Exists(newFile), "the folder was renamed on disk: " + body);
        Assert.False(File.Exists(file));
        var trackedNow = (await world.Movies.GetByIdAsync(movie.Id, CancellationToken.None))!.FilePath;
        Assert.Equal(newFile, trackedNow);
        var evidencePath = (await world.ScalarAsync("movies", "SELECT file_path FROM media_preference_evaluations WHERE file_path IS NOT NULL;"))?.ToString();
        Assert.Equal(newFile, evidencePath);
    }
}
