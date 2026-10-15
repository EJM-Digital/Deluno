namespace Deluno.Quality;

/// <summary>
/// The one place that builds the inputs of a wanted-state decision from what is known about a file (#1334).
///
/// <para>An import, a re-decision after a profile is saved and a re-decision after a library changes all judge a
/// file the same way: by its quality name, its real size and the size its tier asks for. When each built those
/// inputs for itself, the re-decision forgot the size, and saving a profile again turned an undersized file marked
/// Upgrade into Covered.</para>
/// </summary>
public static class WantedDecisionInputs
{
    public static MediaWantedDecisionInput Build(
        string mediaType,
        bool hasFile,
        string? currentQuality,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        long? fileSizeBytes,
        QualityModelSnapshot? model,
        bool isReleased = true)
        => new(
            MediaType: mediaType,
            HasFile: hasFile,
            CurrentQuality: currentQuality,
            CutoffQuality: cutoffQuality,
            UpgradeUntilCutoff: upgradeUntilCutoff,
            UpgradeUnknownItems: upgradeUnknownItems,
            IsReleased: isReleased,
            FileSizeBytes: fileSizeBytes,
            SizeFloorBytes: model is null ? null : SizeFloorFor(mediaType, currentQuality, model));

    /// <summary>The size floor this tier sets, in bytes, or null where there is no rule or the quality is unknown.</summary>
    public static long? SizeFloorFor(string mediaType, string? quality, QualityModelSnapshot model)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        var tier = model.Tiers.FirstOrDefault(item =>
            string.Equals(item.Name, quality, StringComparison.OrdinalIgnoreCase));
        if (tier is null)
        {
            return null;
        }

        // A show's rule is per episode and a film's per film.
        var (floor, _) = mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase)
            ? QualityTierBytes.ForEpisode(tier)
            : QualityTierBytes.ForMovie(tier);
        return floor > 0 ? floor : null;
    }
}
