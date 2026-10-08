using Deluno.Contracts;
using Deluno.Worker.Dashboard;

namespace Deluno.Tray;

/// <summary>
/// The tray's reading of the bell's "things need a look". The bell adds its count up in the browser from lists the
/// browser fetches; the tray has no browser, so it adds up the server's own counts of the same kinds of thing
/// (stuck imports, a processor handoff that failed, a stalled download, a client, indexer or processor that isn't
/// answering, jobs that gave up, no internet) plus Health's problems that no row already covers.
/// </summary>
public sealed record TrayAttentionInputs(
    int RecoveryOpen = 0,
    int FailedImportJobs = 0,
    int FailedProcessorHandoffs = 0,
    int Stalled = 0,
    int ClientsNeedingAction = 0,
    int IndexersFailing = 0,
    int ProcessorsFailing = 0,
    int BackgroundJobsGaveUp = 0,
    int LanesFailing = 0,
    int CollectionsFailing = 0,
    int RequestServersFailing = 0,
    bool InternetOutage = false,
    int HealthProblemsNotOtherwiseCounted = 0);

public sealed record TrayAttention(int Count, bool HealthProblem)
{
    public static TrayAttention None { get; } = new(0, false);

    /// <summary>A health problem about a connection already has its own row, so it is that row and not a second one.</summary>
    private static readonly string[] CoveredHealthPrefixes = ["indexer:", "download-client:", "processor:"];

    public static int Total(TrayAttentionInputs i)
        => i.RecoveryOpen + i.FailedImportJobs + i.FailedProcessorHandoffs + i.Stalled + i.ClientsNeedingAction
           + i.IndexersFailing + i.ProcessorsFailing + i.BackgroundJobsGaveUp + i.LanesFailing + i.CollectionsFailing
           + i.RequestServersFailing + (i.InternetOutage ? 1 : 0) + i.HealthProblemsNotOtherwiseCounted;

    public static TrayAttentionInputs InputsFrom(DashboardSummary? summary, IReadOnlyList<HealthCheckResultItem> health)
    {
        var imports = summary?.Imports;
        var system = summary?.System;
        var failedImports = imports?.FailedImportJobs ?? 0;
        var notOtherwiseCounted = health.Count(check =>
            check.Status == HealthCheckStatuses.Problem
            && !CoveredHealthPrefixes.Any(prefix => check.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        return new TrayAttentionInputs(
            RecoveryOpen: imports?.RecoveryOpen ?? 0,
            FailedImportJobs: failedImports,
            FailedProcessorHandoffs: imports?.FailedProcessorHandoffs ?? 0,
            Stalled: summary?.Downloads?.Stalled ?? 0,
            ClientsNeedingAction: summary?.Downloads?.Clients.Count(client => !client.Reachable || client.Paused == true || client.Warnings > 0) ?? 0,
            IndexersFailing: summary?.Indexers?.Failing ?? 0,
            ProcessorsFailing: summary?.Processors?.Failing ?? 0,
            BackgroundJobsGaveUp: Math.Max(0, (system?.Jobs.Failed ?? 0) - failedImports),
            LanesFailing: system?.Lanes.Count(lane => lane.State == "failing") ?? 0,
            CollectionsFailing: summary?.Library?.Collections.SyncFailing ?? 0,
            RequestServersFailing: summary?.Requests?.SeerrFailing ?? 0,
            InternetOutage: system?.InternetOutage ?? false,
            HealthProblemsNotOtherwiseCounted: notOtherwiseCounted);
    }

    /// <summary>The count, and whether Health has a problem that is not snoozed (a snoozed one is not counted, as on the Dashboard).</summary>
    public static TrayAttention From(DashboardSummary? summary, IReadOnlyList<HealthCheckResultItem> health, DateTimeOffset now)
    {
        var unsnoozed = health.Where(check => check.Status == HealthCheckStatuses.Problem && !check.IsSnoozed(now)).ToList();
        var inputs = InputsFrom(summary, unsnoozed);
        return new TrayAttention(Total(inputs), unsnoozed.Count > 0);
    }
}
