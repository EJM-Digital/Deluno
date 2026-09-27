using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// A media server's answer, or the fact that it could not give one (#493).
///
/// <para><b>This type exists for one reason.</b> An empty list from a server
/// that was asked means "nothing matches" - permission to proceed. An empty list
/// standing in for a server that could not be asked means the same thing to any
/// code that reads it, and that is how an unreachable server causes deletions.
/// Weir learned it the hard way (ADR-0015). So the two are different types:
/// code that wants the items has to take them out of <see cref="Reported"/>, and
/// cannot do that by accident with an <see cref="Unreachable"/>.</para>
/// </summary>
public abstract record MediaServerAnswer<T>
{
    private MediaServerAnswer()
    {
    }

    /// <summary>The server was asked and answered.</summary>
    public sealed record Reported(T Value) : MediaServerAnswer<T>;

    /// <summary>The server could not be asked: down, refused the token, or answered with something unreadable.</summary>
    public sealed record Unreachable(string Reason, IntegrationFailure? Failure = null) : MediaServerAnswer<T>;

    /// <summary>The server was reached but cannot answer this question.</summary>
    public sealed record NotSupported(string Reason) : MediaServerAnswer<T>;
}

/// <summary>
/// Whether an item has been watched, as far as the server can say.
/// </summary>
/// <param name="PlayedBy">Names of the users who finished it, when the server reports per user.</param>
/// <param name="PerUserKnown">
/// False for Plex, whose token speaks for one account: "not watched" there means
/// "not watched by that account", and a rule has to be able to say so.
/// </param>
public sealed record MediaServerWatchState(
    bool PlayedByAnyone,
    IReadOnlyList<string> PlayedBy,
    DateTimeOffset? LastPlayedUtc,
    bool PerUserKnown)
{
    public static readonly MediaServerWatchState Unplayed = new(false, [], null, true);
}

/// <summary>What one person on a server has done with an item (#494).</summary>
/// <param name="InProgress">Started and not finished: a play attempt.</param>
/// <param name="Favourite">Null where the server cannot say.</param>
/// <param name="Rating">The person's own score out of ten, when they gave one.</param>
public sealed record MediaServerViewer(
    string User,
    bool Played,
    int PlayCount,
    DateTimeOffset? LastPlayedUtc,
    bool InProgress,
    bool? Favourite,
    double? Rating = null);

/// <summary>Scores a server carries, each out of ten. Null is "the server has none".</summary>
public sealed record MediaServerRatings(
    double? Critic = null,
    double? Audience = null,
    double? Imdb = null,
    double? RottenTomatoesCritic = null,
    double? RottenTomatoesAudience = null,
    double? Tmdb = null)
{
    public static readonly MediaServerRatings None = new();
}

/// <summary>The video the server found in the file.</summary>
/// <param name="Resolution">As a person says it: <c>2160p</c>, <c>1080p</c>, <c>720p</c>, <c>480p</c>, <c>SD</c>.</param>
public sealed record MediaServerVideo(string? Resolution = null, long? BitrateKbps = null, string? Codec = null)
{
    public static readonly MediaServerVideo None = new();
}

/// <summary>Where an item sits in a server's library.</summary>
public static class MediaServerItemLevels
{
    public const string Movie = "movie";
    public const string Show = "show";
    public const string Season = "season";
    public const string Episode = "episode";
}

/// <summary>One movie, show, season or episode as a media server sees it.</summary>
/// <param name="ServerItemId">The server's own id: <c>ratingKey</c> on Plex, <c>Id</c> on Jellyfin and Emby.</param>
/// <param name="SectionId">Plex library section the item lives in; Jellyfin and Emby leave it null.</param>
public sealed record MediaServerItem(
    string ServerItemId,
    string MediaType,
    string Title,
    int? Year,
    string? TmdbId,
    string? ImdbId,
    string? TvdbId,
    IReadOnlyList<string> Paths,
    DateTimeOffset? AddedUtc,
    MediaServerWatchState Watch,
    string? SectionId = null)
{
    /// <summary>Movie, show, season or episode. A <c>tv</c> listing holds all three TV levels.</summary>
    public string Level { get; init; } = MediaType == "tv" ? MediaServerItemLevels.Show : MediaServerItemLevels.Movie;

    /// <summary>Plex's own <c>plex://</c> identity, which its watchlist is keyed by.</summary>
    public string? Guid { get; init; }

    /// <summary>The show a season or episode belongs to.</summary>
    public string? ShowServerItemId { get; init; }

    /// <summary>The season an episode belongs to.</summary>
    public string? SeasonServerItemId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>Release date, or an episode's air date.</summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>Labels on Plex, tags on Jellyfin and Emby.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public IReadOnlyList<string> Collections { get; init; } = [];

    public IReadOnlyList<string> Playlists { get; init; } = [];

    public MediaServerRatings Ratings { get; init; } = MediaServerRatings.None;

    public MediaServerVideo Video { get; init; } = MediaServerVideo.None;

    /// <summary>Everyone the server reports anything for. Plex reports its token's account only.</summary>
    public IReadOnlyList<MediaServerViewer> Viewers { get; init; } = [];

    /// <summary>Who has it on a watchlist. Null where the server has no watchlist to ask.</summary>
    public IReadOnlyList<string>? WatchlistedBy { get; init; }
}

/// <summary>
/// A server's whole library for one media type, and who uses it: "everyone has
/// watched it" needs the list of everyone.
/// </summary>
/// <param name="Users">Every enabled account; Plex's token account only.</param>
public sealed record MediaServerLibrary(IReadOnlyList<MediaServerItem> Items, IReadOnlyList<string> Users);

/// <summary>What removing an item from a server did.</summary>
public sealed record MediaServerRemoval(string Message);

/// <summary>
/// What telling a server about changed files did (#662).
/// </summary>
/// <param name="UsedFallbackScan">
/// True when no targeted per-path refresh could be sent - no section matched
/// the path on Plex, or the targeted call failed on Jellyfin/Emby - so a full
/// library scan was requested instead.
/// </param>
public sealed record MediaServerRefresh(string Message, bool UsedFallbackScan);

/// <summary>Whether a mapped server path is one the server can actually see (#662).</summary>
public sealed record MediaServerPathMappingTest(bool Found, string Message);

/// <summary>An item that belongs in a collection Deluno keeps on a server.</summary>
/// <param name="SectionId">Plex's library section; a Plex collection lives in one section.</param>
public sealed record MediaServerCollectionMember(string ItemId, string Level, string? SectionId = null);

/// <summary>
/// A collection Deluno owns on a server, and exactly what should be in it (#521).
/// No members means the collection should not exist.
/// </summary>
/// <param name="PromoteToHome">Show it on the home screen: Plex's own-home promotion, or Jellyfin/Emby's Favorites row (#854).</param>
public sealed record MediaServerCollectionRequest(
    string MediaType,
    string Name,
    string Description,
    IReadOnlyList<MediaServerCollectionMember> Members,
    bool PromoteToHome = false);

/// <summary>What bringing a collection into line did.</summary>
/// <param name="Skipped">Members this server cannot hold in a collection of this kind.</param>
public sealed record MediaServerCollectionSync(int Added, int Removed, int Members, int Skipped, bool Deleted);

/// <summary>
/// One implementation per kind of server. Plex, Jellyfin and Emby differ enough
/// - in how they authenticate, what "watched" means and how an item leaves the
/// library - that one generic client would be a switch statement pretending
/// otherwise.
/// </summary>
public interface IMediaServerClient
{
    string Kind { get; }

    Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken);

    /// <param name="mediaType">
    /// <c>movies</c>, or <c>tv</c> for every show, season and episode. A listing
    /// that could not be read completely is <see cref="MediaServerAnswer{T}.Unreachable"/>:
    /// half a library would read as "these were never watched".
    /// </param>
    Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(
        MediaServerConnection connection,
        string mediaType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes an item whose files Deluno has already taken away.
    ///
    /// <para>Never the server's own delete call. On Jellyfin and Emby that call
    /// deletes whatever is at the item's path, and on Plex it needs deletion
    /// switched on and does the same - neither is something Deluno should do
    /// to a folder it has already emptied into its recycle bin. Instead the
    /// server is told the files are gone, and drops the item the way it would
    /// after any deletion it noticed itself.</para>
    /// </summary>
    Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(
        MediaServerConnection connection,
        MediaServerItem item,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tells the server about files that were created, replaced or removed
    /// (#662) - the direct equivalent of Radarr's/Sonarr's Connect "On
    /// Import"/"On Upgrade"/"On Rename" events. A targeted refresh is
    /// preferred: Plex rescans the section that owns each distinct folder,
    /// Jellyfin/Emby are sent one <c>Library/Media/Updated</c> call carrying
    /// every change in the batch. When a targeted refresh cannot be sent -
    /// nothing on the server maps to the path - a full library scan is
    /// requested instead, and <see cref="MediaServerRefresh.UsedFallbackScan"/>
    /// says so.
    /// </summary>
    /// <param name="mediaType"><c>movies</c> or <c>tv</c>, to pick the right Plex section type.</param>
    /// <param name="changes">
    /// Paths already translated into this server's own filesystem view (path
    /// mapping applied by the caller) - a season pack sends one entry per
    /// file, not one call per file.
    /// </param>
    Task<MediaServerAnswer<MediaServerRefresh>> RefreshAsync(
        MediaServerConnection connection,
        string mediaType,
        IReadOnlyList<MediaServerPathChange> changes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="serverPath"/> - a path already translated by a
    /// configured path mapping - is one the server can see on its own
    /// filesystem. Backs the "test" action on a path mapping (#662): the most
    /// common path-mapping mistake is a mapping that still points nowhere the
    /// server can reach.
    /// </summary>
    Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(
        MediaServerConnection connection,
        string serverPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes the named collection hold exactly the requested members: creates
    /// it, adds what is missing, takes out what should not be there (including
    /// anything a person added by hand), and deletes it when nothing is left.
    /// Only ever the collection itself - never an item in it.
    /// </summary>
    Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(
        MediaServerConnection connection,
        MediaServerCollectionRequest request,
        CancellationToken cancellationToken);
}

public interface IMediaServerClientRegistry
{
    IMediaServerClient? Find(string kind);
}

public sealed class MediaServerClientRegistry(IEnumerable<IMediaServerClient> clients) : IMediaServerClientRegistry
{
    public IMediaServerClient? Find(string kind)
        => clients.FirstOrDefault(client => string.Equals(client.Kind, kind, StringComparison.OrdinalIgnoreCase));
}
