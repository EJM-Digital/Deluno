using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Guides;

/// <summary>
/// Machine-readable proof that every item exposed by the bundled guide package
/// has an explicit Deluno representation. A guide item may be represented by a
/// typed trait/plan or by an auditable Advanced legacy matcher, but it may not
/// disappear at the translation boundary.
/// </summary>
public sealed record GuideCapabilityInventory(
    string PackageId,
    int PackageVersion,
    string SourceRevision,
    string PackageIntegritySha256,
    int TotalItemCount,
    int TypedItemCount,
    int AdvancedItemCount,
    IReadOnlyList<GuideCapabilityInventoryItem> Items,
    IReadOnlyList<string> Unaccounted,
    string InventoryHash);

public sealed record GuideCapabilityInventoryItem(
    string Kind,
    string Id,
    string MediaType,
    string Category,
    string Representation,
    IReadOnlyList<string> TypedTraitIds,
    string Provenance);

public static class GuideCapabilityInventoryBuilder
{
    public static GuideCapabilityInventory Build(GuidePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var items = new List<GuideCapabilityInventoryItem>();
        var unaccounted = GuidePackageCatalog.Validate(package)
            .Select(error => $"package: {error}")
            .ToList();

        foreach (var tier in package.QualityTiers ?? [])
        {
            var quality = NormalizeGuideQuality(tier.Label);
            var traitId = string.IsNullOrWhiteSpace(quality)
                ? null
                : InstalledPreferenceEvaluationFactory.QualityTraitId(quality);
            var typed = traitId is not null && PreferenceTraitRegistry.Current.TryResolve(traitId, out _);
            items.Add(new GuideCapabilityInventoryItem(
                "quality-tier",
                tier.Id,
                "movies+tv",
                tier.Source,
                typed ? "typed-trait" : "unaccounted",
                typed ? [traitId!] : [],
                "trash-guide-package"));
            if (!typed)
            {
                unaccounted.Add($"quality-tier:{tier.Id} could not be represented by a canonical Deluno quality trait.");
            }
        }

        var formatsById = (package.CustomFormats ?? [])
            .Where(format => !string.IsNullOrWhiteSpace(format.TrashId))
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        foreach (var format in package.CustomFormats ?? [])
        {
            var typedTraits = (format.MappedTraitIds ?? [])
                .Where(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait) && !trait.Transient)
                .Select(traitId => PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait) ? trait.Id : traitId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(traitId => traitId, StringComparer.Ordinal)
                .ToArray();
            var invalidTypedTraits = (format.MappedTraitIds ?? [])
                .Where(traitId => !PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait) || trait.Transient)
                .ToArray();

            var representation = format.MappingStatus switch
            {
                GuideMappingStatus.Reviewed when IsForbiddenCategory(format.Category)
                    && invalidTypedTraits.Length == 0 && typedTraits.Length > 0 => "typed-forbidden",
                GuideMappingStatus.Reviewed when invalidTypedTraits.Length == 0 && typedTraits.Length > 0 => "typed-trait",
                GuideMappingStatus.Advanced when invalidTypedTraits.Length == 0 && typedTraits.Length == 0 => "advanced-legacy-matcher",
                _ => "unaccounted"
            };
            items.Add(new GuideCapabilityInventoryItem(
                "custom-format",
                format.TrashId,
                "movies+tv",
                format.Category,
                representation,
                typedTraits,
                format.SourceKind));
            if (representation == "unaccounted")
            {
                unaccounted.Add($"custom-format:{format.TrashId} has an invalid typed/Advanced representation.");
            }

            // The package currently exposes release-title regex clauses. Keep
            // the clause shape in the inventory so a future package cannot add
            // a matcher form without making the coverage decision explicit.
            for (var index = 0; index < (format.Patterns ?? []).Count; index++)
            {
                items.Add(new GuideCapabilityInventoryItem(
                    "matcher-clause",
                    $"{format.TrashId}:{index + 1}",
                    "movies+tv",
                    format.Category,
                    "release-title-regex|required|not-negated",
                    [],
                    format.SourceKind));
            }
        }

        foreach (var profile in package.QualityProfiles ?? [])
        {
            try
            {
                var compilation = GuidePlanCompiler.Compile(profile.Id, profile.MediaType, package);
                items.Add(new GuideCapabilityInventoryItem(
                    "quality-profile",
                    profile.Id,
                    profile.MediaType,
                    "quality-and-release",
                    compilation.AdvancedRules.Count == 0 ? "typed-plan" : "typed-plan+advanced",
                    compilation.Plan.Families
                        .SelectMany(family => family.Levels)
                        .SelectMany(level => level.TraitIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(traitId => traitId, StringComparer.Ordinal)
                        .ToArray(),
                    $"trash-guide-package:{package.Source.UpstreamRevision}"));
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                items.Add(new GuideCapabilityInventoryItem(
                    "quality-profile",
                    profile.Id,
                    profile.MediaType,
                    "quality-and-release",
                    "unaccounted",
                    [],
                    "trash-guide-package"));
                unaccounted.Add($"quality-profile:{profile.Id} could not compile: {exception.Message}");
            }
        }

        foreach (var bundle in package.Bundles ?? [])
        {
            var missing = (bundle.Includes ?? [])
                .Where(entry => !formatsById.ContainsKey(entry.TrashId))
                .Select(entry => entry.TrashId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            var hasAdvanced = (bundle.Includes ?? [])
                .Select(entry => formatsById.GetValueOrDefault(entry.TrashId))
                .Any(format => format?.MappingStatus == GuideMappingStatus.Advanced);
            var representation = missing.Length > 0
                ? "unaccounted"
                : hasAdvanced ? "typed-bundle+advanced" : "typed-bundle";
            items.Add(new GuideCapabilityInventoryItem(
                "format-bundle",
                bundle.Id,
                bundle.MediaType,
                bundle.Level,
                representation,
                [],
                $"trash-guide-package:{package.Source.UpstreamRevision}"));
            foreach (var missingId in missing)
            {
                unaccounted.Add($"format-bundle:{bundle.Id} references missing custom-format:{missingId}.");
            }
        }

        var orderedItems = items
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ToArray();
        var orderedUnaccounted = unaccounted
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(error => error, StringComparer.Ordinal)
            .ToArray();
        var typedCount = orderedItems.Count(item => item.Representation.StartsWith("typed", StringComparison.Ordinal));
        var advancedCount = orderedItems.Count(item => item.Representation.Contains("advanced", StringComparison.Ordinal));
        var packageIntegrity = package.IntegritySha256 ?? GuidePackageCatalog.ComputeIntegritySha256(package);
        var inventoryHash = ComputeHash(package, packageIntegrity, orderedItems, orderedUnaccounted);

        return new GuideCapabilityInventory(
            package.Id,
            package.Version,
            package.Source.UpstreamRevision,
            packageIntegrity,
            orderedItems.Length,
            typedCount,
            advancedCount,
            orderedItems,
            orderedUnaccounted,
            inventoryHash);
    }

    private static string ComputeHash(
        GuidePackage package,
        string packageIntegrity,
        IReadOnlyList<GuideCapabilityInventoryItem> items,
        IReadOnlyList<string> unaccounted)
    {
        var payload = JsonSerializer.Serialize(new
        {
            package = package.Id,
            version = package.Version,
            source = package.Source.UpstreamRevision,
            packageIntegrity,
            items,
            unaccounted
        }, ReleasePreferenceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string? NormalizeGuideQuality(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var normalized = label.Trim()
            .Replace("web-dl", "web", StringComparison.OrdinalIgnoreCase)
            .Replace("webrip", "web", StringComparison.OrdinalIgnoreCase)
            .Replace("4k", "2160p", StringComparison.OrdinalIgnoreCase);
        return MediaPolicyCatalog.Current.NormalizeQuality(normalized);
    }

    private static bool IsForbiddenCategory(string? category)
        => string.Equals(category?.Trim(), "unwanted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category?.Trim(), "safety", StringComparison.OrdinalIgnoreCase);
}
