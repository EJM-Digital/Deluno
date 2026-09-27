namespace Deluno.Integrations.Subtitles;

/// <summary>
/// What a provider needs from you before it will answer.
///
/// <para>Declared per provider rather than assumed, because the honest answer
/// differs: Gestdown wants nothing, Podnapisi takes an optional account,
/// OpenSubtitles will not talk to you without one. A settings screen that shows
/// the same three boxes for all of them teaches nothing and looks broken on the
/// two that need none.</para>
/// </summary>
[Flags]
public enum SubtitleCredentialFields
{
    None = 0,
    Username = 1,
    Password = 2,
    ApiKey = 4
}

/// <summary>Which media a provider can actually answer for.</summary>
public enum SubtitleProviderScope
{
    Both,
    MoviesOnly,
    TvOnly
}

/// <summary>
/// The account details one provider holds, unprotected, at the moment of use.
///
/// <para>Never stored in this shape — the repository protects them on the way in
/// and unprotects on the way out, the same as an indexer's API key.</para>
/// </summary>
public sealed record SubtitleProviderCredentials(
    string? Username = null,
    string? Password = null,
    string? ApiKey = null)
{
    public static readonly SubtitleProviderCredentials None = new();

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Username)
        || !string.IsNullOrWhiteSpace(Password)
        || !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// One file to find subtitles for, described the way a provider asks about it.
///
/// <para>A film is a title and a year; an episode is a show, a season and a
/// number. Both also carry the release name where Deluno knows it, because
/// matching the release is the difference between a subtitle that is in time and
/// one that is forty seconds out — and that is the single most common reason
/// anybody touches a subtitle by hand.</para>
/// </summary>
/// <param name="Languages">
/// In the order the library asked for them. Providers are told all of them at
/// once where they can take a list, so one request answers "English, then
/// Spanish" rather than two.
/// </param>
public sealed record SubtitleSearchRequest(
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? ReleaseName,
    string? ImdbId,
    IReadOnlyList<string> Languages,
    bool IsEpisode)
{
    /// <summary>
    /// The words a provider is actually searched with.
    ///
    /// <para><c>Show S01E02</c> or <c>Title 1999</c>. Ported from Weir, which
    /// arrived at it the hard way: a bare show title returns the whole series and
    /// a bare film title returns every remake.</para>
    /// </summary>
    public string Query =>
        IsEpisode
            ? SeasonNumber is not null && EpisodeNumber is not null
                ? $"{Title} S{SeasonNumber.Value:00}E{EpisodeNumber.Value:00}"
                : Title
            : Year is not null
                ? $"{Title} {Year.Value}"
                : Title;
}

/// <summary>
/// One subtitle a provider says it has.
///
/// <para><see cref="DownloadToken"/> is whatever that provider needs handed back
/// to fetch this exact file — a URL for Gestdown, an id for Podnapisi, a file id
/// for OpenSubtitles. Opaque here on purpose: the alternative is a union of every
/// provider's addressing scheme in the shared type, which is the shape that
/// becomes unreadable at the fourth provider.</para>
/// </summary>
public sealed record SubtitleCandidate(
    string ProviderKey,
    string DownloadToken,
    string Language,
    bool HearingImpaired,
    bool Forced,
    string? ReleaseName = null,
    string? Uploader = null,
    int? DownloadCount = null);

/// <summary>
/// One subtitle source Deluno can ask.
///
/// <para><b>Stateless.</b> Credentials arrive per call rather than being held,
/// so a provider row that is edited takes effect on the next search without
/// anything being rebuilt, and a test can run against details that have not been
/// saved yet.</para>
///
/// <para><b>It does not decide anything.</b> Which languages are wanted, which
/// candidate wins and where the file lands are all decided once, outside, by
/// <c>SubtitleFetchService</c>. A provider that picked its own favourite would be
/// a second copy of the preference rule, and the eight of them would disagree.</para>
/// </summary>
public interface ISubtitleProvider
{
    /// <summary>Stable, and what the stored row names.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>What it is, in one line, for the screen that lists them.</summary>
    string Description { get; }

    SubtitleProviderScope Scope { get; }

    /// <summary>What it needs before it will answer. <see cref="SubtitleCredentialFields.None"/> means no account.</summary>
    SubtitleCredentialFields RequiredCredentials { get; }

    /// <summary>
    /// Whether the credentials it does take are optional.
    ///
    /// <para>Podnapisi answers anonymously and answers better signed in. Saying
    /// so on the screen is the difference between "this needs an account" and
    /// "an account gets you more", and a person deciding what to sign up for
    /// deserves the second sentence.</para>
    /// </summary>
    bool CredentialsOptional { get; }

    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken);

    /// <summary>
    /// The subtitle itself, as bytes.
    ///
    /// <para>A provider may hand back a zip; unwrapping it is the provider's job,
    /// because only it knows what its archives look like. What comes out is a
    /// single subtitle file.</para>
    /// </summary>
    Task<byte[]> DownloadAsync(
        SubtitleCandidate candidate,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken);
}

/// <summary>
/// Every provider Deluno ships, by key.
///
/// <para>Eleven, each with health and a test, each saying plainly what an
/// account buys you — rather than the fifty-plus Bazarr offers. DESIGN-002:
/// <i>"a provider that fails silently is worse than one that is absent."</i></para>
/// </summary>
public interface ISubtitleProviderRegistry
{
    IReadOnlyList<ISubtitleProvider> All { get; }

    ISubtitleProvider? Find(string? key);
}
