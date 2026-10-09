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
            var rejected = new List<string>();
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

                // Parsed and checked before it goes anywhere near the data folder (#1064):
                // a definition that will not parse, will not run, names another id or
                // points at something that is not a web address never replaces a good one.
                if (Reject(definition.Id, definition.Yaml) is { } reason)
                {
                    logger.LogWarning("Tracker definition {Id} from the remote source was not used: {Reason}", definition.Id, reason);
                    rejected.Add(definition.Id);
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

                // Written beside, then moved into place, so an interrupted write never leaves half a file.
                var partial = path + ".tmp";
                await File.WriteAllTextAsync(partial, definition.Yaml, cancellationToken);
                File.Move(partial, path, overwrite: true);
                (isNew ? added : changed).Add(definition.Id);
            }

            var updated = added.Concat(changed).ToList();

            // Said once per set of rejected files, not on every run: a definition Deluno
            // cannot run stays rejected, and repeating the line each pass is noise.
            rejected.Sort(StringComparer.OrdinalIgnoreCase);
            var rejectedMarker = Path.Combine(directory, "definitions-rejected.txt");
            var rejectedKey = string.Join("\n", rejected);
            var previousRejectedKey = File.Exists(rejectedMarker) ? await File.ReadAllTextAsync(rejectedMarker, cancellationToken) : string.Empty;
            if (rejected.Count == 0)
            {
                if (File.Exists(rejectedMarker))
                {
                    File.Delete(rejectedMarker);
                }
            }
            else if (!string.Equals(previousRejectedKey, rejectedKey, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(rejectedMarker, rejectedKey, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "trackers.definitions.rejected",
                    $"Deluno kept its current copy of {rejected.Count} tracker definition{(rejected.Count == 1 ? "" : "s")} ({string.Join(", ", rejected)}) because the downloaded file was not valid.",
                    JsonSerializer.Serialize(new { rejectedTrackerIds = rejected }),
                    null,
                    "trackers",
                    null,
                    cancellationToken);
            }

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
        catch (Deluno.Infrastructure.Http.GitHubRateLimitException limit)
        {
            // GitHub's hourly allowance is used up (#1181): not an unreachable source. Say so, and when it lifts.
            logger.LogWarning("Tracker definition update stopped by GitHub's limit: {Message}", limit.Message);
            return await store.SaveFailureAsync(limit.Message, checkedUtc, cancellationToken);
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

    /// <summary>The biggest definition file Deluno will take from the remote source.</summary>
    internal const int MaxDefinitionBytes = 1024 * 1024;

    /// <summary>Why a downloaded definition must not be used, or null when it is sound (#1064).</summary>
    internal static string? Reject(string id, string yaml)
    {
        if (yaml.Length > MaxDefinitionBytes)
        {
            return "It is larger than a definition should be.";
        }

        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('.'))
        {
            return "Its id is not a plain name.";
        }

        TrackerDefinition parsed;
        try
        {
            parsed = TrackerDefinition.Parse(yaml);
        }
        catch (Exception exception)
        {
            return "It could not be read: " + exception.Message;
        }

        if (!string.Equals(parsed.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return "Its own id does not match the name it was published under.";
        }

        if (parsed.Links.Count == 0 && parsed.LegacyLinks.Count == 0)
        {
            return "It has no site address.";
        }

        foreach (var link in parsed.Links.Concat(parsed.LegacyLinks))
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return $"Its address '{link}' is not a web address.";
            }
        }

        return TrackerDefinitionValidator.Unsupported(parsed);
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
