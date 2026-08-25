using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class LibraryViewPersistenceTests
{
    [Fact]
    public async Task Saved_view_round_trips_its_library_filter()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO users (
                    id, username, display_name, password_hash, avatar_initials,
                    security_stamp, created_utc, updated_utc
                ) VALUES (
                    'view-user', 'view-user', 'View User', 'not-used', 'VU',
                    'stamp', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var created = await repository.CreateLibraryViewAsync(
            "view-user",
            new CreateLibraryViewRequest(
                Variant: "movies",
                LibraryId: "anime-library",
                Name: "Anime movies",
                QuickFilter: "all",
                SortField: "title",
                SortDirection: "asc",
                ViewMode: "grid",
                CardSize: "md",
                DisplayOptionsJson: "{}",
                RulesJson: "[]"),
            CancellationToken.None);

        Assert.Equal("anime-library", created.LibraryId);
        var listed = Assert.Single(await repository.ListLibraryViewsAsync("view-user", "movies", CancellationToken.None));
        Assert.Equal("anime-library", listed.LibraryId);

        var updated = await repository.UpdateLibraryViewAsync(
            "view-user",
            created.Id,
            new UpdateLibraryViewRequest(
                LibraryId: null,
                Name: "All movies",
                QuickFilter: "all",
                SortField: "title",
                SortDirection: "asc",
                ViewMode: "grid",
                CardSize: "md",
                DisplayOptionsJson: "{}",
                RulesJson: "[]"),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Null(updated!.LibraryId);
    }
}
