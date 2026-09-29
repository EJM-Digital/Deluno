using Deluno.Platform.Contracts;
using Deluno.Quality;

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
    int PreferredProtocolScore,
    /// <summary>
    /// #992: skip the acquisition delay once a release is already the best
    /// quality the governing profile allows. Combined the same conservative
    /// way as the delay itself: every applicable profile must opt in, so one
    /// profile leaving it off (the default) keeps the wait in force.
    /// </summary>
    bool BypassIfHighestQuality = false)
{
    public static EffectiveReleaseProfileRules Empty { get; } = new(
        PreferredProtocol: "any",
        DelayMinutes: 0,
        MustContain: [],
        MustNotContain: [],
        PreferredTerms: [],
        PreferredProtocolScore: 0,
        BypassIfHighestQuality: false);
}

public static class ReleaseProfileRuleEvaluator
{
    /// <summary>
    /// A disabled profile never applies, and a profile scoped to specific
    /// indexers only applies to a release from one of them; an unscoped
    /// (empty) list keeps applying everywhere, exactly as it always has (#770).
    /// </summary>
    public static bool AppliesToIndexer(ReleaseProfileItem profile, string? indexerId)
    {
        if (!profile.Enabled)
        {
            return false;
        }

        if (profile.IndexerIds is not { Count: > 0 } scopedIndexerIds)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(indexerId)
            && scopedIndexerIds.Contains(indexerId, StringComparer.OrdinalIgnoreCase);
    }

    public static EffectiveReleaseProfileRules Combine(
        IReadOnlyList<ReleaseProfileItem>? allProfiles,
        string indexerProtocol,
        string? indexerId = null)
    {
        var profiles = (allProfiles ?? [])
            .Where(profile => AppliesToIndexer(profile, indexerId))
            .ToArray();
        if (profiles is not { Length: > 0 })
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
            PreferredProtocolScore: protocolScore,
            BypassIfHighestQuality: profiles.All(profile => profile.BypassIfHighestQuality));
    }

    public static IReadOnlyList<string> SplitTerms(string? value)
        => ReleaseTermPattern.Split(value);

    /// <summary>
    /// Plain words match anywhere in the name; a term written /like this/i is a
    /// pattern (#996), the same rule Sonarr's term matcher uses.
    /// </summary>
    public static bool ContainsTerm(string value, string term)
        => ReleaseTermPattern.IsMatch(value, term);
}
