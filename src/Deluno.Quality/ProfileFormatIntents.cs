using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality;

/// <summary>
/// How much <b>this profile</b> cares about each preference it selected.
///
/// <para>#394: a custom format carried one score globally, so a profile could
/// choose whether to care about HDR10 and never how much. Two shelves that both
/// want HDR could not disagree about whether it is a nice-to-have or the whole
/// point — which is the same complaint as size, one level down.</para>
///
/// <para><b>Words, not a number.</b> The five answers are the vocabulary
/// #382 settled on for release rules, and reusing it is the point: an owner who
/// has read one of these screens has read both. A per-profile <i>score</i> would
/// have been easier to store and would have put an unbounded number back on the
/// one surface #353 removed it from.</para>
/// </summary>
public static class ProfileFormatIntents
{
    public const string MustNotHave = "blocked";
    public const string Avoid = "avoid";
    public const string DoNotCare = "neutral";
    public const string Prefer = "prefer";
    public const string StronglyPrefer = "strong-prefer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Known =
        [MustNotHave, Avoid, DoNotCare, Prefer, StronglyPrefer];

    public static bool IsKnown(string? intent)
        => intent is not null && Known.Contains(intent.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// The typed intent this answer becomes.
    ///
    /// <para><c>Avoid</c> and <c>Prefer</c> both land on <see cref="PreferenceIntent.Ranked"/>
    /// because the engine ranks in one direction and the sign lives in the
    /// level order, not in a second intent. <c>Strongly prefer</c> is
    /// <see cref="PreferenceIntent.Ranked"/> too and differs by driving upgrades
    /// — which is what "can justify replacing a file you already have" means.</para>
    /// </summary>
    public static PreferenceIntent ToPreferenceIntent(string? intent)
        => Normalize(intent) switch
        {
            MustNotHave => PreferenceIntent.Forbidden,
            DoNotCare => PreferenceIntent.Neutral,
            _ => PreferenceIntent.Ranked
        };

    /// <summary>Whether this answer is strong enough to justify replacing a held file.</summary>
    public static bool DrivesUpgrade(string? intent) => Normalize(intent) == StronglyPrefer;

    /// <summary>Whether this answer refuses the release outright.</summary>
    public static bool Refuses(string? intent) => Normalize(intent) == MustNotHave;

    public static string Normalize(string? intent)
    {
        var trimmed = intent?.Trim().ToLowerInvariant();
        return IsKnown(trimmed) ? trimmed! : DoNotCare;
    }

    /// <summary>
    /// What the guide's own score means, so a profile that has said nothing
    /// starts from the recommendation rather than from "do not care".
    ///
    /// <para>The thresholds are the ones the rules list already reads scores by,
    /// named once here so the list and the profile cannot disagree about what
    /// -10000 or 500 means.</para>
    /// </summary>
    public static string FromGuideScore(int score)
        => score switch
        {
            <= -10000 => MustNotHave,
            < 0 => Avoid,
            0 => DoNotCare,
            >= 500 => StronglyPrefer,
            _ => Prefer
        };

    public static string Serialize(IReadOnlyDictionary<string, string>? intents)
    {
        var normalized = NormalizeAll(intents);
        return normalized.Count == 0 ? string.Empty : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return NormalizeAll(JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions));
        }
        catch (JsonException)
        {
            // An unreadable answer is no answer. Every selected preference then
            // starts from the guide's own recommendation, which is where a
            // profile that had never answered would start anyway.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Named apart from the single-answer <see cref="Normalize(string?)"/> on
    /// purpose: two overloads that mean different things and both accept null
    /// are a call site the compiler cannot resolve and a reader cannot either.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NormalizeAll(IReadOnlyDictionary<string, string>? intents)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (formatId, intent) in intents ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(formatId) && IsKnown(intent))
            {
                normalized[formatId.Trim()] = Normalize(intent);
            }
        }

        return normalized;
    }
}
