using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Deluno.Jobs.Data;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// #1174: an import once linked one file to a second, unrelated episode (a stray "5.1" in the
/// release name read as episode 1). Once at start-up, any library file linked to several
/// episodes where its name gives one is kept on the right episode, the wrong link is cleared (and the
/// episode's own file relinked when the show's folder holds it, #1174),
/// and each clearing is written to Activity in plain words. Running it again changes nothing.
/// It changes library records, so while automatic jobs are off it does nothing, says so once in Activity, and runs
/// its one pass when they are switched on (#1321).
/// </summary>
[ChangesMediaOnlyWhenAutomaticJobsAreOn]
public sealed class EpisodeFileLinkRepairService(
    IServiceScopeFactory scopes,
    ILogger<EpisodeFileLinkRepairService> logger) : BackgroundService
{
    public const string ActivityCategory = "series.episode-file-link.repaired";
    internal const string ServiceKey = "episode-file-link-repair";

    /// <summary>How often a service waiting for automatic jobs to be switched on looks again.</summary>
    public TimeSpan WaitForSwitch { get; init; } = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WaitForSwitch);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var activity = scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>();
                if (!await AutomaticJobs.IsOnAsync(scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>(), stoppingToken))
                {
                    await activity.RecordActivityOnChangeAsync(
                        AutomaticJobs.PausedActivityCategory,
                        AutomaticJobs.PausedMessage("checking that each episode file is linked to the right episode"),
                        null, null, "service", ServiceKey, stoppingToken);
                    continue;
                }

                await RunAsync(scope.ServiceProvider.GetRequiredService<ISeriesCatalogRepository>(), activity, stoppingToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not check episode file links; they are left as they are.");
                return;
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Returns how many wrong links were cleared.</summary>
    public static async Task<int> RunAsync(
        ISeriesCatalogRepository series,
        IActivityFeedRepository activity,
        CancellationToken cancellationToken)
    {
        var repairs = await series.RepairWrongEpisodeFileLinksAsync(cancellationToken);
        foreach (var repair in repairs)
        {
            var season = $"{repair.SeriesTitle} S{repair.ClearedSeasonNumber:00}E{repair.ClearedEpisodeNumber:00}";
            var message = repair.RelinkedFilePath is { } own
                ? $"{season} was showing a file that belongs to {repair.KeptEpisodes}. The wrong link was removed and it now shows its own file, {Path.GetFileName(own)}."
                : $"{season} was showing a file that belongs to {repair.KeptEpisodes}. The wrong link was removed, so it is Missing again.";
            await activity.RecordActivityAsync(
                ActivityCategory,
                message,
                null, null, "episode", repair.EpisodeId, cancellationToken);
        }

        return repairs.Count;
    }
}
