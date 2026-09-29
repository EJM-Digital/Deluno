using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The scheduled and manual halves of #756: fetches tracker definitions from
/// <see cref="IRemoteTrackerDefinitionSource"/> and writes any that changed
/// into the data folder's <c>trackers</c> directory -- the exact override
/// path <see cref="TrackerDefinitionCatalog"/> already treats a hand-dropped
/// YAML file as, so this only automates dropping it there and reloading the
/// catalog. A local setup problem or an outage on the remote source is never
/// treated as anything worse than a failed check that tries again on its own
/// schedule (#880, #901): nothing here can take a tracker away.
/// </summary>
public sealed class TrackerDefinitionUpdateService(
    ITrackerDefinitionUpdateStore store,
    IRemoteTrackerDefinitionSource source,
    ITrackerDefinitionCatalog catalog,
    IOptions<StoragePathOptions> storage,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<TrackerDefinitionUpdateService> logger) : ITrackerDefinitionUpdateService
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Bundled = new(() =>
        TrackerDefinitionCatalog.Bundled().ToDictionary(item => item.Id, item => item.Yaml, StringComparer.OrdinalIgnoreCase));

    private static readonly Lazy<IReadOnlySet<string>> KeptOwn = new(() =>
        TrackerDefinitionCatalog.BuiltIn().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static bool Same(string left, string right)
        => string.Equals(left.ReplaceLineEndings("\n").Trim(), right.ReplaceLineEndings("\n").Trim(), StringComparison.Ordinal);

    public Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken)
        => store.GetAsync(cancellationToken);

    public Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken)
        => store.SetSettingsAsync(isEnabled, intervalHours, cancellationToken);

    public Task<TrackerDefinitionUpdateState> UpdateNowAsync(CancellationToken cancellationToken)
        // Explicit, owner-requested: runs regardless of the scheduled on/off
        // switch, the same way a "Test" or "Check now" button elsewhere is
        // never gated by the automatic pass's own switch.
        => RunAsync(cancellationToken);

    public async Task<TrackerDefinitionUpdateState> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(cancellationToken);
        if (!state.IsEnabled)
        {
            return state;
        }

        if (state.Status != TrackerDefinitionUpdateStatuses.Failed
            && state.LastCheckedUtc is { } lastChecked
            && timeProvider.GetUtcNow() - lastChecked < TimeSpan.FromHours(Math.Max(1, state.IntervalHours)))
        {
            return state;
        }

        return await RunAsync(cancellationToken);
    }

    private async Task<TrackerDefinitionUpdateState> RunAsync(CancellationToken cancellationToken)
    {
        var checkedUtc = timeProvider.GetUtcNow();
        try
        {
            var remote = await source.FetchAsync(cancellationToken);
            var directory = Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "trackers");
            Directory.CreateDirectory(directory);

            // Named apart (#754/#756: "naming what changed" means saying which
            // definitions were added versus which were already there and only
            // changed) -- a brand-new tracker landing is a different fact from
            // an existing one's rules being patched, even though both are
            // "updated" as far as the state this job persists is concerned.
            var added = new List<string>();
            var changed = new List<string>();
            foreach (var definition in remote)
            {
                if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Yaml))
                {
                    continue;
                }

                // A site Deluno keeps its own definition for is not replaced by the repository's.
                if (KeptOwn.Value.Contains(definition.Id))
                {
                    continue;
                }

                var path = Path.Combine(directory, $"{definition.Id}.yml");
                var isNew = !File.Exists(path);
                var existing = isNew ? null : await File.ReadAllTextAsync(path, cancellationToken);
                if (string.Equals(existing, definition.Yaml, StringComparison.Ordinal))
                {
                    continue;
                }

                // A copy already in the built-in snapshot needs no second copy on disk.
                if (isNew && Bundled.Value.TryGetValue(definition.Id, out var bundled) && Same(bundled, definition.Yaml))
                {
                    continue;
                }

                await File.WriteAllTextAsync(path, definition.Yaml, cancellationToken);
                (isNew ? added : changed).Add(definition.Id);
            }

            var updated = added.Concat(changed).ToList();

            if (updated.Count > 0)
            {
                // The catalog's own Lazy load never sees this write on its
                // own -- reload it now so a fresh definition is offered
                // without restarting Deluno.
                catalog.Reload();

                added.Sort(StringComparer.OrdinalIgnoreCase);
                changed.Sort(StringComparer.OrdinalIgnoreCase);
                updated.Sort(StringComparer.OrdinalIgnoreCase);
                await activityFeedRepository.RecordActivityAsync(
                    "trackers.definitions.update",
                    DescribeChange(added, changed),
                    JsonSerializer.Serialize(new { addedTrackerIds = added, updatedTrackerIds = changed }),
                    null,
                    "trackers",
                    null,
                    cancellationToken);
            }

            return await store.SaveSuccessAsync(checkedUtc, updated, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tracker definition update could not complete.");
            return await store.SaveFailureAsync(
                "Deluno could not reach the tracker definitions source. It will try again on its own schedule.",
                checkedUtc,
                cancellationToken);
        }
    }

    /// <summary>The Activity sentence naming which definitions were added and which were only changed (#754/#756).</summary>
    private static string DescribeChange(IReadOnlyList<string> added, IReadOnlyList<string> changed)
    {
        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add(added.Count == 1
                ? $"added the {added[0]} tracker definition"
                : $"added {added.Count} tracker definitions ({string.Join(", ", added)})");
        }

        if (changed.Count > 0)
        {
            parts.Add(changed.Count == 1
                ? $"updated the {changed[0]} tracker definition"
                : $"updated {changed.Count} tracker definitions ({string.Join(", ", changed)})");
        }

        return $"Deluno {string.Join(" and ", parts)} from its remote source.";
    }
}
