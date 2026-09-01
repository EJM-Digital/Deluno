namespace Deluno.Quality.Guides;

/// <summary>
/// Builds an owner-requested, immutable TRaSH source sync candidate. It never
/// rewrites local custom formats, quality profiles, scenario plans, or release
/// decisions. Existing reviewed mappings remain exactly as Deluno last
/// reviewed them; unknown upstream rules stay Advanced.
/// </summary>
public sealed class GuidePackageSyncService(
    IGuidePackageStore guidePackageStore,
    GuideUpstreamTreeClient upstreamTreeClient) : IGuidePackageSyncService
{
    public async Task<GuidePackageUpdatePreview> PreviewAsync(
        GuidePackageSyncRequest request,
        CancellationToken cancellationToken)
    {
        var current = await guidePackageStore.GetCurrentAsync(cancellationToken);
        try
        {
            var candidate = await BuildCandidateAsync(current, request, cancellationToken);
            return await guidePackageStore.PreviewAsync(
                new GuidePackageUpdateRequest(candidate, request.ExpectedCurrentIntegritySha256),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return UnavailablePreview(current, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return UnavailablePreview(current, exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return UnavailablePreview(current, exception.Message);
        }
    }

    public async Task<StoredGuidePackage> ApplyAsync(
        GuidePackageSyncRequest request,
        CancellationToken cancellationToken)
    {
        var current = await guidePackageStore.GetCurrentAsync(cancellationToken);
        var candidate = await BuildCandidateAsync(current, request, cancellationToken);
        var preview = await guidePackageStore.PreviewAsync(
            new GuidePackageUpdateRequest(candidate, request.ExpectedCurrentIntegritySha256),
            cancellationToken);
        if (!preview.CanApply)
            throw new ArgumentException(string.Join(" | ", preview.Errors.Concat(preview.Warnings)), nameof(request));
        if (string.IsNullOrWhiteSpace(request.ExpectedProposedIntegritySha256)
            || !string.Equals(
                request.ExpectedProposedIntegritySha256.Trim(),
                preview.ProposedIntegritySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The staged guide source changed after its preview. Review the package diff again before syncing.");
        }

        return await guidePackageStore.ApplyAsync(
            new GuidePackageUpdateRequest(preview.Proposed, request.ExpectedCurrentIntegritySha256),
            cancellationToken);
    }

    private async Task<GuidePackage> BuildCandidateAsync(
        StoredGuidePackage current,
        GuidePackageSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedCurrentIntegritySha256)
            || !string.Equals(
                request.ExpectedCurrentIntegritySha256.Trim(),
                current.IntegritySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active guide package changed. Check for guide updates again before syncing.");
        }

        if (current.Package.SourceInventory is null)
            throw new InvalidOperationException("This historical guide package has no pinned source inventory and cannot be synced. Restore or apply a current package first.");

        var snapshot = await upstreamTreeClient.GetSnapshotAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ExpectedUpstreamRevision)
            || !string.Equals(
                request.ExpectedUpstreamRevision.Trim(),
                snapshot.Revision,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TRaSH Guides changed after this check. Check again, then review the new sync preview.");
        }

        var sourceInventory = await upstreamTreeClient.GetSourceInventoryAsync(snapshot, cancellationToken);
        var basePackage = current.Package with
        {
            Version = current.Package.Version + 1,
            IntegritySha256 = null,
            Source = current.Package.Source with { UpstreamRevision = sourceInventory.UpstreamRevision }
        };
        return GuidePackageCatalog.MergeSourceInventory(basePackage, sourceInventory);
    }

    private static GuidePackageUpdatePreview UnavailablePreview(StoredGuidePackage current, string message)
        => new(
            current,
            current.Package,
            current.IntegritySha256,
            GuideCapabilityInventoryBuilder.Build(current.Package),
            [],
            [message],
            [],
            false);
}
