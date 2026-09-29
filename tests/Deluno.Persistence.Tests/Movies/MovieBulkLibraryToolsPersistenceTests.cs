using Deluno.Infrastructure.Storage;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #787: a bulk minimum-availability action, and a bulk library reassignment
/// that can actually move files rather than only re-pointing the record.
/// These exercise the persistence contract both bulk tools rely on -
/// <see cref="SqliteMovieCatalogRepository.UpdateMinimumAvailabilityAsync"/>
/// and the two <see cref="SqliteMovieCatalogRepository.ReassignLibraryAsync"/>
/// overloads the reassign-library endpoint chooses between depending on
/// whether the user opted in to moving files.
/// </summary>
public sealed class MovieBulkLibraryToolsPersistenceTests
{
    private static async Task<SqliteMovieCatalogRepository> CreateRepositoryAsync(TestStorage storage, FixedTimeProvider timeProvider)
    {
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }

    [Fact]
    public async Task Bulk_minimum_availability_updates_every_selected_movie_and_leaves_others_untouched()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var selected = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        var untouched = await repository.AddAsync(new CreateMovieRequest("Dune Part Three", 2026, "tt00000001"), CancellationToken.None);

        // The endpoint loops one id at a time, same as every other bulk tool
        // in this file - this pins that the underlying update genuinely only
        // touches the movie it is given.
        foreach (var id in new[] { selected.Id })
        {
            Assert.True(await repository.UpdateMinimumAvailabilityAsync(id, "announced", CancellationToken.None));
        }

        var selectedAfter = await repository.GetByIdAsync(selected.Id, CancellationToken.None);
        var untouchedAfter = await repository.GetByIdAsync(untouched.Id, CancellationToken.None);

        Assert.Equal("announced", selectedAfter!.MinimumAvailability);
        Assert.Equal("released", untouchedAfter!.MinimumAvailability);
    }

    [Fact]
    public async Task ReassignLibrary_without_a_moved_path_only_repoints_the_record()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2024, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: "/library/movies-main/Dune Part Two (2024)/Dune.mkv", fileSizeBytes: 42,
            CancellationToken.None, movieId: movie.Id);

        var reassigned = await repository.ReassignLibraryAsync(
            [movie.Id], "movies-main", "movies-4k", movedFilePathsByMovieId: null, CancellationToken.None);

        Assert.Equal(1, reassigned);
        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal("movies-4k", after!.LibraryId);
        // The whole point of the no-move path: the record moved, the file
        // path on record did not.
        Assert.Equal("/library/movies-main/Dune Part Two (2024)/Dune.mkv", after.FilePath);
    }

    [Fact]
    public async Task ReassignLibrary_with_a_moved_path_records_both_the_new_library_and_the_new_file_location()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2024, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: "/library/movies-main/Dune Part Two (2024)/Dune.mkv", fileSizeBytes: 42,
            CancellationToken.None, movieId: movie.Id);

        var newPath = "/library/movies-4k/Dune Part Two (2024)/Dune.mkv";
        var reassigned = await repository.ReassignLibraryAsync(
            [movie.Id], "movies-main", "movies-4k",
            movedFilePathsByMovieId: new Dictionary<string, string> { [movie.Id] = newPath },
            CancellationToken.None);

        Assert.Equal(1, reassigned);
        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal("movies-4k", after!.LibraryId);
        Assert.Equal(newPath, after.FilePath);
    }

    [Fact]
    public async Task ReassignLibrary_with_a_partial_move_map_only_records_the_path_for_ids_present_in_it()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var moved = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        var failed = await repository.AddAsync(new CreateMovieRequest("Dune Part Three", 2026, "tt00000001"), CancellationToken.None);
        foreach (var (movieEntry, path) in new[]
                 {
                     (moved, "/library/movies-main/Dune Part Two (2024)/Dune.mkv"),
                     (failed, "/library/movies-main/Dune Part Three (2026)/Dune3.mkv")
                 })
        {
            await repository.EnsureWantedStateAsync(
                movieEntry.Id, "movies-main", "covered", "Imported.", hasFile: true,
                currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
            await repository.ImportExistingAsync(
                "movies-main", movieEntry.Title, movieEntry.ReleaseYear, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
                filePath: path, fileSizeBytes: 42, CancellationToken.None, movieId: movieEntry.Id);
        }

        // Mirrors the endpoint's behaviour when one file's move throws and the
        // other succeeds: only the id whose move succeeded is reassigned, and
        // only its record gets a new path (#787's "both the move and no-move
        // paths" acceptance line, in the one bulk call that mixes them).
        var newPath = "/library/movies-4k/Dune Part Two (2024)/Dune.mkv";
        var reassigned = await repository.ReassignLibraryAsync(
            [moved.Id], "movies-main", "movies-4k",
            movedFilePathsByMovieId: new Dictionary<string, string> { [moved.Id] = newPath },
            CancellationToken.None);

        Assert.Equal(1, reassigned);
        var movedAfter = await repository.GetByIdAsync(moved.Id, CancellationToken.None);
        var failedAfter = await repository.GetByIdAsync(failed.Id, CancellationToken.None);
        Assert.Equal("movies-4k", movedAfter!.LibraryId);
        Assert.Equal(newPath, movedAfter.FilePath);
        // Never asked to reassign, so it keeps its original library and path.
        Assert.Equal("movies-main", failedAfter!.LibraryId);
        Assert.Equal("/library/movies-main/Dune Part Three (2026)/Dune3.mkv", failedAfter.FilePath);
    }

    [Fact]
    public async Task Relocating_a_folder_whose_name_starts_with_an_uppercase_non_ASCII_letter_still_rewrites_every_path()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Édith Piaf", 2007, "tt0450188"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2007, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: "/library/main/ÉDITH Piaf (2007)/Edith.mkv", fileSizeBytes: 42,
            CancellationToken.None, movieId: movie.Id);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO movie_subtitle_state (movie_id, language, forced, hearing_impaired, source, file_path, created_utc, updated_utc) VALUES ('{movie.Id}', 'en', 0, 0, 'external', '/library/main/ÉDITH Piaf (2007)/Subs/Edith.en.srt', 'x', 'x');";
            await command.ExecuteNonQueryAsync();
        }

        // The folder is written with a lowercase é on the way in: it is the same folder.
        var rewritten = await repository.RelocateTrackedFilePathsAsync(
            movie.Id, "/library/main/édith Piaf (2007)", "/library/4k/Édith Piaf (2007)", CancellationToken.None);

        Assert.Equal(2, rewritten);
        var paths = await repository.ListTrackedFilePathsAsync(movie.Id, CancellationToken.None);
        Assert.Contains("/library/4k/Édith Piaf (2007)/Edith.mkv", paths);
        Assert.Contains("/library/4k/Édith Piaf (2007)/Subs/Edith.en.srt", paths);
    }

    [Fact]
    public async Task Relocating_a_moved_folder_rewrites_every_tracked_path_under_it_and_only_those()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        var repository = await CreateRepositoryAsync(storage, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Arrival", 2016, "tt2543164"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2016, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: "/library/main/Arrival (2016)/Arrival.mkv", fileSizeBytes: 42,
            CancellationToken.None, movieId: movie.Id);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            foreach (var sql in new[]
                     {
                         $"INSERT INTO movie_subtitle_state (movie_id, language, forced, hearing_impaired, source, file_path, created_utc, updated_utc) VALUES ('{movie.Id}', 'en', 0, 0, 'external', '/library/main/Arrival (2016)/Subs/Arrival.en.srt', 'x', 'x');",
                         $"INSERT INTO movie_subtitle_state (movie_id, language, forced, hearing_impaired, source, file_path, created_utc, updated_utc) VALUES ('{movie.Id}', 'fr', 0, 0, 'external', '/library/main/Arrival (2016) Extra/Arrival.fr.srt', 'x', 'x');",
                         $"INSERT INTO movie_subtitle_scan (movie_id, file_path, probe_status, scanned_utc) VALUES ('{movie.Id}', '/library/main/Arrival (2016)/Arrival.mkv', 'ok', 'x');"
                     })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }

        var rewritten = await repository.RelocateTrackedFilePathsAsync(
            movie.Id, "/library/main/Arrival (2016)", "/library/4k/Arrival (2016)", CancellationToken.None);

        Assert.Equal(3, rewritten);
        var paths = await repository.ListTrackedFilePathsAsync(movie.Id, CancellationToken.None);
        Assert.Contains("/library/4k/Arrival (2016)/Arrival.mkv", paths);
        Assert.Contains("/library/4k/Arrival (2016)/Subs/Arrival.en.srt", paths);
        // A sibling folder that merely starts with the same name is not under the moved folder.
        Assert.Contains("/library/main/Arrival (2016) Extra/Arrival.fr.srt", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/library/main/Arrival (2016)/", StringComparison.Ordinal));
    }
}
