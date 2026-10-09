using Deluno.Companions;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.Attention;

/// <summary>The one place the bell and the tray ask what needs a person (#1185).</summary>
public interface IAttentionSummaryService
{
    /// <param name="fresh">Build the Dashboard summary again instead of using the one remembered for a few seconds (the tray does, once a push says something moved).</param>
    Task<AttentionSummary> GetAsync(bool fresh, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the lists the bell's count is made of and hands them to <see cref="AttentionSummaryBuilder"/> (#1185). The
/// bell's endpoint and the tray both call <see cref="GetAsync"/>, so there is one reading and one formula.
/// </summary>
public sealed class AttentionSummaryService(
    IServiceScopeFactory scopeFactory,
    DashboardSummaryService dashboard,
    TimeProvider timeProvider) : IAttentionSummaryService
{
    public async Task<AttentionSummary> GetAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (fresh)
        {
            dashboard.Invalidate();
        }

        var summary = await dashboard.GetAsync(cancellationToken).ConfigureAwait(false);
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var now = timeProvider.GetUtcNow();

        var telemetry = await sp.GetRequiredService<IDownloadClientTelemetryService>()
            .GetLatestOverviewAsync(DownloadTelemetryFreshness.Screen, cancellationToken).ConfigureAwait(false);
        var movieCases = (await sp.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(cancellationToken, 500, true).ConfigureAwait(false)).RecentCases;
        var seriesCases = (await sp.GetRequiredService<ISeriesCatalogRepository>().GetImportRecoverySummaryAsync(cancellationToken, 500, true).ConfigureAwait(false)).RecentCases;
        var jobs = sp.GetRequiredService<IJobQueueRepository>();
        var failedJobs = await ListFailedJobsAsync(jobs, cancellationToken).ConfigureAwait(false);
        var dispatches = (await jobs.ListDownloadDispatchesPageAsync(new PageRequest(60), null, cancellationToken).ConfigureAwait(false)).Items;

        var processors = sp.GetRequiredService<IProcessorRepository>();
        var handoffs = await processors.ListProcessorHandoffsAsync(null, 50, cancellationToken).ConfigureAwait(false);
        var outcomes = (await processors.ListProcessorOutcomesAsync(handoffs.Select(item => item.Id).ToArray(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.HandoffId, StringComparer.OrdinalIgnoreCase);
        handoffs = handoffs.Select(item => outcomes.TryGetValue(item.Id, out var outcome) ? item with { Outcome = outcome } : item).ToArray();

        var indexers = await sp.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(cancellationToken).ConfigureAwait(false);
        var processorConnections = await processors.ListProcessorConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var health = await sp.GetRequiredService<IHealthCheckRepository>().ListAsync(cancellationToken).ConfigureAwait(false);
        var noRelease = await AttentionEndpoints.ListNoReleaseAsync(
            sp.GetRequiredService<INoAcceptableReleaseReader>(),
            sp.GetRequiredService<AttentionDismissalStore>(),
            cancellationToken).ConfigureAwait(false);
        var updates = await sp.GetRequiredService<ConnectionUpdateService>().ListAsync(cancellationToken).ConfigureAwait(false);

        return AttentionSummaryBuilder.Build(new AttentionSources(
            summary, telemetry, movieCases, seriesCases, failedJobs, handoffs, dispatches,
            indexers, processorConnections, health, noRelease, updates, now));
    }

    private static async Task<IReadOnlyList<JobQueueItem>> ListFailedJobsAsync(IJobQueueRepository jobs, CancellationToken cancellationToken)
    {
        var all = new List<JobQueueItem>();
        string? token = null;
        do
        {
            var page = await jobs.ListPageAsync(new PageRequest(PageRequest.MaximumPageSize, token), true, cancellationToken, includePayload: true).ConfigureAwait(false);
            all.AddRange(page.Items);
            token = page.HasMore ? page.NextPageToken : null;
        }
        while (token is not null);
        return all;
    }
}
