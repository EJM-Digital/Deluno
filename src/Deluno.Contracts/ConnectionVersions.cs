namespace Deluno.Contracts;

/// <summary>
/// The version one connection last reported (#1117), as stored with its latest check.
/// <c>Project</c> names whose releases to compare it with (for example "qbittorrent"); null when nothing
/// publishes a feed for it.
/// </summary>
public sealed record ConnectionVersionRow(
    string Kind,
    string Id,
    string? Project,
    string Version,
    DateTimeOffset ReadUtc,
    string? DismissedVersion = null);

/// <summary>What the Connections screens show beside a connection's name: its version, and the newer stable one when it is out.</summary>
public sealed record ConnectionVersionItem(
    string Kind,
    string Id,
    string Version,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl);

/// <summary>Remembers the version a connection last reported, so a screen never waits on the connection.</summary>
public interface IConnectionVersionStore
{
    /// <summary>
    /// Stores what a check read. A read that found nothing (<paramref name="version"/> null or blank) changes
    /// nothing: a failed or unrecognised answer is "unknown", never an error and never a wiped number.
    /// </summary>
    Task RecordAsync(string kind, string id, string? project, string? version, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionVersionRow>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// #1118: remembers that the person dismissed the notice for <paramref name="version"/> on this connection. Only a
    /// newer version brings the notice back. False when nothing is stored for the connection.
    /// </summary>
    Task<bool> DismissAsync(string kind, string id, string version, CancellationToken cancellationToken);
}

/// <summary>The newest stable release of a project, and where to read about it.</summary>
public sealed record LatestRelease(string Version, string? Url);

/// <summary>
/// The newest stable release of each project Deluno connects to, from its public release feed (#1117).
/// </summary>
public interface ILatestReleaseService
{
    /// <summary>
    /// What is known right now, without waiting: null when the feed has not answered yet or the project has
    /// none. When the answer is more than a day old this starts a refresh in the background.
    /// </summary>
    LatestRelease? Peek(string project);
}
