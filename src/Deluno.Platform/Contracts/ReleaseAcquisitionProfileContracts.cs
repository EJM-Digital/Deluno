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
    DateTimeOffset UpdatedUtc);

public sealed record CreateReleaseProfileRequest(
    string? Name,
    string? TagName,
    string? PreferredProtocol,
    int? UsenetDelayMinutes,
    int? TorrentDelayMinutes,
    string? MustContain,
    string? MustNotContain,
    IReadOnlyList<ReleaseTermScore>? PreferredTerms);

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
    IReadOnlyList<ReleaseTermScore>? PreferredTerms);

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
