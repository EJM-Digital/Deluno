using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Libraries > Remove, on the whole application with its own databases and a real library folder: removing a library
/// never deletes a file from the disk, stops the work queued for it, and ends the downloads still open for it.
/// </summary>
public sealed class LibraryRemovalOnRealFilesTests
{
    /// <summary>Reads the jobs database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> JobsAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Removing_a_library_leaves_every_file_and_folder_on_the_disk_and_takes_only_the_library_off_the_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var other = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, libraryId: await ApiScenes.CreateLibraryAsync(app, "movies", "Other", "Other"));
        var sibling = Path.Combine(film.FolderPath, "Dune (2021).en.srt");
        await File.WriteAllTextAsync(sibling, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");

        var removed = await app.Client.DeleteAsync($"/api/libraries/{film.LibraryId}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.True(File.Exists(film.FilePath), "the film file must stay where it is");
        Assert.True(File.Exists(sibling), "a subtitle beside it must stay too");
        Assert.True(Directory.Exists(film.FolderPath));
        Assert.True(File.Exists(other.FilePath), "another library's files are untouched");
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries");
        Assert.DoesNotContain(libraries.EnumerateArray(), item => item.GetProperty("id").GetString() == film.LibraryId);
        Assert.Contains(libraries.EnumerateArray(), item => item.GetProperty("id").GetString() == other.LibraryId);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/libraries/{film.LibraryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/libraries/no-such-library")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_library_cancels_the_work_queued_for_it_and_ends_its_open_downloads_without_touching_another_librarys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var gone = await ApiScenes.CreateLibraryAsync(app, "movies", "Gone", "Gone");
        var kept = await ApiScenes.CreateLibraryAsync(app, "movies", "Kept", "Kept");
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        foreach (var (library, key) in new[] { (gone, "gone"), (kept, "kept") })
        {
            await scheduler.EnqueueAsync(new EnqueueJobRequest("library.search", "test", "{}", "library", library, IdempotencyKey: $"search:{key}"), CancellationToken.None);
        }

        var queue = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var goneDispatch = await queue.RecordDownloadDispatchAsync(gone, "movies", "movie", "movie-gone", "Gone.Film.2020.1080p-GRP", "Fixture", "client-1", "Client", "sent", null, clientExternalId: "hash-gone", cancellationToken: CancellationToken.None);
        var keptDispatch = await queue.RecordDownloadDispatchAsync(kept, "movies", "movie", "movie-kept", "Kept.Film.2020.1080p-GRP", "Fixture", "client-1", "Client", "sent", null, clientExternalId: "hash-kept", cancellationToken: CancellationToken.None);
        Assert.Equal("1", await JobsAsync(app, $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{gone}'"));
        Assert.NotNull(await dispatches.GetDispatchAsync(goneDispatch, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/libraries/{gone}")).StatusCode);

        Assert.Equal("0", await JobsAsync(app, $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{gone}'"));
        Assert.Equal("1", await JobsAsync(app, $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{kept}'"));
        Assert.Null(await dispatches.GetDispatchAsync(goneDispatch, CancellationToken.None));
        Assert.NotNull(await dispatches.GetDispatchIncludingArchivedAsync(goneDispatch, CancellationToken.None));
        Assert.NotNull(await dispatches.GetDispatchAsync(keptDispatch, CancellationToken.None));
    }
}
