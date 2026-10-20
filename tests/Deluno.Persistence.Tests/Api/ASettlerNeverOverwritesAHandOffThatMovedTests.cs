using System.Reflection;
using System.Runtime.ExceptionServices;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1363: a periodic pass that reads a hand-off and writes it a moment later must not overwrite a newer status that
/// landed between its read and its write (a processor's report, a retry). The watcher already writes only over the status
/// it read; the pass that closes handled problems has to do the same.
/// </summary>
public sealed class ASettlerNeverOverwritesAHandOffThatMovedTests
{
    private const string Release = "Moved.Between.2021.1080p.WEB-DL-GOLDEN";
    private const string Client = "client-1";
    private const string Hash = "hash-moved";

    /// <summary>Runs <c>afterList</c> right after the settler lists hand-offs, i.e. between its read and its write.</summary>
    private class Interposed<T> : DispatchProxy where T : class
    {
        public T Inner { get; set; } = default!;

        private bool Done;

        public Func<Task> AfterList { get; set; } = () => Task.CompletedTask;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result;
            try
            {
                result = targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
                throw;
            }

            if (targetMethod.Name == nameof(IProcessorRepository.ListProcessorHandoffsWithStatusAsync) && result is Task<IReadOnlyList<ProcessorHandoffItem>> listing)
            {
                return Wrap(listing);
            }

            return result;
        }

        private async Task<IReadOnlyList<ProcessorHandoffItem>> Wrap(Task<IReadOnlyList<ProcessorHandoffItem>> listing)
        {
            var read = await listing;
            if (!Done)
            {
                Done = true;
                await AfterList();
            }

            return read;
        }
    }

    private static IProcessorRepository Moving(IProcessorRepository inner, Func<Task> afterList)
    {
        var proxy = DispatchProxy.Create<IProcessorRepository, Interposed<IProcessorRepository>>();
        var forwarding = (Interposed<IProcessorRepository>)(object)proxy;
        forwarding.Inner = inner;
        forwarding.AfterList = afterList;
        return proxy;
    }

    private static async Task<ProcessorHandoffItem> GoneHandoffAsync(ApplicationTestHost app)
    {
        var dispatches = app.Services.GetRequiredService<IJobQueueRepository>();
        var dispatchId = await dispatches.RecordDownloadDispatchAsync(
            "library-1", "movies", "movie", "movie-1", Release, "Fixture Indexer", Client, "Transmission", "sent", null,
            clientExternalId: Hash, cancellationToken: CancellationToken.None);
        // Deluno had already closed that download, so a source-gone report about it settles by itself.
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().ArchiveDispatchAsync(dispatchId, "closed", CancellationToken.None);

        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var created = await processors.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "movies", Client, Hash, Release, $"/downloads/{Release}", "Weir"),
            CancellationToken.None);
        var gone = await processors.UpdateProcessorHandoffAsync(created.Id, ProcessorHandoffStatuses.SourceGone, null, null, "The download was gone before Weir took it.", CancellationToken.None);
        Assert.NotNull(gone);
        return gone;
    }

    private static Task<int> SettleAsync(ApplicationTestHost app, IProcessorRepository processors)
        => new HandledProblemsSettler(
                app.Services.GetRequiredService<IMovieCatalogRepository>(),
                app.Services.GetRequiredService<ISeriesCatalogRepository>(),
                app.Services.GetRequiredService<IJobQueueRepository>(),
                app.Services.GetService<IDownloadDispatchesRepository>(),
                app.Services.GetRequiredService<IActivityFeedRepository>(),
                processors)
            .SettleAsync(CancellationToken.None);

    [Fact]
    public async Task A_source_gone_report_that_moved_on_after_the_read_is_not_dismissed_over_the_newer_status()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await GoneHandoffAsync(app);
        var real = app.Services.GetRequiredService<IProcessorRepository>();
        var moved = false;
        var processors = Moving(real, async () =>
        {
            moved = true;
            await real.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, CancellationToken.None);
        });

        var settled = await SettleAsync(app, processors);

        Assert.True(moved, "the newer status landed between the settler's read and its write");
        Assert.Equal(ProcessorHandoffStatuses.Waiting, (await real.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task A_source_gone_report_nothing_moved_is_still_dismissed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await GoneHandoffAsync(app);
        var real = app.Services.GetRequiredService<IProcessorRepository>();

        var settled = await SettleAsync(app, Moving(real, () => Task.CompletedTask));

        Assert.Equal(1, settled);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await real.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }
}
