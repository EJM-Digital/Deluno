using System.Text.Json;

namespace Deluno.Quality;

/// <summary>
/// The three TRaSH-style score guardrails a profile can hold: the minimum
/// custom-format score a release needs to be grabbed at all, the score at
/// which Deluno stops looking for something better, and how much a candidate
/// must gain over the installed file before it counts as an upgrade.
///
/// <para>#778: the TRaSH profile definitions Deluno ships and syncs from
/// (<c>trash-guide-source-inventory.json</c>) already carry
/// <c>minFormatScore</c>, <c>cutoffFormatScore</c> and
/// <c>minUpgradeFormatScore</c> for every pinned profile, but nothing in
/// Deluno read them: a profile had no minimum, no cutoff and no upgrade-gain
/// floor of its own, so that guidance was imported and dropped.</para>
///
/// <para>Every field is nullable and null is the whole point: it means this
/// profile has no opinion, which is exactly the behaviour every profile had
/// before it could hold one. A migrated install that never asked for these
/// keeps deciding exactly as it did.</para>
/// </summary>
public sealed record ProfileScoreLimits(
    /// <summary>
    /// "Never grab copies TRaSH rates as bad." A release scoring below this is
    /// refused outright. Null or non-positive means no floor.
    /// </summary>
    int? MinFormatScore = null,
    /// <summary>
    /// "Stop upgrading once a copy is: Good / Great / The best available."
    /// Once the installed file's score meets or exceeds this, Deluno stops
    /// chasing something better. Null means never stop on score alone.
    /// </summary>
    int? CutoffFormatScore = null,
    /// <summary>
    /// "Only replace a file when the new copy is: A little better / Clearly
    /// better / Much better." The minimum score gain a same-quality candidate
    /// must offer over the installed file to count as an upgrade. Null means
    /// any positive gain counts, which is the behaviour before this existed.
    /// </summary>
    int? MinUpgradeFormatScore = null)
{
    public bool IsEmpty => MinFormatScore is null && CutoffFormatScore is null && MinUpgradeFormatScore is null;

    public ProfileScoreLimits Normalize()
        => this with
        {
            MinFormatScore = MinFormatScore is > 0 ? MinFormatScore : null,
            // A cutoff or upgrade-gain of zero or less would mean "always met"
            // or "any change at all counts", which is not a floor anybody
            // meant to set - it is read as no opinion, same as null.
            CutoffFormatScore = CutoffFormatScore is > 0 ? CutoffFormatScore : null,
            MinUpgradeFormatScore = MinUpgradeFormatScore is > 0 ? MinUpgradeFormatScore : null
        };
}

public static class ProfileScoreLimitsCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProfileScoreLimits? limits)
    {
        var normalized = limits?.Normalize();
        return normalized is null || normalized.IsEmpty
            ? string.Empty
            : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static ProfileScoreLimits? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileScoreLimits>(json, JsonOptions)?.Normalize();
        }
        catch (JsonException)
        {
            // Limits nobody can read are no limits. A profile whose stored
            // answer is malformed falls back to having no score opinion rather
            // than silently refusing every release below a floor nobody set.
            return null;
        }
    }
}
