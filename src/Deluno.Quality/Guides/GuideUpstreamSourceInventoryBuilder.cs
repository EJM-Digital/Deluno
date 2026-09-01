using System.Text.Json;

namespace Deluno.Quality.Guides;

/// <summary>
/// Converts the small, fixed set of TRaSH JSON sources Deluno supports into a
/// lossless provenance inventory. This deliberately preserves upstream
/// matcher clauses and scores as data; it does not interpret them as a
/// release decision.
/// </summary>
public static class GuideUpstreamSourceInventoryBuilder
{
    private static readonly SourceRoot[] Roots =
    [
        new("docs/json/radarr/cf", "custom-format", "movies"),
        new("docs/json/sonarr/cf", "custom-format", "tv"),
        new("docs/json/radarr/cf-groups", "format-group", "movies"),
        new("docs/json/sonarr/cf-groups", "format-group", "tv"),
        new("docs/json/radarr/quality-profiles", "quality-profile", "movies"),
        new("docs/json/sonarr/quality-profiles", "quality-profile", "tv")
    ];

    public static bool IsTrackedSourcePath(string path)
        => TryGetRoot(path, out _);

    public static GuideSourceInventory Build(
        GuideUpstreamTreeSnapshot snapshot,
        IReadOnlyDictionary<string, string> sourceTextByPath)
    {
        var customFormats = new List<GuideSourceCustomFormat>();
        var groups = new List<GuideSourceFormatGroup>();
        var profiles = new List<GuideSourceQualityProfile>();

        foreach (var (path, blobSha) in snapshot.BlobShaByPath.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!TryGetRoot(path, out var root)) continue;
            if (!sourceTextByPath.TryGetValue(path, out var sourceText))
                throw new InvalidDataException($"The pinned upstream archive omitted tracked source '{path}'.");

            using var document = JsonDocument.Parse(sourceText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Upstream source '{path}' must contain a JSON object.");

            switch (root.Kind)
            {
                case "custom-format":
                    customFormats.Add(ParseCustomFormat(document.RootElement, path, blobSha, root.MediaType));
                    break;
                case "format-group":
                    groups.Add(ParseFormatGroup(document.RootElement, path, blobSha, root.MediaType));
                    break;
                case "quality-profile":
                    profiles.Add(ParseQualityProfile(document.RootElement, path, blobSha, root.MediaType, sourceText));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported upstream source kind '{root.Kind}'.");
            }
        }

        if (customFormats.Count == 0 || groups.Count == 0 || profiles.Count == 0)
            throw new InvalidDataException("The upstream guide archive did not contain every required supported source collection.");

        return new GuideSourceInventory(
            2,
            snapshot.Revision,
            customFormats.OrderBy(item => item.MediaType, StringComparer.Ordinal).ThenBy(item => item.TrashId, StringComparer.Ordinal).ToArray(),
            groups.OrderBy(item => item.MediaType, StringComparer.Ordinal).ThenBy(item => item.TrashId, StringComparer.Ordinal).ToArray(),
            profiles.OrderBy(item => item.MediaType, StringComparer.Ordinal).ThenBy(item => item.TrashId, StringComparer.Ordinal).ToArray());
    }

    private static GuideSourceCustomFormat ParseCustomFormat(JsonElement raw, string path, string blobSha, string mediaType)
    {
        var scores = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (raw.TryGetProperty("trash_scores", out var rawScores) && rawScores.ValueKind == JsonValueKind.Object)
        {
            foreach (var score in rawScores.EnumerateObject())
            {
                if (!score.Value.TryGetInt32(out var value))
                    throw new InvalidDataException($"Upstream source '{path}' has a non-integer score '{score.Name}'.");
                scores.Add(score.Name, value);
            }
        }

        var clauses = new List<GuideSourceMatcherClause>();
        if (raw.TryGetProperty("specifications", out var specifications) && specifications.ValueKind != JsonValueKind.Null)
        {
            if (specifications.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Upstream source '{path}' has invalid matcher specifications.");
            foreach (var specification in specifications.EnumerateArray())
            {
                if (specification.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Upstream source '{path}' contains a non-object matcher specification.");
                clauses.Add(new GuideSourceMatcherClause(
                    ReadRequiredString(specification, "name", path),
                    ReadRequiredString(specification, "implementation", path),
                    ReadBoolean(specification, "negate", false, path),
                    ReadBoolean(specification, "required", true, path),
                    specification.TryGetProperty("fields", out var fields) ? fields.GetRawText() : "null"));
            }
        }

        return new GuideSourceCustomFormat(
            ReadRequiredString(raw, "trash_id", path),
            ReadRequiredString(raw, "name", path),
            ReadOptionalString(raw, "trash_description"),
            mediaType,
            path,
            blobSha,
            scores,
            ReadBoolean(raw, "includeCustomFormatWhenRenaming", false, path),
            clauses);
    }

    private static GuideSourceFormatGroup ParseFormatGroup(JsonElement raw, string path, string blobSha, string mediaType)
    {
        var customFormats = new List<GuideSourceFormatGroupEntry>();
        if (raw.TryGetProperty("custom_formats", out var formats) && formats.ValueKind != JsonValueKind.Null)
        {
            if (formats.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Upstream source '{path}' has invalid custom-format entries.");
            foreach (var format in formats.EnumerateArray())
            {
                customFormats.Add(new GuideSourceFormatGroupEntry(
                    ReadRequiredString(format, "trash_id", path),
                    ReadRequiredString(format, "name", path),
                    ReadBoolean(format, "required", false, path)));
            }
        }

        var profileIds = new List<string>();
        if (raw.TryGetProperty("quality_profiles", out var qualityProfiles)
            && qualityProfiles.ValueKind == JsonValueKind.Object
            && qualityProfiles.TryGetProperty("include", out var include)
            && include.ValueKind == JsonValueKind.Object)
        {
            profileIds.AddRange(include.EnumerateObject()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => ReadStringValue(item.Value, path, $"quality profile '{item.Name}'")));
        }

        return new GuideSourceFormatGroup(
            ReadRequiredString(raw, "trash_id", path),
            ReadRequiredString(raw, "name", path),
            ReadOptionalString(raw, "trash_description"),
            mediaType,
            path,
            blobSha,
            customFormats,
            profileIds);
    }

    private static GuideSourceQualityProfile ParseQualityProfile(JsonElement raw, string path, string blobSha, string mediaType, string sourceText)
    {
        var assignments = new List<GuideSourceProfileFormatAssignment>();
        if (raw.TryGetProperty("formatItems", out var formatItems) && formatItems.ValueKind != JsonValueKind.Null)
        {
            if (formatItems.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Upstream source '{path}' has invalid profile format assignments.");
            assignments.AddRange(formatItems.EnumerateObject()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new GuideSourceProfileFormatAssignment(item.Name, ReadStringValue(item.Value, path, $"format assignment '{item.Name}'"))));
        }

        return new GuideSourceQualityProfile(
            ReadRequiredString(raw, "trash_id", path),
            ReadRequiredString(raw, "name", path),
            ReadOptionalString(raw, "trash_description"),
            mediaType,
            path,
            blobSha,
            assignments,
            sourceText.Trim());
    }

    private static bool TryGetRoot(string path, out SourceRoot root)
    {
        foreach (var candidate in Roots)
        {
            var prefix = candidate.Path + "/";
            if (path.StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !path[prefix.Length..].Contains('/', StringComparison.Ordinal))
            {
                root = candidate;
                return true;
            }
        }

        root = default!;
        return false;
    }

    private static string ReadRequiredString(JsonElement element, string property, string path)
        => element.TryGetProperty(property, out var value)
            ? ReadStringValue(value, path, property)
            : throw new InvalidDataException($"Upstream source '{path}' is missing required property '{property}'.");

    private static string? ReadOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static string ReadStringValue(JsonElement value, string path, string name)
        => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Upstream source '{path}' has an invalid {name}.");

    private static bool ReadBoolean(JsonElement element, string property, bool fallback, string path)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind == JsonValueKind.True ? true
            : value.ValueKind == JsonValueKind.False ? false
            : throw new InvalidDataException($"Upstream source '{path}' has a non-boolean '{property}'.");
    }

    private sealed record SourceRoot(string Path, string Kind, string MediaType);
}
