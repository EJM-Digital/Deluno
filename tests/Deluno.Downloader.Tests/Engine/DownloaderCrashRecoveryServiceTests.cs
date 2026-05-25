using Deluno.Downloader.Engine;
using Deluno.Downloader.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Downloader.Tests.Engine;

public class DownloaderCrashRecoveryServiceTests
{
    [Fact]
    public async Task Re_queues_every_mid_flight_state()
    {
        var jobs = new FakeJobRepo();
        // Seed one job for every state that should be re-queued.
        var midFlightStates = new[]
        {
            JobLifecycleState.Fetching, JobLifecycleState.Reassembled,
            JobLifecycleState.Verify, JobLifecycleState.Verified, JobLifecycleState.Repair,
            JobLifecycleState.Extracting, JobLifecycleState.Extracted,
            JobLifecycleState.PostProcessed, JobLifecycleState.ImportPending,
            JobLifecycleState.Seeding,
        };
        foreach (var s in midFlightStates)
        {
            jobs.Seed(MakeJob(Guid.NewGuid().ToString("N"), s,
                s == JobLifecycleState.Seeding ? DownloadProtocol.Torrent : DownloadProtocol.Nzb));
        }
        // Plus one Queued, one Paused, one Done, one Failed — none should be touched.
        var queuedId = "queued";
        var pausedId = "paused";
        var doneId = "done";
        var failedId = "failed";
        jobs.Seed(MakeJob(queuedId, JobLifecycleState.Queued));
        jobs.Seed(MakeJob(pausedId, JobLifecycleState.Paused));
        jobs.Seed(MakeJob(doneId, JobLifecycleState.Done));
        jobs.Seed(MakeJob(failedId, JobLifecycleState.Failed));

        var svc = new DownloaderCrashRecoveryService(jobs, TimeProvider.System,
            NullLogger<DownloaderCrashRecoveryService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        // Every mid-flight job is now Queued.
        foreach (var s in midFlightStates)
        {
            var matching = jobs.AllJobs.Where(j => j.StateReason?.Contains("Recovered from crash") == true).ToList();
            Assert.NotEmpty(matching);
        }
        var transitioned = jobs.AllJobs.Where(j => j.StateReason?.Contains("Recovered from crash") == true).ToList();
        Assert.Equal(midFlightStates.Length, transitioned.Count);
        Assert.All(transitioned, j => Assert.Equal(JobLifecycleState.Queued, j.State));

        // Untouched: queued (still queued, but no recovery reason), paused, done, failed.
        Assert.Equal(JobLifecycleState.Queued, jobs.AllJobs.Single(j => j.Id == queuedId).State);
        Assert.NotEqual("Recovered from crash", jobs.AllJobs.Single(j => j.Id == queuedId).StateReason);
        Assert.Equal(JobLifecycleState.Paused, jobs.AllJobs.Single(j => j.Id == pausedId).State);
        Assert.Equal(JobLifecycleState.Done, jobs.AllJobs.Single(j => j.Id == doneId).State);
        Assert.Equal(JobLifecycleState.Failed, jobs.AllJobs.Single(j => j.Id == failedId).State);
    }

    [Fact]
    public async Task No_jobs_means_no_op()
    {
        var jobs = new FakeJobRepo();
        var svc = new DownloaderCrashRecoveryService(jobs, TimeProvider.System,
            NullLogger<DownloaderCrashRecoveryService>.Instance);
        // Should not throw with an empty store.
        await svc.StartAsync(CancellationToken.None);
        Assert.Empty(jobs.AllJobs);
    }

    private static JobRecord MakeJob(string id, JobLifecycleState state, DownloadProtocol protocol = DownloadProtocol.Nzb)
        => new(
            Id: id,
            Protocol: protocol,
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
            TotalBytes: 0,
            DownloadedBytes: 0,
            UploadedBytes: 0,
            DispatchId: null,
            LibraryId: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null);
}

/// <summary>In-memory IJobRepository for unit tests.</summary>
internal sealed class FakeJobRepo : IJobRepository
{
    private readonly Dictionary<string, JobRecord> _store = new();
    public IReadOnlyCollection<JobRecord> AllJobs => _store.Values;

    public void Seed(JobRecord j) => _store[j.Id] = j;

    public Task<JobRecord?> GetAsync(string id, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue(id, out var j) ? j : null);

    public Task<IReadOnlyList<JobRecord>> ListByStateAsync(
        IReadOnlyList<JobLifecycleState> states, int limit, CancellationToken ct)
    {
        var set = states.ToHashSet();
        IReadOnlyList<JobRecord> result =
            _store.Values.Where(j => set.Contains(j.State)).Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<JobRecord>> ListPriorityOrderedAsync(
        JobLifecycleState state, int limit, CancellationToken ct)
    {
        IReadOnlyList<JobRecord> result =
            _store.Values.Where(j => j.State == state).Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task UpsertAsync(JobRecord job, CancellationToken ct)
    {
        _store[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task TransitionAsync(string jobId, JobLifecycleState to, string? reason,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (!_store.TryGetValue(jobId, out var existing))
            throw new InvalidOperationException($"Job {jobId} not found.");
        JobLifecycleTransitions.EnsureLegal(existing.State, to, existing.Protocol);
        _store[jobId] = existing with { State = to, StateReason = reason, UpdatedAt = occurredAt };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StateTransitionRecord>> GetTransitionsAsync(string jobId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<StateTransitionRecord>>(Array.Empty<StateTransitionRecord>());

    public Task ArchiveAsync(
        string jobId, string? torrentInfohashV1Hex, string? torrentInfohashV2Hex, CancellationToken ct)
    {
        // Crash-recovery tests don't exercise archive; just remove the
        // row so the in-memory store stays consistent.
        _store.Remove(jobId);
        return Task.CompletedTask;
    }
}
