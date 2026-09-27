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

    /// <summary>A reviewed entry that is a genuine TRaSH format, id and all.</summary>
    public const string GenuineSourceKind = "trash-guides";

    /// <summary>A format TRaSH does not publish, owned and named by Deluno.</summary>
    public const string DelunoSourceKind = "deluno";

    public const string DelunoIdPrefix = "deluno-";

    /// <summary>
    /// From this schema on, every reviewed entry is either a genuine TRaSH
    /// format or a Deluno-owned extra, and the package proves which. Before it,
    /// the shipped catalogue gave TRaSH-looking ids to its own adaptations -
    /// "Amazon Prime" carried TRaSH's id for the Canadian service Crave - and
    /// nothing checked. Earlier packages stay readable as they were.
    /// </summary>
    public const int GenuineFormatsSchemaVersion = 3;

    /// <summary>
    /// TRaSH format-group names that identify an upstream item as belonging to
    /// a specific region, mapped to the picker category it should be reachable
    /// under instead of the catch-all "upstream-advanced" bucket (#779). Anime
    /// regional groups are excluded: the anime category already exists and
    /// promoting anime tiers by default is #781's job, not a regional split.
    ///
    /// Declared before <see cref="Current"/> so its own static initializer has
    /// already run by the time Current's Load() call reaches
    /// <see cref="MergeSourceInventory"/> -- C# runs static field initializers
    /// in textual declaration order, and Current used to sit above this field,
    /// so every regional lookup silently saw a null dictionary.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RegionalSourceGroupCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["[Release Groups] French"] = "french",
            ["[Optional] French Audio Version"] = "french",
            ["[Unwanted] Unwanted Formats French"] = "french",
            ["[Streaming Services] French"] = "french",
            ["[Release Groups] German"] = "german",
            ["[Optional] German Miscellaneous"] = "german",
            ["[Unwanted] Unwanted Formats German"] = "german",
            ["[Streaming Services] Dutch"] = "dutch",
            ["[Streaming Services] Asian"] = "asian",
            ["[Release Groups] Asian"] = "asian",
            ["[Streaming Services] UK"] = "uk"
        };

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
        if (package.SchemaVersion >= GenuineFormatsSchemaVersion)
        {
            ValidateGenuineFormats(package, errors);
        }

        return errors;
    }

    private static void ValidateGenuineFormats(GuidePackage package, ICollection<string> errors)
    {
        var sourceById = (package.SourceInventory?.CustomFormats ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.TrashId))
            .ToDictionary(item => item.TrashId, StringComparer.OrdinalIgnoreCase);
        foreach (var format in package.CustomFormats ?? [])
        {
            var upstream = sourceById.GetValueOrDefault(format.TrashId);
            switch (format.SourceKind)
            {
                case GenuineSourceKind:
                    // A format TRaSH later drops keeps the specifications it
                    // was merged with, so a sync cannot silently change it.
                    if (format.SourceMatcherClauses is not { Count: > 0 })
                        errors.Add($"Guide custom format '{format.Name}' claims TRaSH id '{format.TrashId}' but has no TRaSH specifications.");
                    if (upstream is not null && !string.Equals(upstream.Name, format.Name, StringComparison.Ordinal))
                        errors.Add($"Guide custom format '{format.Name}' carries the TRaSH id of '{upstream.Name}'.");
                    if (format.Patterns is { Count: > 0 })
                        errors.Add($"Genuine TRaSH format '{format.Name}' must be matched by TRaSH's specifications, not by Deluno patterns.");
                    break;
                case DelunoSourceKind:
                    if (!format.TrashId.StartsWith(DelunoIdPrefix, StringComparison.Ordinal))
                        errors.Add($"Deluno guide format '{format.Name}' must use a '{DelunoIdPrefix}' id, not '{format.TrashId}'.");
                    if (upstream is not null)
                        errors.Add($"Deluno guide format '{format.Name}' uses an id TRaSH already gives '{upstream.Name}'.");
                    if (format.Patterns is not { Count: > 0 })
                        errors.Add($"Deluno guide format '{format.Name}' needs at least one pattern.");
                    break;
            }
        }
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
    /// interpreted from its numeric score. Reviewed Deluno mappings remain
    /// intact; a newly discovered upstream format is retained as Advanced until
    /// Deluno has reviewed its semantics.
    /// </summary>
    public static GuidePackage MergeSourceInventory(
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
        var groupNameById = (sourceInventory.FormatGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group.TrashId))
            .ToDictionary(group => group.TrashId, group => group.Name, StringComparer.OrdinalIgnoreCase);
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
                    // A genuine entry is TRaSH's format under TRaSH's name, so
                    // an upstream rename is followed rather than hidden.
                    Name = string.Equals(reviewed.SourceKind, GenuineSourceKind, StringComparison.Ordinal)
                        ? sourceFormat.Name
                        : reviewed.Name,
                    MediaTypes = [sourceFormat.MediaType],
                    SourceGroupIds = groupIds,
                    SourceMatcherClauses = sourceFormat.MatcherClauses,
                    SourceScores = sourceFormat.Scores,
                    SourcePath = sourceFormat.SourcePath,
                    IncludeWhenRenaming = sourceFormat.IncludeWhenRenaming
                });
                continue;
            }

            var regionalCategory = groupIds
                .Select(groupId => groupNameById.GetValueOrDefault(groupId))
                .Where(name => name is not null)
                .Select(name => RegionalSourceGroupCategories.GetValueOrDefault(name!))
                .FirstOrDefault(category => category is not null);

            formats.Add(new GuideCustomFormat(
                sourceFormat.TrashId,
                sourceFormat.Name,
                regionalCategory ?? "upstream-advanced",
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
                sourceFormat.SourcePath,
                sourceFormat.IncludeWhenRenaming));
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
            if (source.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(format.SourceBlobSha))
                errors.Add($"Source custom format '{format.TrashId}' needs its pinned source blob SHA.");
            if ((format.MatcherClauses ?? []).Any(clause => string.IsNullOrWhiteSpace(clause.Implementation) || string.IsNullOrWhiteSpace(clause.FieldsJson)))
                errors.Add($"Source custom format '{format.TrashId}' contains an incomplete matcher clause.");
        }

        foreach (var group in source.FormatGroups ?? [])
        {
            if (source.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(group.SourceBlobSha))
                errors.Add($"Source format group '{group.TrashId}' needs its pinned source blob SHA.");
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
            if (source.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(profile.SourceBlobSha))
                errors.Add($"Source quality profile '{profile.TrashId}' needs its pinned source blob SHA.");
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
