using Deluno.Quality.Data;

namespace Deluno.Quality.Guides;

/// <summary>
/// Compares the latest public TRaSH Git tree with the exact source blobs in
/// Deluno's active package. The output is a review report only: it never
/// rewrites custom formats, package versions, profiles, or decision plans.
/// </summary>
public sealed class GuideUpdateCheckService(
    IGuideUpdateCheckStore store,
    IGuidePackageStore guidePackageStore,
    IQualityRepository qualityRepository,
    GuideUpstreamTreeClient upstreamTreeClient,
    TimeProvider timeProvider) : IGuideUpdateCheckService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

    public Task<GuideUpdateCheckState> GetAsync(CancellationToken cancellationToken)
        => store.GetAsync(cancellationToken);

    public Task<GuideUpdateCheckState> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
        => store.SetEnabledAsync(isEnabled, cancellationToken);

    public async Task<GuideUpdateCheckState> CheckNowAsync(CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(cancellationToken);
        if (!state.IsEnabled)
        {
            // A disabled setting is a privacy boundary. The owner must turn it
            // on before a network request can occur, even for a button click.
            return state;
        }

        return await CheckAsync(cancellationToken);
    }

    public async Task<GuideUpdateCheckState> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var state = await store.GetAsync(cancellationToken);
        if (!state.IsEnabled
            || (state.Status != GuideUpdateCheckStatuses.Failed
                && state.LastCheckedUtc >= timeProvider.GetUtcNow() - CheckInterval))
        {
            return state;
        }

        return await CheckAsync(cancellationToken);
    }

    private async Task<GuideUpdateCheckState> CheckAsync(CancellationToken cancellationToken)
    {
        var checkedUtc = timeProvider.GetUtcNow();
        try
        {
            var storedPackage = await guidePackageStore.GetCurrentAsync(cancellationToken);
            var inventory = storedPackage.Package.SourceInventory
                ?? throw new InvalidOperationException("The active guide package does not retain the pinned source inventory needed for an update check.");
            var tracked = GetTrackedSources(inventory).ToArray();
            var missingBaselineBlobs = tracked
                .Where(item => string.IsNullOrWhiteSpace(item.BlobSha))
                .Select(item => item.SourcePath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (missingBaselineBlobs.Length > 0)
            {
                throw new InvalidOperationException(
                    "The active guide package predates source blob tracking. Update the local guide package before checking it against upstream.");
            }

            var remote = await upstreamTreeClient.GetSnapshotAsync(cancellationToken);
            var usedFormats = (await qualityRepository.ListCustomFormatsAsync(cancellationToken))
                .Where(format => !string.IsNullOrWhiteSpace(format.TrashId))
                .GroupBy(format => format.TrashId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(format => format.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var report = BuildReport(inventory, tracked, remote, usedFormats, checkedUtc);
            return await store.SaveSuccessAsync(report, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await store.SaveFailureAsync(
                ExplainFailure(exception),
                checkedUtc,
                cancellationToken);
        }
    }

    private static GuideUpdateCheckReport BuildReport(
        GuideSourceInventory inventory,
        IReadOnlyList<TrackedSource> tracked,
        GuideUpstreamTreeSnapshot remote,
        IReadOnlyDictionary<string, IReadOnlyList<string>> usedFormats,
        DateTimeOffset checkedUtc)
    {
        var changes = tracked
            .Select(item => ToChange(item, remote.BlobShaByPath, usedFormats))
            .Where(change => change is not null)
            .Cast<GuideUpdateCheckChange>()
            .OrderBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.MediaType, StringComparer.Ordinal)
            .ThenBy(change => change.Name, StringComparer.Ordinal)
            .ToArray();
        var additions = FindAddedSources(tracked, remote.BlobShaByPath);
        var usedChangeCount = changes.Count(change => change.IsInUse);
        var summary = changes.Length == 0 && additions.Count == 0
            ? $"No tracked TRaSH source changes were found at {remote.Revision}."
            : $"{changes.Length} tracked source change(s) and {additions.Count} new source file(s) were found at {remote.Revision}; {usedChangeCount} changed source item(s) affect saved custom formats.";
        return new GuideUpdateCheckReport(
            inventory.UpstreamRevision,
            remote.Revision,
            checkedUtc,
            true,
            changes,
            additions,
            summary);
    }

    private static GuideUpdateCheckChange? ToChange(
        TrackedSource item,
        IReadOnlyDictionary<string, string> remoteBlobs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> usedFormats)
    {
        var changeType = !remoteBlobs.TryGetValue(item.SourcePath, out var remoteBlob)
            ? "removed"
            : string.Equals(item.BlobSha, remoteBlob, StringComparison.OrdinalIgnoreCase)
                ? null
                : "changed";
        if (changeType is null)
        {
            return null;
        }

        var inUse = item.RelatedCustomFormatTrashIds
            .Where(usedFormats.ContainsKey)
            .SelectMany(id => usedFormats[id])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return new GuideUpdateCheckChange(
            item.Kind,
            item.Id,
            item.Name,
            item.MediaType,
            item.SourcePath,
            changeType,
            inUse.Length > 0,
            inUse);
    }

    private static IReadOnlyList<GuideUpdateCheckAddedSource> FindAddedSources(
        IReadOnlyList<TrackedSource> tracked,
        IReadOnlyDictionary<string, string> remoteBlobs)
    {
        var knownPaths = tracked.Select(item => item.SourcePath).ToHashSet(StringComparer.Ordinal);
        var roots = tracked
            .GroupBy(item => DirectoryPath(item.SourcePath), StringComparer.Ordinal)
            .Select(group => new TrackedRoot(
                group.Key,
                group.First().Kind,
                group.First().MediaType))
            .OrderBy(root => root.Path.Length)
            .ToArray();
        return remoteBlobs.Keys
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !knownPaths.Contains(path))
            .Select(path => new { Path = path, Root = roots.LastOrDefault(root => path.StartsWith(root.Path + "/", StringComparison.Ordinal)) })
            .Where(item => item.Root is not null)
            .Select(item => new GuideUpdateCheckAddedSource(item.Root!.Kind, item.Root.MediaType, item.Path))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.MediaType, StringComparer.Ordinal)
            .ThenBy(item => item.SourcePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<TrackedSource> GetTrackedSources(GuideSourceInventory inventory)
    {
        foreach (var format in inventory.CustomFormats ?? [])
        {
            yield return new TrackedSource(
                "custom-format",
                format.TrashId,
                format.Name,
                format.MediaType,
                format.SourcePath,
                format.SourceBlobSha,
                [format.TrashId]);
        }

        foreach (var group in inventory.FormatGroups ?? [])
        {
            yield return new TrackedSource(
                "format-group",
                group.TrashId,
                group.Name,
                group.MediaType,
                group.SourcePath,
                group.SourceBlobSha,
                (group.CustomFormats ?? []).Select(format => format.TrashId).ToArray());
        }

        foreach (var profile in inventory.QualityProfiles ?? [])
        {
            yield return new TrackedSource(
                "quality-profile",
                profile.TrashId,
                profile.Name,
                profile.MediaType,
                profile.SourcePath,
                profile.SourceBlobSha,
                (profile.FormatAssignments ?? []).Select(assignment => assignment.TrashId).ToArray());
        }
    }

    private static string DirectoryPath(string sourcePath)
    {
        var separator = sourcePath.LastIndexOf('/');
        return separator > 0 ? sourcePath[..separator] : sourcePath;
    }

    private static string ExplainFailure(Exception exception)
        => exception is HttpRequestException or InvalidDataException or InvalidOperationException
            ? exception.Message
            : "The TRaSH Guides update check did not complete. Deluno did not change any guide package or plan.";

    private sealed record TrackedSource(
        string Kind,
        string Id,
        string Name,
        string MediaType,
        string SourcePath,
        string BlobSha,
        IReadOnlyList<string> RelatedCustomFormatTrashIds);

    private sealed record TrackedRoot(string Path, string Kind, string MediaType);
}
