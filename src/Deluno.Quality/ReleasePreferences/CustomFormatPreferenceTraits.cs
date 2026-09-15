using System.Security.Cryptography;
using System.Text;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// The bounded plan-scoped vocabulary for a saved custom-format matcher.
/// Arbitrary matcher text never becomes a trait id: the immutable rule id is
/// hashed into exactly two closed-world states, and plan validation requires
/// both states to be backed by the matching provenance record.
/// </summary>
public static class CustomFormatPreferenceTraits
{
    public const string SourceKind = "deluno-custom-format";
    public const string MappingVersion = "deluno-custom-format/v1";
    private const string Prefix = "custom-format.";

    public static string Family(string ruleId) => $"{Prefix}{Key(ruleId)}";

    public static string Matched(string ruleId) => $"{Prefix}{Key(ruleId)}.matched";

    public static string NotMatched(string ruleId) => $"{Prefix}{Key(ruleId)}.not-matched";

    public static bool IsDynamic(string? traitId)
        => traitId?.Trim().StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static bool IsBackedByPlan(ReleasePreferencePlan plan, string traitId)
    {
        var normalized = traitId.Trim().ToLowerInvariant();
        return (plan.Sources ?? []).Any(source =>
        {
            if (!string.Equals(source.SourceKind?.Trim(), SourceKind, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(source.SourceId)
                || string.IsNullOrWhiteSpace(source.MatcherDefinition)
                || !string.Equals(source.MappingVersion?.Trim(), MappingVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var ruleId = string.IsNullOrWhiteSpace(source.RuleId) ? source.SourceId : source.RuleId;
            var expected = new[] { Matched(ruleId!), NotMatched(ruleId!) };
            return expected.Contains(normalized, StringComparer.Ordinal)
                && (source.MappedTraitIds ?? []).Contains(normalized, StringComparer.OrdinalIgnoreCase);
        });
    }

    private static string Key(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        var normalized = ruleId.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return hash[..16];
    }
}
