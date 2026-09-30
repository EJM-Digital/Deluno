using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1062: the leftover scan proves a download was Deluno's own by asking for
/// the names on disk, not by reading a window of recent dispatches. An own
/// leftover whose dispatch is older than more than a thousand newer ones, or
/// archived, is still proven.
/// </summary>
public sealed class LeftoverProofHasNoDispatchWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");

    [Fact]
    public async Task An_own_dispatch_older_than_a_thousand_newer_ones_is_still_found_by_name()
    {
        var storage = TestStorage.Create();
        try
        {
            var time = new FixedTimeProvider(Now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "TV", "tv", "library", Path.Combine(storage.DataRoot, "tv"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);

            var oldest = await jobs.RecordDownloadDispatchAsync(
                library.Id, "tv", "episode", "episode-old", "Old.Own.Show.S01E01.1080p", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.ArchiveDispatchAsync(oldest, "test", CancellationToken.None);
            for (var index = 0; index < 1100; index++)
            {
                await jobs.RecordDownloadDispatchAsync(
                    library.Id, "tv", "episode", $"episode-{index}", $"Newer.Show.S01E{index:0000}.1080p", "Fixture", client.Id, client.Name, "sent", null,
                    cancellationToken: CancellationToken.None);
            }

            // The entry on disk is a client's duplicate copy, differently cased.
            var candidates = new[] { "old.own.show.s01e01.1080p.1", "Outside.Tool.Movie.2020" }
                .SelectMany(name => OrphanedDownloads.CandidateNames(new DownloadFolderEntry(@"C:Downloads\" + name, true, 0, Now)))
                .ToArray();
            var found = await jobs.FindGrabbedReleaseNamesAsync(candidates, CancellationToken.None);

            Assert.Equal(["Old.Own.Show.S01E01.1080p"], found);
        }
        finally
        {
            storage.Dispose();
        }
    }
}
