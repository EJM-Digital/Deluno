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

/// <summary>One movie or show as a media server sees it.</summary>
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
    string? SectionId = null);

/// <summary>What removing an item from a server did.</summary>
public sealed record MediaServerRemoval(string Message);

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

    /// <param name="mediaType"><c>movies</c> or <c>tv</c>.</param>
    Task<MediaServerAnswer<IReadOnlyList<MediaServerItem>>> ListItemsAsync(
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
