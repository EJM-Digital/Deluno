using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// LibraryImportExistingJobHandler over a real library folder, the real catalogue files and the real job queue: one job imports
/// one bounded slice and, while films remain, queues the next slice under a key that names the position; the last slice queues
/// nothing; every film comes in exactly once and no file is touched; a payload that names no run fails the job.
/// </summary>
public sealed class LibraryImportSliceHandlerOnDiskTests
{
    private static JobQueueItem Job(string runId, string libraryId, string? payload = null) => new(
        "job-1", "library.import.existing", "library-import", "running", payload ?? JsonSerializer.Serialize(new { RunId = runId, LibraryId = libraryId }),
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, "library", libraryId, null, null, 3, null, null);

    [Fact]
    public async Task One_job_imports_one_slice_and_queues_the_next_until_every_film_is_in_exactly_once_without_touching_a_file()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton(new LibraryImportSliceOptions(MaxItemsPerSlice: 3, TimeSpan.FromSeconds(20), MovieBatchSize: 2, SeriesBatchSize: 2)));
        var root = Path.Combine(app.DataRoot, "movies");
        var files = new List<string>();
        for (var number = 1; number <= 8; number++)
        {
            var folder = Path.Combine(root, $"Film Number {number} ({2000 + number})");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Film.Number.{number}.{2000 + number}.1080p.BluRay.x264-GRP.mkv");
            File.WriteAllBytes(file, [1, 2, 3, (byte)number]);
            files.Add(file);
        }

        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films", mediaType = "movies", purpose = "collection", rootPath = root, importWorkflow = "copy",
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var scope = app.Services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueueRepository>();
        var handler = new LibraryImportExistingJobHandler(import, scope.ServiceProvider.GetRequiredService<IJobScheduler>());
        var run = (await import.StartAsync(libraryId, CancellationToken.None))!.Run.Id;
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        var message = await handler.HandleAsync(Job(run, libraryId), CancellationToken.None);

        var afterFirst = (await movies.ListAsync(CancellationToken.None)).Count;
        Assert.InRange(afterFirst, 1, 3);
        Assert.False(string.IsNullOrWhiteSpace(message));
        var continuation = Assert.Single((await jobs.ListAsync(100, CancellationToken.None)).Where(job => job.JobType == "library.import.existing" && job.DedupeKey is not null).ToArray());
        Assert.StartsWith($"library.import.existing:{run}:", continuation.DedupeKey, StringComparison.Ordinal);
        Assert.Equal("library-import", continuation.Source);
        using (var payload = JsonDocument.Parse(continuation.PayloadJson!))
        {
            Assert.Equal(run, payload.RootElement.GetProperty("RunId").GetString());
        }

        for (var slice = 0; slice < 10 && (await import.GetProgressAsync(libraryId, CancellationToken.None))!.Run.Status != LibraryImportRunStatuses.Completed; slice++)
        {
            await handler.HandleAsync(Job(run, libraryId), CancellationToken.None);
        }

        var finished = (await import.GetProgressAsync(libraryId, CancellationToken.None))!;
        Assert.Equal(LibraryImportRunStatuses.Completed, finished.Run.Status);
        var imported = await movies.ListAsync(CancellationToken.None);
        Assert.Equal(8, imported.Count);
        Assert.Equal(8, imported.Select(item => item.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var queuedBeforeLast = (await jobs.ListAsync(100, CancellationToken.None)).Count(job => job.JobType == "library.import.existing");
        await handler.HandleAsync(Job(run, libraryId), CancellationToken.None);
        Assert.Equal(queuedBeforeLast, (await jobs.ListAsync(100, CancellationToken.None)).Count(job => job.JobType == "library.import.existing"));
        for (var number = 0; number < files.Count; number++)
        {
            Assert.Equal(new byte[] { 1, 2, 3, (byte)(number + 1) }, File.ReadAllBytes(files[number]));
        }
    }

    [Fact]
    public async Task A_payload_that_names_no_run_fails_the_job_and_imports_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var scope = app.Services.CreateScope();
        var handler = new LibraryImportExistingJobHandler(
            scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>(),
            scope.ServiceProvider.GetRequiredService<IJobScheduler>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Job("", "lib", payload: "{\"LibraryId\":\"lib\"}"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Job("", "lib", payload: "not json"), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(app.DataRoot, "movies.db")));
        Assert.Empty(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }
}
