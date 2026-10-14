using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// #1323: every column in the five databases that holds a media file's path is either rewritten with a folder rename (in the
/// same transaction) or deliberately left alone because it is a record of what happened. The columns are listed from the
/// real schema, so a new path column cannot be added without deciding which it is.
/// </summary>
public sealed class EveryStoredFilePathFollowsAFolderRenameOnDiskTests
{
    private static readonly string[] Databases = ["platform", "movies", "series", "jobs", "cache"];

    private const string Follows = "follows";
    private const string History = "history";
    private const string Other = "other";

    /// <summary>database.table.column to what it is. A path column that is not here fails the schema test.</summary>
    private static readonly Dictionary<string, (string Kind, string Why)> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        // A media file's path in the catalogue: rewritten with the folder.
        ["movies.movie_wanted_state.file_path"] = (Follows, "the title's tracked file"),
        ["movies.movie_subtitle_state.file_path"] = (Follows, "the subtitle files beside it"),
        ["movies.movie_subtitle_scan.file_path"] = (Follows, "the file the last subtitle scan read"),
        ["movies.media_preference_evaluations.file_path"] = (Follows, "the stored release evidence for the file"),
        ["movies.movie_entries.primary_file_path"] = (Follows, "a copy of the tracked path kept by a database trigger"),
        ["series.episode_entries.file_path"] = (Follows, "the episode's tracked file"),
        ["series.series_wanted_state.file_path"] = (Follows, "the show's remembered file"),
        ["series.episode_subtitle_state.file_path"] = (Follows, "the subtitle files beside it"),
        ["series.episode_subtitle_scan.file_path"] = (Follows, "the file the last subtitle scan read"),
        ["series.media_preference_evaluations.file_path"] = (Follows, "the stored release evidence for the file"),
        ["series.series_entries.primary_file_path"] = (Follows, "a copy of the tracked path kept by a database trigger"),

        // A record of what happened at the time. It is not a pointer the app reads to find the file, and it lives in another database file.
        ["jobs.activity_events.file_path"] = (History, "the Activity line says where the file was then"),
        ["jobs.download_dispatches.imported_file_path"] = (History, "where this download was imported then"),
        ["jobs.download_dispatches.replacement_expected_path"] = (History, "a safety check on a download in flight: a different path refuses the overwrite"),
        ["platform.processor_handoff_outcomes.imported_path"] = (History, "where the processor's output was imported then"),
        ["platform.library_import_issues.source_path"] = (History, "the folder the import issue was found in"),

        // Not the path of a media file in a library.
        ["platform.destination_rules.root_path"] = (Other, "a folder setting"),
        ["platform.download_client_path_mappings.local_path"] = (Other, "a folder setting"),
        ["platform.download_client_path_mappings.remote_path"] = (Other, "a folder setting"),
        ["platform.indexer_sources.api_path"] = (Other, "a URL path"),
        ["platform.intake_sources.quality_profile_id"] = (Other, "an id"),
        ["platform.libraries.downloads_path"] = (Other, "a folder setting"),
        ["platform.libraries.processor_output_path"] = (Other, "a folder setting"),
        ["platform.libraries.quality_profile_id"] = (Other, "an id"),
        ["platform.libraries.root_path"] = (Other, "a folder setting"),
        ["platform.library_import_runs.root_path"] = (Other, "the folder that was scanned"),
        ["platform.media_server_path_mappings.deluno_path"] = (Other, "a folder setting"),
        ["platform.media_server_path_mappings.server_path"] = (Other, "a folder setting"),
        ["platform.policy_sets.quality_profile_id"] = (Other, "an id"),
        ["platform.processor_handoffs.output_path"] = (Other, "the processor's output folder"),
        ["platform.processor_handoffs.source_path"] = (Other, "the download folder"),
        ["platform.processor_path_mappings.deluno_path"] = (Other, "a folder setting"),
        ["platform.processor_path_mappings.processor_path"] = (Other, "a folder setting"),
        ["platform.root_paths.root_path"] = (Other, "a folder setting"),
        ["platform.seerr_servers.movie_4k_quality_profile_id"] = (Other, "an id"),
        ["platform.seerr_servers.movie_quality_profile_id"] = (Other, "an id"),
        ["platform.seerr_servers.series_4k_quality_profile_id"] = (Other, "an id"),
        ["platform.seerr_servers.series_quality_profile_id"] = (Other, "an id"),
        ["movies.media_preference_evaluations.file_identity"] = (Other, "a hash; every lookup is by path and size"),
        ["movies.media_preference_evaluations.file_size_bytes"] = (Other, "a size"),
        ["movies.movie_collections.quality_profile_id"] = (Other, "an id"),
        ["movies.movie_collections.quality_profile_name"] = (Other, "a name"),
        ["movies.movie_collections.root_path"] = (Other, "a folder setting"),
        ["movies.movie_entries.file_count"] = (Other, "a count"),
        ["movies.movie_entries.primary_file_size_bytes"] = (Other, "a size"),
        ["movies.movie_entries.primary_has_file"] = (Other, "a flag"),
        ["movies.movie_entries.quality_profile_id"] = (Other, "an id"),
        ["movies.movie_subtitle_scan.file_size_bytes"] = (Other, "a size"),
        ["movies.movie_wanted_state.file_size_bytes"] = (Other, "a size"),
        ["movies.movie_wanted_state.has_file"] = (Other, "a flag"),
        ["series.episode_entries.file_size_bytes"] = (Other, "a size"),
        ["series.episode_entries.has_file"] = (Other, "a flag"),
        ["series.episode_subtitle_scan.file_size_bytes"] = (Other, "a size"),
        ["series.media_preference_evaluations.file_identity"] = (Other, "a hash; every lookup is by path and size"),
        ["series.media_preference_evaluations.file_size_bytes"] = (Other, "a size"),
        ["series.series_entries.aired_with_file_count"] = (Other, "a count"),
        ["series.series_entries.episode_with_file_count"] = (Other, "a count"),
        ["series.series_entries.file_count"] = (Other, "a count"),
        ["series.series_entries.primary_file_size_bytes"] = (Other, "a size"),
        ["series.series_entries.primary_has_file"] = (Other, "a flag"),
        ["series.series_entries.quality_profile_id"] = (Other, "an id"),
        ["series.series_wanted_state.file_size_bytes"] = (Other, "a size"),
        ["series.series_wanted_state.has_file"] = (Other, "a flag"),
        ["jobs.download_dispatches.imported_file_bytes"] = (Other, "a size"),
        ["cache.artwork_cache.local_path"] = (Other, "a cached poster, not a media file")
    };

    [Fact]
    public async Task Every_path_or_file_column_in_the_five_databases_is_accounted_for()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var found = await ListColumnsAsync(app);

        Assert.Empty(found.Except(Columns.Keys, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(Columns.Keys.Except(found, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task After_a_folder_rename_every_column_that_follows_holds_the_new_path_and_none_holds_the_old()
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
        var oldFolder = Path.GetDirectoryName(file)!;

        // The subtitle rows that the import itself does not write.
        var subtitle = Path.ChangeExtension(file, ".en.srt");
        await world.SqlAsync("movies", $"""
            INSERT INTO movie_subtitle_state (movie_id, language, forced, hearing_impaired, source, file_path, created_utc, updated_utc)
            VALUES ('{movie.Id}', 'en', 0, 0, 'sidecar', '{subtitle.Replace("'", "''")}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT OR REPLACE INTO movie_subtitle_scan (movie_id, file_path, probe_status, subtitle_count, scanned_utc)
            VALUES ('{movie.Id}', '{file.Replace("'", "''")}', 'ok', 1, '2026-01-01T00:00:00Z');
            """);

        // A show whose folder is renamed in the same way (the series endpoint calls this one repository method).
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var show = await series.AddAsync(new CreateSeriesRequest("The Expanse", 2015, "tt3230854"), CancellationToken.None);
        await series.SyncEpisodeCatalogueAsync(show.Id, [new CatalogueEpisodeItem(1, 1, "Dulcinea", null, DateTimeOffset.UtcNow.AddDays(-40))], "tmdb", CancellationToken.None);
        var oldShowFolder = "/tv-main/The Expanse (2015)";
        var oldEpisode = oldShowFolder + "/Season 01/S01E01.mkv";
        await series.ImportExistingAsync(
            "tv-main", "The Expanse", 2015, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: oldEpisode, fileSizeBytes: 1,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: oldEpisode, FileSizeBytes: 1)],
            CancellationToken.None);
        var episodeId = (await series.ListEpisodeFilePathsAsync(show.Id, CancellationToken.None)).Single().EpisodeId;
        await world.SqlAsync("series", $"""
            INSERT INTO episode_subtitle_state (episode_id, language, forced, hearing_impaired, source, file_path, created_utc, updated_utc)
            VALUES ('{episodeId}', 'en', 0, 0, 'sidecar', '{oldShowFolder}/Season 01/S01E01.en.srt', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO episode_subtitle_scan (episode_id, file_path, probe_status, subtitle_count, scanned_utc)
            VALUES ('{episodeId}', '{oldEpisode}', 'ok', 1, '2026-01-01T00:00:00Z');
            INSERT INTO media_preference_evaluations (id, media_id, library_id, file_identity, file_path, file_size_bytes, plan_id, plan_version, plan_hash,
                facts_json, evaluation_json, matched_rule_ids_json, evaluated_utc, created_utc, updated_utc)
            VALUES ('eval-1', '{show.Id}', 'tv-main', 'identity-1', '{oldEpisode}', 1, 'plan', '1', 'hash', json_object(), json_object(), '[]',
                '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """);

        // Every column that should follow has something to follow before the rename.
        var following = Columns.Where(pair => pair.Value.Kind == Follows).Select(pair => pair.Key).ToList();
        foreach (var column in following)
        {
            Assert.True((await ValuesAsync(world, column)).Count > 0, "nothing to follow in " + column);
        }

        var renamed = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-apply", new { movieIds = new[] { movie.Id }, template = "Sintel Renamed ({Release Year})" });
        var newFolder = Path.Combine(world.Root, "Sintel Renamed (2010)");
        Assert.True(Directory.Exists(newFolder), await renamed.Content.ReadAsStringAsync());
        var newShowFolder = "/tv-main/The Expanse (2015) [renamed]";
        await series.UpdateEpisodeFileLocationsAsync([(episodeId, oldEpisode, newShowFolder + "/Season 01/S01E01.mkv")], CancellationToken.None);

        foreach (var column in following)
        {
            var isShow = column.StartsWith("series.", StringComparison.OrdinalIgnoreCase);
            var oldPrefix = isShow ? oldShowFolder + "/" : oldFolder + Path.DirectorySeparatorChar;
            var newPrefix = isShow ? newShowFolder : newFolder;
            var values = await ValuesAsync(world, column);
            Assert.All(values, value =>
            {
                Assert.DoesNotContain(oldPrefix, value, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(newPrefix, value, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    private static async Task<List<string>> ValuesAsync(RealDiskLibrary world, string column)
    {
        var parts = column.Split('.');
        var factory = world.App.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(parts[0], CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {parts[2]} FROM {parts[1]} WHERE {parts[2]} IS NOT NULL AND {parts[2]} <> '';";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<List<string>> ListColumnsAsync(ApplicationTestHost app)
    {
        var factory = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        var found = new List<string>();
        foreach (var database in Databases)
        {
            await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.name, p.name FROM sqlite_master m, pragma_table_info(m.name) p
                WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%' AND (p.name LIKE '%path%' OR p.name LIKE '%file%');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                found.Add($"{database}.{reader.GetString(0)}.{reader.GetString(1)}");
            }
        }

        return found;
    }
}
