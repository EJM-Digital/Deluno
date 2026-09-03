using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Contracts;

/// <summary>
/// Words a subtitle's release name must or must not carry before Deluno will
/// take it.
///
/// <para>Language and hearing-impaired are already decided elsewhere. This is
/// the remaining axis a person actually uses: "only take subtitles from this
/// group", or "never take one that says HDTV when my file is a Blu-ray". A
/// provider list is not a substitute — the same provider carries good and bad
/// releases, and it is the release that is being refused.</para>
///
/// <para>Terms are matched case-insensitively as substrings of the release
/// name. That is deliberately blunt: a regular expression here would be a
/// second matcher language beside the one release rules already have, and this
/// is a filter rather than a rule engine.</para>
/// </summary>
public sealed record SubtitleNamePolicy(
    IReadOnlyList<string>? MustContain = null,
    IReadOnlyList<string>? MustNotContain = null)
{
    [JsonIgnore]
    public bool IsEnabled => (MustContain?.Count ?? 0) > 0 || (MustNotContain?.Count ?? 0) > 0;

    /// <summary>
    /// Whether this release name is acceptable.
    ///
    /// <para>A name Deluno does not have is not a reason to refuse: several
    /// providers return a subtitle with no release name at all, and refusing
    /// those would silently empty the candidate list for anybody who typed one
    /// must-contain term.</para>
    /// </summary>
    public bool Accepts(string? releaseName)
    {
        if (!IsEnabled) return true;

        var name = releaseName?.Trim();
        if (string.IsNullOrEmpty(name)) return true;

        foreach (var term in MustNotContain ?? [])
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var required = MustContain ?? [];
        if (required.Count == 0) return true;

        // Any, not all. "Only from NTb or FLUX" is the question people ask;
        // requiring every term at once would accept almost nothing.
        foreach (var term in required)
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>Canonical persistence for a subtitle name policy.</summary>
public static class SubtitleNamePolicyCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Trims, lowercases, de-duplicates and orders both lists, so the same
    /// intent typed two ways is stored one way.
    /// </summary>
    public static SubtitleNamePolicy? Normalize(SubtitleNamePolicy? policy)
    {
        if (policy is null) return null;

        var normalized = new SubtitleNamePolicy(
            Clean(policy.MustContain),
            Clean(policy.MustNotContain));
        return normalized.IsEnabled ? normalized : null;
    }

    private static IReadOnlyList<string>? Clean(IReadOnlyList<string>? terms)
    {
        if (terms is null) return null;

        var cleaned = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToArray();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public static string? Serialize(SubtitleNamePolicy? policy)
    {
        var normalized = Normalize(policy);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static SubtitleNamePolicy? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return Normalize(JsonSerializer.Deserialize<SubtitleNamePolicy>(json, JsonOptions));
        }
        catch (JsonException)
        {
            // A policy nobody can read is not a policy. Refusing every
            // subtitle because one stored row is malformed would be the worse
            // of the two failures.
            return null;
        }
    }
}
