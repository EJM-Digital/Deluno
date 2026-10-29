using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The evidence stored for an imported episode (the release it was grabbed as, and the rules it matched) must follow the
/// file when the show's folder is renamed. Probed on the real series database file; the rename writes the new paths
/// through this one repository method, as the show rename route does.
/// </summary>
public sealed class RenamingAShowFolderKeepsAnEpisodesEvidenceOnDiskTests
{
    [Fact]
    public async Task Renaming_a_shows_folder_keeps_the_episodes_release_name_and_matched_rules_and_points_them_at_the_new_path()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var show = await series.AddAsync(new CreateSeriesRequest("The Expanse", 2015, "tt3230854"), CancellationToken.None);
        await series.SyncEpisodeCatalogueAsync(show.Id, [new CatalogueEpisodeItem(1, 1, "Dulcinea", null, DateTimeOffset.UtcNow.AddDays(-40))], "tmdb", CancellationToken.None);
        const string oldEpisode = "/tv-main/The Expanse (2015)/Season 01/S01E01.mkv";
        const string newEpisode = "/tv-main/The Expanse (2015) [renamed]/Season 01/S01E01.mkv";
        await series.ImportExistingAsync(
            "tv-main", "The Expanse", 2015, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: oldEpisode, fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: oldEpisode, FileSizeBytes: 1)],
            CancellationToken.None);
        var episodeId = (await series.ListEpisodeFilePathsAsync(show.Id, CancellationToken.None)).Single().EpisodeId;
        await world.SqlAsync("series", $"""
            INSERT INTO media_preference_evaluations (id, media_id, library_id, file_identity, file_path, file_size_bytes, plan_id, plan_version, plan_hash,
                facts_json, evaluation_json, matched_rule_ids_json, release_name_evidence, evaluated_utc, created_utc, updated_utc)
            VALUES ('eval-1', '{show.Id}', 'tv-main', 'identity-1', '{oldEpisode}', 1, 'plan', '1', 'hash', json_object(), json_object(), '["rule-nf","rule-group"]',
                'The.Expanse.S01E01.1080p.NF.WEB-DL-GRP', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """);

        Assert.Contains(Directory.GetFiles(app.DataRoot, "series.db", SearchOption.AllDirectories), path => File.Exists(path));

        await series.UpdateEpisodeFileLocationsAsync([(episodeId, oldEpisode, newEpisode)], CancellationToken.None);

        Assert.Equal(newEpisode, (await world.ScalarAsync("series", "SELECT file_path FROM media_preference_evaluations;"))?.ToString());
        Assert.Equal("The.Expanse.S01E01.1080p.NF.WEB-DL-GRP", (await world.ScalarAsync("series", "SELECT release_name_evidence FROM media_preference_evaluations;"))?.ToString());
        Assert.Equal("[\"rule-nf\",\"rule-group\"]", (await world.ScalarAsync("series", "SELECT matched_rule_ids_json FROM media_preference_evaluations;"))?.ToString());
        Assert.Equal("1", (await world.ScalarAsync("series", "SELECT COUNT(*) FROM media_preference_evaluations;"))?.ToString());
    }
}
