using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Quality;

/// <summary>
/// How big a file of a given tier should be, <b>for this profile</b>.
///
/// <para>#394: nothing in Quality &amp; Release is shared. Anime at 1080p and a
/// film at 1080p are not the same number of gigabytes, and one range for both
/// cannot be right for either — so the answer belongs to the profile, beside
/// the other six answers, rather than to a table every profile reads.</para>
///
/// <para><b>Nothing is inherited.</b> A profile that has said nothing about a
/// tier is not falling back to a shared setting; it simply has no size opinion
/// about that tier, and any size passes. What <see cref="QualityTypicalSizes"/>
/// provides is not a fallback value but the band drawn behind the slider, so
/// somebody choosing 2–5 GB for anime 1080p can see where films of that tier
/// normally land while they do it.</para>
/// </summary>
public sealed record ProfileSizeRule(
    string Quality,
    double MinGb,
    double MaxGb,
    double MinMb,
    double MaxMb)
{
    /// <summary>Zero or less means "no ceiling", the same convention the quality model used.</summary>
    [JsonIgnore]
    public bool HasFilmCeiling => MaxGb > 0;

    [JsonIgnore]
    public bool HasEpisodeCeiling => MaxMb > 0;
}

public static class ProfileSizeRulesCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One rule per tier, trimmed, ordered, and with reversed handles put back
    /// the right way round — dragging the maximum below the minimum is a thing
    /// a slider lets you do, and storing it would refuse every release for that
    /// tier without saying so.
    /// </summary>
    public static IReadOnlyList<ProfileSizeRule> Normalize(IReadOnlyList<ProfileSizeRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return [];
        }

        return rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Quality))
            .GroupBy(rule => rule.Quality.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rule = group.Last() with { Quality = group.Key.Trim() };
                var minGb = Math.Max(0, rule.MinGb);
                var maxGb = Math.Max(0, rule.MaxGb);
                var minMb = Math.Max(0, rule.MinMb);
                var maxMb = Math.Max(0, rule.MaxMb);

                return rule with
                {
                    MinGb = maxGb > 0 && minGb > maxGb ? maxGb : minGb,
                    MaxGb = maxGb > 0 && minGb > maxGb ? minGb : maxGb,
                    MinMb = maxMb > 0 && minMb > maxMb ? maxMb : minMb,
                    MaxMb = maxMb > 0 && minMb > maxMb ? minMb : maxMb
                };
            })
            .OrderBy(rule => rule.Quality, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProfileSizeRule? For(IReadOnlyList<ProfileSizeRule>? rules, string? quality)
        => string.IsNullOrWhiteSpace(quality)
            ? null
            : rules?.FirstOrDefault(rule =>
                string.Equals(rule.Quality, quality.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Serialize(IReadOnlyList<ProfileSizeRule>? rules)
    {
        var normalized = Normalize(rules);
        return normalized.Count == 0 ? string.Empty : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static IReadOnlyList<ProfileSizeRule> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<IReadOnlyList<ProfileSizeRule>>(json, JsonOptions));
        }
        catch (JsonException)
        {
            // Rules nobody can read are no rules. Refusing every release
            // because one stored row is malformed is the worse of the two
            // failures, and the profile still has its other six answers.
            return [];
        }
    }

    /// <summary>
    /// The rules a new profile starts with: the typical band for each tier it
    /// allows.
    ///
    /// <para>Not inheritance — these are written into the profile and are its
    /// own from that moment. A slider has to have a position, and the position
    /// where files of that tier actually land is the right one to start at.</para>
    /// </summary>
    public static IReadOnlyList<ProfileSizeRule> StartingRulesFor(IEnumerable<string> allowedQualities)
        => Normalize(allowedQualities
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Select(quality =>
            {
                var (minGb, maxGb) = QualityTypicalSizes.FilmSizeGb(quality);
                var (minMb, maxMb) = QualityTypicalSizes.EpisodeSizeMb(quality);
                return new ProfileSizeRule(quality.Trim(), minGb, maxGb, minMb, maxMb);
            })
            .ToArray());
}
