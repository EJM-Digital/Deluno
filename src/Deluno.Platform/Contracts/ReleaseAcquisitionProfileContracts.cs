namespace Deluno.Platform.Contracts;

/// <summary>
/// A term-level preference attached to a release profile. Positive scores
/// prefer a matching release; negative scores make it less attractive. Hard
/// exclusions belong in <see cref="ReleaseProfileItem.MustNotContain"/> so a
/// user can tell the difference between "avoid" and "never".
/// </summary>
public sealed record ReleaseTermScore(string Term, int Score);

/// <summary>
/// The acquisition rules that apply to titles carrying one tag. The raw text
/// fields intentionally remain editable text: comma/newline separated terms
/// are easy to migrate from Radarr/Sonarr and preserve the user's vocabulary.
/// </summary>
public sealed record ReleaseProfileItem(
    string Id,
    string Name,
    string TagName,
    string PreferredProtocol,
    int UsenetDelayMinutes,
    int TorrentDelayMinutes,
    string MustContain,
    string MustNotContain,
    IReadOnlyList<ReleaseTermScore> PreferredTerms,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>A disabled profile is skipped entirely during acquisition evaluation.</summary>
    bool Enabled,
    /// <summary>
    /// Indexer ids this profile is scoped to. Empty means every indexer,
    /// matching every profile's behaviour before scoping existed.
    /// </summary>
    IReadOnlyList<string> IndexerIds,
    /// <summary>
    /// #992: skip this profile's own acquisition delay once a release is
    /// already the best quality its title's quality profile allows, over
    /// the protocol preferred here. Defaults off, matching Radarr's delay
    /// profile before either of its bypass toggles is turned on, and every
    /// profile that existed before this column did.
    /// </summary>
    bool BypassIfHighestQuality = false,
    /// <summary>
    /// #994: every tag this rule applies to. A title carrying any one of them
    /// matches, the way a Radarr/Sonarr profile linked to several tags does.
    /// Empty means the rule has no tag (the default rule). <see cref="TagName"/>
    /// stays the first of these for older callers.
    /// </summary>
    IReadOnlyList<string>? TagNames = null,
    /// <summary>#995: place in the list of rules; the lowest number is the top rule.</summary>
    int Position = 0)
{
    /// <summary>The tags this rule applies to, whichever way they were stored.</summary>
    public IReadOnlyList<string> AppliesToTags
        => TagNames is { Count: > 0 }
            ? TagNames
            : string.IsNullOrWhiteSpace(TagName) ? [] : [TagName.Trim()];
}

public sealed record CreateReleaseProfileRequest(
    string? Name,
    string? TagName,
    string? PreferredProtocol,
    int? UsenetDelayMinutes,
    int? TorrentDelayMinutes,
    string? MustContain,
    string? MustNotContain,
    IReadOnlyList<ReleaseTermScore>? PreferredTerms,
    bool? Enabled = null,
    IReadOnlyList<string>? IndexerIds = null,
    bool? BypassIfHighestQuality = null,
    IReadOnlyList<string>? TagNames = null);

/// <summary>
/// PUT is a full edit of the profile. Empty strings and an empty term list
/// intentionally clear the corresponding rule; this avoids a patch where a
/// null can mean both "leave it" and "remove it".
/// </summary>
public sealed record UpdateReleaseProfileRequest(
    string? Name,
    string? TagName,
    string? PreferredProtocol,
    int? UsenetDelayMinutes,
    int? TorrentDelayMinutes,
    string? MustContain,
    string? MustNotContain,
    IReadOnlyList<ReleaseTermScore>? PreferredTerms,
    bool? Enabled = null,
    IReadOnlyList<string>? IndexerIds = null,
    bool? BypassIfHighestQuality = null,
    IReadOnlyList<string>? TagNames = null);

/// <summary>
/// #995: how several matching release rules are used. <c>combine</c> is what
/// Deluno has always done - every matching rule applies at once. <c>first-match</c>
/// is the Radarr/Sonarr delay-profile way: the rules sit in a list, the top rule
/// that matches the title's tags is the only one used, and the rule with no tag
/// is the default that applies when none of the others match. A missing setting
/// means <c>combine</c>, so an install that upgrades keeps behaving as before.
/// </summary>
public static class ReleaseRuleModes
{
    public const string Combine = "combine";
    public const string FirstMatch = "first-match";
    public const string SettingKey = "releaseRules.mode";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), FirstMatch, StringComparison.OrdinalIgnoreCase) ? FirstMatch : Combine;
}

public sealed record ReleaseRuleSettings(string Mode);

/// <summary>The new order of the rules, top rule first. Ids left out keep their place after these.</summary>
public sealed record ReorderReleaseProfilesRequest(IReadOnlyList<string>? Ids);

public static class AcquisitionSearchKinds
{
    public const string Automatic = "automatic";
    public const string Interactive = "interactive";

    public static string Normalize(string? value)
        => string.Equals(value, Interactive, StringComparison.OrdinalIgnoreCase)
            ? Interactive
            : Automatic;
}

public static class IndexerProtocolKinds
{
    public const string Usenet = "usenet";
    public const string Torrent = "torrent";

    public static string FromIndexerProtocol(string? protocol)
        => string.Equals(protocol, "newznab", StringComparison.OrdinalIgnoreCase)
            ? Usenet
            : Torrent;
}
