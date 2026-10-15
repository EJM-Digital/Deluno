using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// CollectionExclusionMigrationHostedService on the real Movies and Platform database files: an exclusion written by the old
/// per-collection flag moves into the shared record, the old flag is cleared only after the shared write is in, a decision is
/// never lost when the shared write fails, and running it twice moves nothing twice.
/// </summary>
public sealed class CollectionExclusionMoveOnRealDatabasesTests
{
    private sealed class NoReporter : IBackgroundProblemReporter
    {
        public List<string> Reported { get; } = [];
        public void Report(string key, string title, string message, string reason) => Reported.Add(reason);
        public void Clear(string key) { }
    }

    private sealed class FailingExclusions : IUnifiedExclusionRepository
    {
        public Task<IReadOnlyList<MediaExclusionItem>> ListActiveAsync(string? mediaType, string? sourceKind, string? sourceId, CancellationToken cancellationToken) => throw new InvalidOperationException("the shared record is locked");
        public Task<MediaExclusionItem?> UpsertAsync(UpsertMediaExclusionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("the shared record is locked");
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => throw new InvalidOperationException("the shared record is locked");
        public Task<bool> DeleteByScopeAsync(string sourceKind, string sourceId, string entryKey, CancellationToken cancellationToken) => throw new InvalidOperationException("the shared record is locked");
    }

    private static async Task SeedAsync(ApplicationTestHost app)
    {
        await ApiScenes.ExecuteAsync(app, "movies",
            """
            INSERT INTO movie_collections (id, provider, provider_id, library_id, library_name, root_path, name, created_utc, updated_utc)
            VALUES ('col-1', 'tmdb', '10', 'lib-1', 'Films', '/films', 'The Trilogy', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO movie_collection_members (collection_id, provider_id, title, release_year, imdb_id, is_excluded, created_utc, updated_utc)
            VALUES ('col-1', '101', 'Part One', 2001, 'tt0000101', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                   ('col-1', '102', 'Part Two', 2003, 'tt0000102', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                   ('col-1', '103', 'Part Three', 2005, NULL, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """);
    }

    private static Task<long> StillFlaggedAsync(ApplicationTestHost app)
        => ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collection_members WHERE is_excluded = 1;");

    [Fact]
    public async Task Old_flagged_exclusions_move_into_the_shared_record_then_the_flag_is_cleared_and_a_second_run_moves_nothing_more()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);
        var exclusions = app.Services.GetRequiredService<IUnifiedExclusionRepository>();
        var service = new CollectionExclusionMigrationHostedService(
            app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(),
            exclusions,
            NullLogger<CollectionExclusionMigrationHostedService>.Instance,
            new NoReporter());

        await service.StartAsync(CancellationToken.None);

        var moved = await exclusions.ListActiveAsync("movies", MediaExclusionSourceKinds.Collection, "col-1", CancellationToken.None);
        Assert.Equal(["101", "103"], moved.Select(item => item.EntryKey).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.All(moved, item => Assert.Equal("The Trilogy", item.SourceName));
        Assert.True(File.Exists(Path.Combine(app.DataRoot, "movies.db")), "the move was made in the real Movies database file");
        Assert.Equal(0, await StillFlaggedAsync(app));
        Assert.Equal(3L, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collection_members;"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, (await exclusions.ListActiveAsync("movies", MediaExclusionSourceKinds.Collection, "col-1", CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_shared_record_that_cannot_be_written_leaves_every_old_flag_in_place_so_no_decision_is_lost_and_Health_hears_of_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);
        var reporter = new NoReporter();
        var service = new CollectionExclusionMigrationHostedService(
            app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(),
            new FailingExclusions(),
            NullLogger<CollectionExclusionMigrationHostedService>.Instance,
            reporter);

        await service.StartAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(app.DataRoot, "movies.db")));
        Assert.Equal(2, await StillFlaggedAsync(app));
        Assert.Equal("the shared record is locked", Assert.Single(reporter.Reported));
    }
}
