using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// #1515, from the rig (The Amazing Spider-Man): Deluno stopped mid-import and started again. Restart recovery finished the
/// import, but the queued job for it stayed, ran again, found the processor's output gone and recorded a false "Import
/// failed" with an open recovery case. Here the same sequence runs on real files and the real database: job claimed, the
/// process stops mid-import (leases released, a staged file and its note left), recovery runs, then the queue runs.
/// </summary>
public sealed class ARestartedImportDoesNotRunAgainOnDiskTests
{
    private const string Lane = "worker-import";
    private const string JobType = "filesystem.import.execute";
    private const string Release = "Sanctuary.2024.WEB.1080p-GROUP";
    private static readonly byte[] Content = Enumerable.Range(0, 4096).Select(value => (byte)((value * 7) % 251)).ToArray();

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        return await RealDiskLibrary.OpenAsync(app);
    }

    [Fact]
    public async Task Restart_recovery_that_finishes_an_import_completes_its_queued_job_so_the_queue_never_runs_it_again()
    {
        await using var world = await StartWorldAsync();
        var jobs = world.App.Services.GetRequiredService<IJobQueueRepository>();
        var movie = await world.AddMovieAsync("Sanctuary", 2024);
        var dispatchId = await world.RecordDispatchAsync(movie.Id, Release);

        // The processor's output: the one copy, which a move import consumes.
        var source = world.WriteDownload(Release + ".mkv");
        File.WriteAllBytes(source, Content);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        var request = RealDiskLibrary.Request(source, "Sanctuary", 2024, mode: "move", dispatchId: dispatchId);
        var job = await world.App.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest(JobType, "processor-watch", JsonSerializer.Serialize(request), "movie", movie.Id),
            CancellationToken.None);

        // The job is claimed and the process stops mid-import: the move reached its staging file and left its note.
        var claimed = await jobs.LeaseNextAsync(Lane, TimeSpan.FromMinutes(5), [JobType], CancellationToken.None);
        Assert.Equal(job.Id, claimed!.Id);
        var folder = Path.Combine(world.Root, "Sanctuary (2024)");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "Sanctuary (2024).mkv");
        var staging = destination + ".deluno-stage-restarttest.tmp";
        File.Move(source, staging);
        Assert.Null(ImportRestartMarkerFile.WriteAtomic(
            ImportRestartMarkerFile.PathFor(staging),
            new ImportRestartMarker(source, destination, staging, "movies", "Sanctuary (2024)", JsonSerializer.Serialize(request))));

        // The restart: leases are released, then restart recovery finishes the import.
        await jobs.ReleaseLeasesAsync([Lane], CancellationToken.None);
        using (var scope = world.App.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<IImportRestartRecoveryService>().RecoverAsync(CancellationToken.None);
            Assert.Equal(1, report.Recovered);
        }

        // Then the queue runs: the finished import's job must not come back.
        var ran = 0;
        while (await jobs.LeaseNextAsync(Lane, TimeSpan.FromMinutes(5), [JobType], CancellationToken.None) is { } next)
        {
            ran++;
            await RunLikeTheWorkerAsync(world, jobs, next);
        }

        Assert.Equal(0, ran);
        Assert.Equal("completed", (await jobs.GetAsync(job.Id, CancellationToken.None))!.Status);
        await AssertImportedOnceAsync(world, dispatchId);
    }

    [Fact]
    public async Task An_import_job_whose_download_is_already_imported_ends_done_with_no_failure_and_no_recovery_case()
    {
        await using var world = await StartWorldAsync();
        var jobs = world.App.Services.GetRequiredService<IJobQueueRepository>();
        var movie = await world.AddMovieAsync("Sanctuary", 2024);
        var dispatchId = await world.RecordDispatchAsync(movie.Id, Release);
        var source = world.WriteDownload(Release + ".mkv");
        File.WriteAllBytes(source, Content);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        var request = RealDiskLibrary.Request(source, "Sanctuary", 2024, mode: "move", dispatchId: dispatchId);
        var first = await world.ImportAsync(request);
        Assert.True(first.Succeeded, first.Message);
        Assert.False(File.Exists(source));

        // A queued job for the same download, whose source is gone because it was imported and tidied.
        var job = await world.App.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest(JobType, "processor-watch", JsonSerializer.Serialize(request), "movie", movie.Id),
            CancellationToken.None);
        var leased = await jobs.LeaseNextAsync(Lane, TimeSpan.FromMinutes(5), [JobType], CancellationToken.None);
        Assert.Equal(job.Id, leased!.Id);
        await RunLikeTheWorkerAsync(world, jobs, leased);

        Assert.Equal("completed", (await jobs.GetAsync(job.Id, CancellationToken.None))!.Status);
        await AssertImportedOnceAsync(world, dispatchId);
    }

    private static async Task RunLikeTheWorkerAsync(RealDiskLibrary world, IJobQueueRepository jobs, JobQueueItem job)
    {
        using var scope = world.App.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().OfType<FilesystemImportExecuteJobHandler>().Single();
        try
        {
            await jobs.CompleteAsync(job.Id, Lane, await handler.HandleAsync(job, CancellationToken.None), CancellationToken.None);
        }
        catch (Exception exception)
        {
            await jobs.FailAsync(job.Id, Lane, exception.Message, CancellationToken.None);
        }
    }

    private static async Task AssertImportedOnceAsync(RealDiskLibrary world, string dispatchId)
    {
        Assert.Equal(Content, File.ReadAllBytes(Assert.Single(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("jobs", $"SELECT COUNT(*) FROM job_queue WHERE job_type = '{JobType}' AND status IN ('failed', 'dead-letter');")));
        Assert.Equal(0L, Convert.ToInt64(await world.ScalarAsync("movies", "SELECT COUNT(*) FROM movie_import_recovery_cases WHERE status = 'open';")));
        Assert.Equal("imported", await world.ScalarAsync("jobs", $"SELECT import_status FROM download_dispatches WHERE id = '{dispatchId}';"));
    }
}
