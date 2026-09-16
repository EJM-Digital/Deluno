using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Quality.Contracts;

namespace Deluno.Quality.Guides;

/// <summary>
/// Works out, without changing anything, how each rule made from Deluno's old
/// guide catalogue moves onto what that entry really is.
///
/// <para>A rule keeps its id, so every profile that uses it and every answer a
/// profile gave about it stay attached; it keeps its score and media type. What
/// changes is what the rule <em>is</em>: the id it claims, the conditions it
/// matches by, and - only when the owner never renamed it - its name.</para>
///
/// <para>A rule is only offered when it still carries both an old catalogue id
/// and the kind of conditions the old catalogue wrote. A rule imported from
/// Radarr or Sonarr can carry the same id legitimately (TRaSH's "Upscaled" id
/// is also old "Blu-ray"'s), and its specifications prove it is not one of
/// these.</para>
/// </summary>
public static class GuideRuleMigrationPlanner
{
    public const string AdoptTrashFormat = "adopt-trash-format";
    public const string AdoptDelunoFormat = "adopt-deluno-format";
    public const string KeepAsOwnRule = "keep-as-own-rule";

    public static GuideRuleMigrationPreview Plan(
        IReadOnlyList<CustomFormatItem> rules,
        IReadOnlyList<QualityProfileItem> profiles,
        GuidePackage package,
        LegacyGuideCatalogue? legacy = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        legacy ??= LegacyGuideCatalogue.Current;
        var formatsById = package.CustomFormats
            .ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var items = new List<GuideRuleMigrationItem>();

        foreach (var rule in rules.OrderBy(item => item.MediaType, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = legacy.Find(rule.TrashId);
            if (entry is null || GuideFormatConditions.IsSpecificationShaped(rule.Conditions))
            {
                continue;
            }

            var patterns = ReadTitlePatterns(rule.Conditions);
            var edited = patterns is null
                || !patterns.ToHashSet(StringComparer.Ordinal).SetEquals(entry.LegacyPatterns);
            var mediaLabel = MediaPolicyCatalog.NormalizeMediaType(rule.MediaType) == "tv" ? "TV" : "movies";
            var profileNames = profiles
                .Where(profile => SplitIds(profile.CustomFormatIds).Contains(rule.Id, StringComparer.OrdinalIgnoreCase))
                .Select(profile => profile.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var keepsName = !string.Equals(rule.Name.Trim(), entry.LegacyName, StringComparison.OrdinalIgnoreCase);
            var editedNote = edited
                ? " Its conditions were changed from what Deluno shipped, so moving it replaces your changes."
                : string.Empty;

            var targetId = entry.TargetFor(rule.MediaType);
            if (targetId is null || !formatsById.TryGetValue(targetId, out var target) || !GuideFormatConditions.AppliesTo(target, rule.MediaType))
            {
                // TRaSH has no version of this for the rule's media type. It
                // stays the owner's rule, still matching any one of its
                // patterns - which without a guide id has to be said outright.
                var ownConditions = patterns is null ? rule.Conditions : AnyOf(patterns);
                items.Add(new GuideRuleMigrationItem(
                    rule.Id,
                    rule.MediaType,
                    rule.Name,
                    rule.TrashId,
                    rule.Conditions,
                    KeepAsOwnRule,
                    rule.Name,
                    null,
                    null,
                    ownConditions,
                    edited,
                    profileNames,
                    $"TRaSH publishes no {mediaLabel} version of “{entry.LegacyName}”. It stays as your own rule with the same conditions, without a guide id."));
                continue;
            }

            var newConditions = GuideFormatConditions.For(target);
            if (string.Equals(rule.TrashId, target.TrashId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Conditions, newConditions, StringComparison.Ordinal))
            {
                continue;
            }

            var genuine = string.Equals(target.SourceKind, GuidePackageCatalog.GenuineSourceKind, StringComparison.Ordinal);
            items.Add(new GuideRuleMigrationItem(
                rule.Id,
                rule.MediaType,
                rule.Name,
                rule.TrashId,
                rule.Conditions,
                genuine ? AdoptTrashFormat : AdoptDelunoFormat,
                keepsName ? rule.Name : target.Name,
                target.TrashId,
                target.Name,
                newConditions,
                edited,
                profileNames,
                // Scores and profile answers surviving is true of every rule in
                // the move, so it is said once where the move is described, not
                // appended to all of them. Repeated on every row it stopped
                // being reassurance and became noise the eye skips - taking the
                // part that differs per rule with it.
                (genuine
                    ? $"Becomes TRaSH's “{target.Name}” for {mediaLabel}, with TRaSH's own id and conditions."
                    : $"TRaSH has no such format. It keeps Deluno's patterns under Deluno's own id, {target.TrashId}.")
                + editedNote));
        }

        return new GuideRuleMigrationPreview(
            package.Version,
            items,
            ComputeHash(package, items));
    }

    /// <summary>
    /// The release-title patterns in any of the three shapes the old catalogue
    /// was written in: a JSON list of patterns, a JSON list of title conditions,
    /// or "regex:" lines. Null when anything else is in there.
    /// </summary>
    public static IReadOnlyList<string>? ReadTitlePatterns(string? conditions)
    {
        if (string.IsNullOrWhiteSpace(conditions))
        {
            return null;
        }

        var trimmed = conditions.Trim();
        if (!trimmed.StartsWith('['))
        {
            var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // "type: value" lines other than regex are conditions of another kind.
            return lines.Any(line => !line.StartsWith("regex:", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                ? null
                : lines.Select(StripRegexPrefix).ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var patterns = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    patterns.Add(StripRegexPrefix(item.GetString() ?? string.Empty));
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var type)
                    || type.GetString() is not ("releaseTitle" or "regex")
                    || !item.TryGetProperty("value", out var value)
                    || (item.TryGetProperty("negate", out var negate) && negate.ValueKind == JsonValueKind.True))
                {
                    return null;
                }

                patterns.Add(StripRegexPrefix(value.GetString() ?? string.Empty));
            }

            return patterns;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripRegexPrefix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["regex:".Length..].Trim()
            : trimmed;
    }

    /// <summary>Title conditions any one of which is enough, with no guide id to imply it.</summary>
    private static string AnyOf(IReadOnlyList<string> patterns)
        => JsonSerializer.Serialize(patterns.Select(pattern => new
        {
            type = "releaseTitle",
            value = pattern,
            negate = false,
            required = false
        }));

    private static IEnumerable<string> SplitIds(string? csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string ComputeHash(GuidePackage package, IReadOnlyList<GuideRuleMigrationItem> items)
    {
        var builder = new StringBuilder()
            .Append(package.Id).Append('|').Append(package.Version).Append('|').Append(package.IntegritySha256).Append('\n');
        foreach (var item in items)
        {
            builder.Append(item.RuleId).Append('|')
                .Append(item.CurrentConditions).Append('|')
                .Append(item.NewName).Append('|')
                .Append(item.NewTrashId).Append('|')
                .Append(item.NewConditions).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

public sealed record GuideRuleMigrationPreview(
    int PackageVersion,
    IReadOnlyList<GuideRuleMigrationItem> Items,
    string PreviewHash);

public sealed record GuideRuleMigrationItem(
    string RuleId,
    string MediaType,
    string CurrentName,
    string? CurrentTrashId,
    string CurrentConditions,
    string Action,
    string NewName,
    string? NewTrashId,
    string? NewFormatName,
    string NewConditions,
    bool ConditionsWereEdited,
    IReadOnlyList<string> ProfileNames,
    string Explanation);
