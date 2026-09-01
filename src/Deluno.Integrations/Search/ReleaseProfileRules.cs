using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Search;

/// <summary>
/// The effective rules for one search. Profiles are combined conservatively:
/// all matching hard terms apply, the longest delay wins, and positive term
/// scores add together. That makes adding a second tag unable to silently
/// weaken a rule that was already protecting a title.
/// </summary>
public sealed record EffectiveReleaseProfileRules(
    string PreferredProtocol,
    int DelayMinutes,
    IReadOnlyList<string> MustContain,
    IReadOnlyList<string> MustNotContain,
    IReadOnlyList<ReleaseTermScore> PreferredTerms,
    int PreferredProtocolScore)
{
    public static EffectiveReleaseProfileRules Empty { get; } = new(
        PreferredProtocol: "any",
        DelayMinutes: 0,
        MustContain: [],
        MustNotContain: [],
        PreferredTerms: [],
        PreferredProtocolScore: 0);
}

public static class ReleaseProfileRuleEvaluator
{
    public static EffectiveReleaseProfileRules Combine(
        IReadOnlyList<ReleaseProfileItem>? profiles,
        string indexerProtocol)
    {
        if (profiles is not { Count: > 0 })
        {
            return EffectiveReleaseProfileRules.Empty;
        }

        var protocolKind = IndexerProtocolKinds.FromIndexerProtocol(indexerProtocol);
        var selectedProtocol = profiles
            .Select(profile => profile.PreferredProtocol.Trim().ToLowerInvariant())
            .Where(protocol => protocol is "usenet" or "torrent")
            .GroupBy(protocol => protocol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "any";

        var protocolScore = profiles.Any(profile =>
            string.Equals(profile.PreferredProtocol, protocolKind, StringComparison.OrdinalIgnoreCase))
            ? 75
            : 0;

        var terms = profiles
            .SelectMany(profile => profile.PreferredTerms ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Term))
            .GroupBy(item => item.Term.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReleaseTermScore(group.Key, group.Sum(item => item.Score)))
            .Where(item => item.Score != 0)
            .OrderBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new EffectiveReleaseProfileRules(
            PreferredProtocol: selectedProtocol,
            DelayMinutes: profiles.Max(profile => protocolKind == IndexerProtocolKinds.Usenet
                ? profile.UsenetDelayMinutes
                : profile.TorrentDelayMinutes),
            MustContain: profiles.SelectMany(profile => SplitTerms(profile.MustContain)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            MustNotContain: profiles.SelectMany(profile => SplitTerms(profile.MustNotContain)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PreferredTerms: terms,
            PreferredProtocolScore: protocolScore);
    }

    public static IReadOnlyList<string> SplitTerms(string? value)
        => (value ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool ContainsTerm(string value, string term)
        => value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
}
