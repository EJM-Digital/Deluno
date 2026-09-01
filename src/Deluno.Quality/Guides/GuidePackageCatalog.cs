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

        ValidateSourceInventory(package, formatIds, errors);

        return errors;
    }

    private static GuidePackage Load()
    {
        using var stream = typeof(GuidePackageCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded guide package '{ResourceName}' was not found.");
        var curated = JsonSerializer.Deserialize<GuidePackage>(stream, ReleasePreferenceJson.Options)
            ?? throw new InvalidOperationException("The embedded guide package was empty.");
        var package = MergeSourceInventory(curated, GuideSourceInventoryCatalog.Current);
        var errors = Validate(package);
        if (errors.Count > 0)
            throw new InvalidOperationException($"The embedded guide package is invalid: {string.Join(" | ", errors)}");
        return package with { IntegritySha256 = ComputeIntegritySha256(package) };
    }

    /// <summary>
    /// The short curated package contains only the mappings Deluno has reviewed
    /// by hand. Merge it with the complete, pinned upstream inventory so every
    /// remaining TRaSH rule is visible as Advanced instead of being omitted or
    /// interpreted from its numeric score.
    /// </summary>
    private static GuidePackage MergeSourceInventory(
        GuidePackage curated,
        GuideSourceInventory sourceInventory)
    {
        var groupIdsByFormat = (sourceInventory.FormatGroups ?? [])
            .SelectMany(group => (group.CustomFormats ?? []).Select(format => new
            {
                format.TrashId,
                GroupId = group.TrashId
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.TrashId) && !string.IsNullOrWhiteSpace(item.GroupId))
            .GroupBy(item => item.TrashId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(item => item.GroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var curatedById = (curated.CustomFormats ?? [])
            .Where(format => !string.IsNullOrWhiteSpace(format.TrashId))
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var formats = new List<GuideCustomFormat>(curatedById.Count + sourceInventory.CustomFormats.Count);

        foreach (var sourceFormat in (sourceInventory.CustomFormats ?? [])
                     .OrderBy(item => item.MediaType, StringComparer.Ordinal)
                     .ThenBy(item => item.TrashId, StringComparer.Ordinal))
        {
            var groupIds = groupIdsByFormat.GetValueOrDefault(sourceFormat.TrashId, []);
            if (curatedById.Remove(sourceFormat.TrashId, out var reviewed))
            {
                formats.Add(reviewed with
                {
                    MediaTypes = [sourceFormat.MediaType],
                    SourceGroupIds = groupIds,
                    SourceMatcherClauses = sourceFormat.MatcherClauses,
                    SourceScores = sourceFormat.Scores,
                    SourcePath = sourceFormat.SourcePath
                });
                continue;
            }

            formats.Add(new GuideCustomFormat(
                sourceFormat.TrashId,
                sourceFormat.Name,
                "upstream-advanced",
                sourceFormat.Description ?? "Upstream TRaSH rule retained for Advanced review.",
                sourceFormat.Scores.GetValueOrDefault("default"),
                [],
                false,
                GuideMappingStatus.Advanced,
                [],
                "trash-guides-upstream-advanced",
                [sourceFormat.MediaType],
                groupIds,
                sourceFormat.MatcherClauses,
                sourceFormat.Scores,
                sourceFormat.SourcePath));
        }

        // Deluno may retain deliberately authored rules that do not map to an
        // upstream custom-format id. They stay explicit curated adaptations;
        // the upstream coverage invariant below applies to every source item.
        formats.AddRange(curatedById.Values
            .OrderBy(format => format.TrashId, StringComparer.Ordinal));

        return curated with
        {
            CustomFormats = formats,
            SourceInventory = sourceInventory
        };
    }

    private static void ValidateSourceInventory(
        GuidePackage package,
        IReadOnlySet<string> formatIds,
        ICollection<string> errors)
    {
        var source = package.SourceInventory;
        if (source is null)
        {
            // Package schema v1 was already persisted before the full source
            // inventory existed. Keep those historical plans readable and
            // immutable; schema v2 and later must carry the inventory so a
            // newly proposed package can never silently omit upstream rules.
            if (package.SchemaVersion >= 2)
            {
                errors.Add("The guide package needs its pinned upstream source inventory.");
            }
            return;
        }

        if (source.SchemaVersion <= 0)
            errors.Add("The guide source inventory schema version must be positive.");
        if (!string.Equals(source.UpstreamRevision, package.Source.UpstreamRevision, StringComparison.OrdinalIgnoreCase))
            errors.Add("The guide source inventory revision must match the package provenance revision.");
        ValidateUnique((source.CustomFormats ?? []).Select(item => item.TrashId), "source custom format", errors);
        ValidateUnique((source.FormatGroups ?? []).Select(item => item.TrashId), "source format group", errors);
        ValidateUnique((source.QualityProfiles ?? []).Select(item => item.TrashId), "source quality profile", errors);

        var sourceFormatIds = (source.CustomFormats ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.TrashId))
            .Select(item => item.TrashId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceProfileIds = (source.QualityProfiles ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.TrashId))
            .Select(item => item.TrashId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var format in source.CustomFormats ?? [])
        {
            if (string.IsNullOrWhiteSpace(format.TrashId))
            {
                errors.Add("Every source custom format needs a stable TRaSH id.");
                continue;
            }
            if (!formatIds.Contains(format.TrashId))
                errors.Add($"Source custom format '{format.TrashId}' is not retained by the guide package.");
            if (string.IsNullOrWhiteSpace(format.MediaType) || string.IsNullOrWhiteSpace(format.SourcePath))
                errors.Add($"Source custom format '{format.TrashId}' needs media applicability and a source path.");
            if ((format.MatcherClauses ?? []).Any(clause => string.IsNullOrWhiteSpace(clause.Implementation) || string.IsNullOrWhiteSpace(clause.FieldsJson)))
                errors.Add($"Source custom format '{format.TrashId}' contains an incomplete matcher clause.");
        }

        foreach (var group in source.FormatGroups ?? [])
        {
            foreach (var entry in group.CustomFormats ?? [])
            {
                if (!sourceFormatIds.Contains(entry.TrashId))
                    errors.Add($"Source format group '{group.TrashId}' refers to unknown source custom format '{entry.TrashId}'.");
            }
            foreach (var profileId in group.QualityProfileIds ?? [])
            {
                if (!sourceProfileIds.Contains(profileId))
                    errors.Add($"Source format group '{group.TrashId}' refers to unknown source quality profile '{profileId}'.");
            }
        }

        foreach (var profile in source.QualityProfiles ?? [])
        {
            foreach (var assignment in profile.FormatAssignments ?? [])
            {
                if (!sourceFormatIds.Contains(assignment.TrashId))
                    errors.Add($"Source quality profile '{profile.TrashId}' refers to unknown source custom format '{assignment.TrashId}'.");
            }
        }
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
