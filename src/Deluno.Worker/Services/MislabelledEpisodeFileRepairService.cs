using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// #1173: search and import now refuse a release whose name says one episode and carries another's title, but a
/// file that was imported before that stays. Once at start-up and every few hours after, any episode whose file came
/// from such a release (the grab's release name, or the file's own name) has the file moved to the recycle bin
/// (Restore works), is Missing again, is searched at once, and says so in Activity. The test is
/// <see cref="ReleaseTitleMatch.MislabelledAs"/>, the rule search uses, so there is one. A file whose name carries its
/// own title, or none, is never touched, and a second run finds nothing to do. It moves a person's file, so while automatic
/// jobs are off it does nothing and says so once in Activity (#1321).
/// </summary>
[ChangesMediaOnlyWhenAutomaticJobsAreOn]
public sealed class MislabelledEpisodeFileRepairService(
    IServiceScopeFactory scopes,
    ILogger<MislabelledEpisodeFileRepairService> logger) : BackgroundService
{
    public const string ActivityCategory = "series.episode-file.mislabelled";
    internal const string ServiceKey = "mislabelled-episode-file-repair";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var services = scope.ServiceProvider;
                if (!await AutomaticJobs.IsOnAsync(services.GetRequiredService<IPlatformSettingsRepository>(), stoppingToken))
                {
                    await services.GetRequiredService<IActivityFeedRepository>().RecordActivityOnChangeAsync(
                        AutomaticJobs.PausedActivityCategory,
                        AutomaticJobs.PausedMessage("checking episode files against the names of the downloads they came from"),
                        null, null, "service", ServiceKey, stoppingToken);
                    continue;
                }

                await RunAsync(
                    services.GetRequiredService<ISeriesCatalogRepository>(),
                    services.GetRequiredService<ILibrariesRepository>(),
                    services.GetRequiredService<IRecycleBinService>(),
                    services.GetRequiredService<IJobQueueRepository>(),
                    services.GetRequiredService<IJobScheduler>(),
                    services.GetRequiredService<IActivityFeedRepository>(),
                    stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not check episode files against their release names; they are left as they are.");
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

    /// <summary>Returns how many wrong files were moved to the recycle bin.</summary>
    public static async Task<int> RunAsync(
        ISeriesCatalogRepository series,
        ILibrariesRepository libraries,
        IRecycleBinService recycleBin,
        IJobQueueRepository dispatches,
        IJobScheduler scheduler,
        IActivityFeedRepository activity,
        CancellationToken cancellationToken)
    {
        await HealShowFileNotesAsync(series, recycleBin, cancellationToken);

        var links = await series.ListEpisodeFileLinksAsync(cancellationToken);
        if (links.Count == 0)
        {
            return 0;
        }

        var libraryById = (await libraries.ListLibrariesAsync(cancellationToken))
            .ToDictionary(library => library.Id, StringComparer.OrdinalIgnoreCase);
        var repaired = 0;
        foreach (var bySeries in links.GroupBy(link => link.SeriesId))
        {
            var inventory = await series.GetInventoryDetailAsync(bySeries.Key, cancellationToken);
            if (inventory is null)
            {
                continue;
            }

            foreach (var link in bySeries)
            {
                var titles = inventory.EpisodeTitlesForSeason(link.SeasonNumber);
                if (titles.Count < 2 || !libraryById.TryGetValue(link.LibraryId, out var library) || !File.Exists(link.FilePath))
                {
                    continue;
                }

                var expected = new ReleaseTitleExpectation(
                    link.SeriesTitle, inventory.StartYear, IsTv: true, link.SeasonNumber, link.EpisodeNumber, EpisodeTitles: titles);
                var wrongName = await MislabelledNameAsync(dispatches, activity, link, expected, cancellationToken);
                if (wrongName is null)
                {
                    continue;
                }

                var (otherEpisode, otherTitle) = wrongName.Value;
                // #1189: the owner is recorded so a restore links the episode again.
                if (await recycleBin.StoreRemovedFileAsync(
                        library,
                        link.FilePath,
                        new RecycleBinFileOwner("tv", "series", link.SeriesId, link.SeriesTitle, inventory.StartYear, link.EpisodeId, true),
                        cancellationToken) is null)
                {
                    continue;
                }

                await series.ClearMislabelledEpisodeFileAsync(link.EpisodeId, link.FilePath, cancellationToken);

                var label = $"{link.SeriesTitle} S{link.SeasonNumber:00}E{link.EpisodeNumber:00}";
                await activity.RecordActivityAsync(
                    ActivityCategory,
                    $"{label} held a copy of S{link.SeasonNumber:00}E{otherEpisode:00} (its name carries '{otherTitle}'). " +
                    "It went to the recycle bin and Deluno is searching for the right one.",
                    null, null, "episode", link.EpisodeId, cancellationToken);

                await scheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        LibrarySearchJobTypes.For("tv"),
                        "mislabelled-file",
                        JsonSerializer.Serialize(new
                        {
                            libraryId = library.Id,
                            libraryName = library.Name,
                            mediaType = "tv",
                            checkMissing = true,
                            checkUpgrades = true,
                            maxItems = 1,
                            retryDelayHours = 24,
                            triggeredBy = "mislabelled-file",
                            targetEntityId = link.EpisodeId
                        }),
                        "episode",
                        link.EpisodeId,
                        IdempotencyKey: $"mislabelled-file:{link.EpisodeId}:{Path.GetFileName(link.FilePath)}",
                        DedupeKey: $"mislabelled-file:{link.EpisodeId}"),
                    cancellationToken);
                repaired++;
            }
        }

        return repaired;
    }

    /// <summary>
    /// A show-level file note that still names a file Deluno itself moved to its recycle bin (an older repair left
    /// those) is corrected, so the library check never reads it as a file removed outside Deluno. Safe to repeat.
    /// </summary>
    public static async Task<int> HealShowFileNotesAsync(
        ISeriesCatalogRepository series,
        IRecycleBinService recycleBin,
        CancellationToken cancellationToken)
    {
        var healed = await series.ClearSeriesFileRecordsInRecycleBinAsync(cancellationToken);
        foreach (var item in await recycleBin.ListAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(item.OriginalPath) && !File.Exists(item.OriginalPath))
            {
                healed += await series.DetachSeriesFileRecordsAsync(null, item.OriginalPath, cancellationToken);
            }
        }

        return healed;
    }

    /// <summary>
    /// The name this episode's file came from, when it carries another episode's title: the release name of the
    /// latest grab imported to this path (an older import the file replaced is never used, #1189), then the library
    /// file's own name. When two imports to the path cannot be told apart by time and name different releases, which
    /// one produced the file is unknown: the file is left alone and Activity says so.
    /// </summary>
    private static async Task<(int Episode, string Title)?> MislabelledNameAsync(
        IJobQueueRepository dispatches,
        IActivityFeedRepository activity,
        EpisodeFileLink link,
        ReleaseTitleExpectation expected,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var imports = (await ImportsThatCouldHaveProducedAsync(dispatches, link, cancellationToken))
            .OrderByDescending(dispatch => dispatch.ImportCompletedUtc ?? dispatch.CreatedUtc)
            .ThenByDescending(dispatch => dispatch.CreatedUtc)
            .ToArray();
        if (imports.Length > 0)
        {
            var label = $"{link.SeriesTitle} S{link.SeasonNumber:00}E{link.EpisodeNumber:00}";

            // #1189: the latest import recorded the size of the file it placed. A file of another size is not that
            // file (it was replaced, or restored from an older copy), so the record cannot say what it is.
            if (imports[0].ImportedFileBytes is { } recordedBytes && FileSizeOrNull(link.FilePath) is { } actualBytes && actualBytes != recordedBytes)
            {
                await activity.RecordActivityOnChangeAsync(
                    ActivityCategory,
                    $"{label} has a file that is not the size of the one its latest download placed, so Deluno cannot tell what it is and left it where it is.",
                    null, null, "episode", link.EpisodeId, cancellationToken);
                return null;
            }

            var latestAt = imports[0].ImportCompletedUtc ?? imports[0].CreatedUtc;
            var sameMoment = imports
                .TakeWhile(dispatch => (dispatch.ImportCompletedUtc ?? dispatch.CreatedUtc) == latestAt)
                .Select(dispatch => dispatch.ReleaseName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sameMoment.Length > 1)
            {
                await activity.RecordActivityOnChangeAsync(
                    ActivityCategory,
                    $"{label} has a file, but Deluno cannot tell which of {sameMoment.Length} downloads produced it, so it left the file where it is.",
                    null, null, "episode", link.EpisodeId, cancellationToken);
                return null;
            }

            names.Add(imports[0].ReleaseName);
        }

        names.Add(Path.GetFileName(link.FilePath));
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (ReleaseTitleMatch.MislabelledAs(name, expected) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Every imported download that could have produced the file at this path (#1189): the grabs recorded under the
    /// episode, and the season-pack grabs recorded under the series, whose import path is the folder the pack was
    /// placed in. A series-level import counts only when its path is this file or a folder holding it; an
    /// episode-level import with no path recorded still counts, as before.
    /// </summary>
    private static async Task<IReadOnlyList<DownloadDispatchItem>> ImportsThatCouldHaveProducedAsync(
        IJobQueueRepository dispatches,
        EpisodeFileLink link,
        CancellationToken cancellationToken)
    {
        var byEpisode = await dispatches.ListDownloadDispatchesPageAsync(
            new PageRequest(50), "tv", cancellationToken, includeArchived: true, entityId: link.EpisodeId);
        var bySeries = await dispatches.ListDownloadDispatchesPageAsync(
            new PageRequest(100), "tv", cancellationToken, includeArchived: true, entityId: link.SeriesId);
        return byEpisode.Items
            .Where(dispatch => IsImported(dispatch)
                && (string.IsNullOrWhiteSpace(dispatch.ImportedFilePath) || CoversFile(dispatch.ImportedFilePath, link.FilePath)))
            .Concat(bySeries.Items.Where(dispatch => IsImported(dispatch)
                && !string.IsNullOrWhiteSpace(dispatch.ImportedFilePath)
                && CoversFile(dispatch.ImportedFilePath, link.FilePath)))
            .DistinctBy(dispatch => dispatch.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsImported(DownloadDispatchItem dispatch)
        => string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase);

    /// <summary>The import's path is the file itself, or a folder the file is inside (a season pack).</summary>
    private static bool CoversFile(string importedPath, string filePath)
    {
        var imported = importedPath.Trim().TrimEnd('/', '\\');
        if (imported.Length == 0)
        {
            return false;
        }

        if (string.Equals(imported, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filePath.Length > imported.Length
            && filePath.StartsWith(imported, StringComparison.OrdinalIgnoreCase)
            && (filePath[imported.Length] == '/' || filePath[imported.Length] == '\\');
    }

    private static long? FileSizeOrNull(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }
}
