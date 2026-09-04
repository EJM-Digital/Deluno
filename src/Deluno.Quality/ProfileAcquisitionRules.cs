using System.Text.Json;

namespace Deluno.Quality;

/// <summary>
/// How <b>this profile</b> wants a release fetched, and which words it insists
/// on or refuses in a release name.
///
/// <para>#394: acquisition rules were keyed by tag, so protocol preference and
/// delays sat apart from the seven answers they belong beside — and a profile
/// could not want usenet for anime and torrents for films without inventing a
/// tag to say so. The answers move to the profile, where the rest of what it
/// wants already lives.</para>
///
/// <para><b>Tag-keyed rules still work.</b> They are combined with this one
/// rather than replaced, because a tag is a real way to say "these six shelves
/// share a rule" and removing it would take something away to give something
/// back. What changes is that a profile no longer <i>needs</i> a tag to have an
/// opinion.</para>
///
/// <para>There is deliberately no per-term score here. The old release profile
/// carried one, the typed plan already ignores it, and steps 5 and 7 answer
/// "who from" and "what never" in words.</para>
/// </summary>
public sealed record ProfileAcquisitionRules(
    /// <summary>"usenet", "torrent", or "any" for no preference.</summary>
    string PreferredProtocol = "any",
    int UsenetDelayMinutes = 0,
    int TorrentDelayMinutes = 0,
    /// <summary>Comma-separated words a release name must carry.</summary>
    string MustContain = "",
    /// <summary>Comma-separated words a release name must not carry.</summary>
    string MustNotContain = "")
{
    public bool IsEmpty
        => Normalized(PreferredProtocol) == "any"
            && UsenetDelayMinutes <= 0
            && TorrentDelayMinutes <= 0
            && string.IsNullOrWhiteSpace(MustContain)
            && string.IsNullOrWhiteSpace(MustNotContain);

    private static string Normalized(string? protocol)
    {
        var trimmed = protocol?.Trim().ToLowerInvariant();
        return trimmed is "usenet" or "torrent" ? trimmed : "any";
    }

    public ProfileAcquisitionRules Normalize()
        => this with
        {
            PreferredProtocol = Normalized(PreferredProtocol),
            // Negative delays would read as "fetch it before it exists". Zero
            // is the honest floor and already means "no delay".
            UsenetDelayMinutes = Math.Max(0, UsenetDelayMinutes),
            TorrentDelayMinutes = Math.Max(0, TorrentDelayMinutes),
            MustContain = CleanTerms(MustContain),
            MustNotContain = CleanTerms(MustNotContain)
        };

    private static string CleanTerms(string? terms)
        => string.Join(", ", (terms ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(term => term, StringComparer.OrdinalIgnoreCase));
}

public static class ProfileAcquisitionRulesCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProfileAcquisitionRules? rules)
    {
        var normalized = rules?.Normalize();
        return normalized is null || normalized.IsEmpty
            ? string.Empty
            : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static ProfileAcquisitionRules? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileAcquisitionRules>(json, JsonOptions)?.Normalize();
        }
        catch (JsonException)
        {
            // Rules nobody can read are no rules. A profile whose stored answer
            // is malformed falls back to having no acquisition opinion rather
            // than refusing every release with a must-contain nobody can see.
            return null;
        }
    }
}
