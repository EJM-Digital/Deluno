using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// An existing-library import continues one slice at a time. Running a slice twice (a retried or doubled job) asks for the
/// same continuation twice; on the real job store the dedupe key (run id plus processed count) collapses it to one queued job,
/// while a slice that has got further queues its own.
/// </summary>
public sealed class TheSameSliceIsNeverQueuedTwiceTests
{
    public class Slices : DispatchProxy
    {
        public int ProcessedTotal { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IExistingLibraryImportService.RunSliceAsync))
            {
                return Task.FromResult(new LibraryImportSliceOutcome("running", 3, ProcessedTotal, true, "Imported three more."));
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }

    private static JobQueueItem Job(string runId) => new(
        "job-1", "library.import.existing", "library-import", "running", JsonSerializer.Serialize(new { RunId = runId, LibraryId = "lib" }),
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, "library", "lib", null, null, 3, null, null);

    [Fact]
    public async Task A_slice_run_twice_leaves_one_continuation_and_a_slice_that_got_further_leaves_its_own()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var import = DispatchProxy.Create<IExistingLibraryImportService, Slices>();
        var slices = (Slices)(object)import;
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        var handler = new LibraryImportExistingJobHandler(import, scheduler);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        slices.ProcessedTotal = 3;

        await handler.HandleAsync(Job("run-1"), CancellationToken.None);
        await handler.HandleAsync(Job("run-1"), CancellationToken.None);

        var queued = (await jobs.ListAsync(100, CancellationToken.None)).Where(job => job.JobType == "library.import.existing").ToArray();
        var only = Assert.Single(queued);
        Assert.Equal("library.import.existing:run-1:3", only.DedupeKey);

        slices.ProcessedTotal = 6;
        await handler.HandleAsync(Job("run-1"), CancellationToken.None);
        await handler.HandleAsync(Job("run-2"), CancellationToken.None);

        var keys = (await jobs.ListAsync(100, CancellationToken.None)).Where(job => job.JobType == "library.import.existing")
            .Select(job => job.DedupeKey).Order().ToArray();
        Assert.Equal("library.import.existing:run-1:3|library.import.existing:run-1:6|library.import.existing:run-2:6", string.Join("|", keys));
    }
}
