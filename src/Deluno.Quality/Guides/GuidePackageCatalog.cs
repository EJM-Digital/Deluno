using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Guides;

/// <summary>
/// Loads the reviewed guide package shipped with the backend. Keeping the
/// package in the server assembly means every caller — UI, worker, and API
/// integration — sees the same version and provenance.
/// </summary>
public static class GuidePackageCatalog
{
    private const string ResourceName = "Deluno.Quality.Guides.trash-guide-package.json";

    public static GuidePackage Current { get; } = Load();

    public static string ComputeIntegritySha256(GuidePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var hashable = package with { IntegritySha256 = null };
        var json = JsonSerializer.Serialize(hashable, ReleasePreferenceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static IReadOnlyList<string> Validate(GuidePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(package.Id)) errors.Add("The guide package needs a stable id.");
        if (string.IsNullOrWhiteSpace(package.Name)) errors.Add("The guide package needs a name.");
        if (package.Version <= 0) errors.Add("The guide package version must be positive.");
        if (package.SchemaVersion <= 0) errors.Add("The guide package schema version must be positive.");
        if (package.Source is null) errors.Add("The guide package needs source provenance.");
        else
        {
            if (string.IsNullOrWhiteSpace(package.Source.SourceName)) errors.Add("Guide source provenance needs a source name.");
            if (string.IsNullOrWhiteSpace(package.Source.RepositoryUrl)) errors.Add("Guide source provenance needs a repository URL.");
            if (string.IsNullOrWhiteSpace(package.Source.GuideUrl)) errors.Add("Guide source provenance needs a guide URL.");
            if (string.IsNullOrWhiteSpace(package.Source.UpstreamRevision)) errors.Add("Guide source provenance needs an upstream revision.");
            if (string.IsNullOrWhiteSpace(package.Source.ReviewedUtc)) errors.Add("Guide source provenance needs a review date.");
            if (string.IsNullOrWhiteSpace(package.Source.Adaptation)) errors.Add("Guide source provenance needs an adaptation note.");
        }

        var tiers = package.QualityTiers ?? [];
        var formats = package.CustomFormats ?? [];
        var profiles = package.QualityProfiles ?? [];
        var bundles = package.Bundles ?? [];
        ValidateUnique(tiers.Select(item => item.Id), "quality tier", errors);
        ValidateUnique(formats.Select(item => item.TrashId), "custom format", errors);
        ValidateUnique(profiles.Select(item => item.Id), "quality profile", errors);
        ValidateUnique(bundles.Select(item => item.Id), "format bundle", errors);

        var tierIds = tiers.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var formatIds = formats.Select(item => item.TrashId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownTraits = PreferenceTraitRegistry.Current;

        foreach (var tier in tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Id)) errors.Add("Every guide quality tier needs a stable id.");
            if (string.IsNullOrWhiteSpace(tier.Label)) errors.Add($"Guide quality tier '{tier.Id}' needs a label.");
            if (tier.MinMbPerMin < 0 || tier.MaxMbPerMin < tier.MinMbPerMin)
                errors.Add($"Guide quality tier '{tier.Id}' has an invalid size range.");
        }

        foreach (var format in formats)
        {
            if (string.IsNullOrWhiteSpace(format.TrashId)) errors.Add("Every guide custom format needs a stable id.");
            if (string.IsNullOrWhiteSpace(format.Name)) errors.Add($"Guide custom format '{format.TrashId}' needs a name.");
            if (string.IsNullOrWhiteSpace(format.Category)) errors.Add($"Guide custom format '{format.TrashId}' needs a category.");
            if (string.IsNullOrWhiteSpace(format.SourceKind)) errors.Add($"Guide custom format '{format.TrashId}' needs source-kind provenance.");

            foreach (var pattern in format.Patterns ?? [])
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
                {
                    errors.Add($"Guide custom format '{format.TrashId}' has an invalid matching pattern: {exception.Message}");
                }
            }

            if (format.MappingStatus == GuideMappingStatus.Reviewed && (format.MappedTraitIds is null || format.MappedTraitIds.Count == 0))
                errors.Add($"Reviewed guide custom format '{format.TrashId}' must declare a typed mapping.");
            if (format.MappingStatus == GuideMappingStatus.Advanced && format.MappedTraitIds is { Count: > 0 })
                errors.Add($"Advanced guide custom format '{format.TrashId}' must not contain an unreviewed typed mapping.");
            foreach (var traitId in format.MappedTraitIds ?? [])
            {
                if (!knownTraits.IsKnown(traitId))
                    errors.Add($"Guide custom format '{format.TrashId}' maps to unknown typed trait '{traitId}'.");
            }
        }

        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) errors.Add("Every guide quality profile needs a stable id.");
            if (!tierIds.Contains(profile.CutoffQualityId))
                errors.Add($"Guide quality profile '{profile.Id}' has an unknown cutoff tier '{profile.CutoffQualityId}'.");
            foreach (var tierId in profile.QualityOrder ?? [])
            {
                if (!tierIds.Contains(tierId)) errors.Add($"Guide quality profile '{profile.Id}' refers to unknown tier '{tierId}'.");
            }
            foreach (var recommendation in profile.RecommendedFormats ?? [])
            {
                if (!formatIds.Contains(recommendation.TrashId))
                    errors.Add($"Guide quality profile '{profile.Id}' refers to unknown custom format '{recommendation.TrashId}'.");
            }
        }

        foreach (var bundle in bundles)
        {
            foreach (var entry in bundle.Includes ?? [])
            {
                if (!formatIds.Contains(entry.TrashId))
                    errors.Add($"Guide format bundle '{bundle.Id}' refers to unknown custom format '{entry.TrashId}'.");
            }
        }

        return errors;
    }

    private static GuidePackage Load()
    {
        using var stream = typeof(GuidePackageCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded guide package '{ResourceName}' was not found.");
        var package = JsonSerializer.Deserialize<GuidePackage>(stream, ReleasePreferenceJson.Options)
            ?? throw new InvalidOperationException("The embedded guide package was empty.");
        var errors = Validate(package);
        if (errors.Count > 0)
            throw new InvalidOperationException($"The embedded guide package is invalid: {string.Join(" | ", errors)}");
        return package with { IntegritySha256 = ComputeIntegritySha256(package) };
    }

    private static void ValidateUnique(IEnumerable<string?> values, string kind, ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!seen.Add(value.Trim())) errors.Add($"Guide {kind} '{value}' appears more than once.");
        }
    }
}
