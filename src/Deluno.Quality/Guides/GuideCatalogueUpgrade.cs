using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Quality.Guides;

/// <summary>
/// Carries Deluno's own guide layer forward on an install that has stored a
/// guide package. The stored package wins over the one Deluno ships, so an
/// install that had synced or applied a package would have kept the old
/// catalogue - TRaSH-looking ids on Deluno's own adaptations - for ever.
///
/// <para>Only the layer Deluno owns is replaced: the upstream inventory the
/// owner synced stays as it is, and the package is stored as a new version
/// beside the old one, which stays retained and can be activated again.
/// Rules made from the old catalogue are not touched here; moving them is the
/// owner's choice, from a preview.</para>
/// </summary>
public static class GuideCatalogueUpgrade
{
    public static async Task<StoredGuidePackage?> RunAsync(
        IGuidePackageStore store,
        GuidePackage shipped,
        CancellationToken cancellationToken)
    {
        var current = await store.GetCurrentAsync(cancellationToken);
        if (current.StoredUtc == DateTimeOffset.MinValue
            || current.Package.SchemaVersion >= shipped.SchemaVersion
            || !string.Equals(current.Package.Id, shipped.Id, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var nextVersion = (await store.ListAsync(cancellationToken))
            .Where(item => string.Equals(item.Package.Id, shipped.Id, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Package.Version)
            .DefaultIfEmpty(shipped.Version)
            .Max() + 1;
        nextVersion = Math.Max(nextVersion, shipped.Version);

        var inventory = current.Package.SourceInventory ?? shipped.SourceInventory;
        var candidate = inventory is null
            ? shipped with { Version = nextVersion, IntegritySha256 = null }
            : GuidePackageCatalog.MergeSourceInventory(
                shipped with
                {
                    Version = nextVersion,
                    IntegritySha256 = null,
                    SourceInventory = null,
                    Source = shipped.Source with { UpstreamRevision = inventory.UpstreamRevision }
                },
                inventory);

        return await store.ApplyAsync(
            new GuidePackageUpdateRequest(candidate, current.IntegritySha256),
            cancellationToken);
    }
}

public sealed class GuideCatalogueUpgradeHostedService(
    IGuidePackageStore store,
    IDelunoStartupGate startupGate,
    ILogger<GuideCatalogueUpgradeHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startupGate.WaitUntilReadyAsync(logger, "The guide layer upgrade", stoppingToken))
            {
                return;
            }
            var upgraded = await GuideCatalogueUpgrade.RunAsync(store, GuidePackageCatalog.Current, stoppingToken);
            if (upgraded is not null)
            {
                logger.LogInformation(
                    "Stored guide package {PackageId} version {Version} with Deluno's genuine TRaSH format layer; the previous version stays retained.",
                    upgraded.Package.Id,
                    upgraded.Package.Version);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deluno could not carry its guide layer forward onto the stored guide package; the stored package stays active.");
        }
    }
}
