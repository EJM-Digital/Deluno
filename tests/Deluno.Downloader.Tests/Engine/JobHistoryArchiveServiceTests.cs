using Deluno.Downloader.Engine;
using Deluno.Downloader.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Downloader.Tests.Engine;

public class JobHistoryArchiveServiceTests
{
    [Fact]
    public async Task Sweeps_Done_and_Failed_jobs_and_leaves_others_alone()
    {
        var jobs = new FakeJobRepo();
        // Seed: 2 terminal (one Done, one Failed) + 3 non-terminal.
        jobs.Seed(MakeJob("done-1", JobLifecycleState.Done));
        jobs.Seed(MakeJob("failed-1", JobLifecycleState.Failed));
        jobs.Seed(MakeJob("queued-1", JobLifecycleState.Queued));
        jobs.Seed(MakeJob("fetching-1", JobLifecycleState.Fetching));
        jobs.Seed(MakeJob("paused-1", JobLifecycleState.Paused));

        var svc = new JobHistoryArchiveService(jobs, TimeProvider.System,
            NullLogger<JobHistoryArchiveService>.Instance);

        await svc.SweepOnceAsync(CancellationToken.None);

        // Terminal jobs were archived (removed from live store by the
        // fake repo's ArchiveAsync). Non-terminal jobs untouched.
        Assert.Null(await jobs.GetAsync("done-1", CancellationToken.None));
        Assert.Null(await jobs.GetAsync("failed-1", CancellationToken.None));
        Assert.NotNull(await jobs.GetAsync("queued-1", CancellationToken.None));
        Assert.NotNull(await jobs.GetAsync("fetching-1", CancellationToken.None));
        Assert.NotNull(await jobs.GetAsync("paused-1", CancellationToken.None));
    }

    [Fact]
    public async Task No_terminal_jobs_means_no_op()
    {
        var jobs = new FakeJobRepo();
        jobs.Seed(MakeJob("queued-1", JobLifecycleState.Queued));

        var svc = new JobHistoryArchiveService(jobs, TimeProvider.System,
            NullLogger<JobHistoryArchiveService>.Instance);

        await svc.SweepOnceAsync(CancellationToken.None);

        // Nothing should have changed.
        Assert.NotNull(await jobs.GetAsync("queued-1", CancellationToken.None));
    }

    private static JobRecord MakeJob(string id, JobLifecycleState state)
        => new(
            Id: id,
            Protocol: DownloadProtocol.Nzb,
            DisplayName: $"job-{id}",
            SourcePath: "",
            SourceKind: "nzb",
            Category: null,
            Priority: 0,
            State: state,
            StateReason: null,
            Paused: false,
            PasswordProtected: null,
            DownloadDir: "",
            OutputDir: null,
            TotalBytes: 1_000_000,
            DownloadedBytes: 1_000_000,
            UploadedBytes: 0,
            DispatchId: null,
            LibraryId: null,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: state is JobLifecycleState.Done or JobLifecycleState.Failed
                ? DateTimeOffset.UtcNow
                : null);
}
