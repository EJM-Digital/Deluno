namespace Deluno.Contracts;

/// <summary>
/// What happened to a file, in the vocabulary Plex/Jellyfin/Emby's own refresh
/// APIs use (#662). <c>Created</c> covers both a fresh import and an upgrade's
/// new file; <c>Deleted</c> covers a replaced or removed file.
/// </summary>
public enum MediaServerChangeKind
{
    Created,
    Modified,
    Deleted
}

/// <summary>One file a media server should be told about, and what happened to it.</summary>
public sealed record MediaServerPathChange(string Path, MediaServerChangeKind Kind);

/// <summary>
/// The job type every media-server notify runs under (#662). One lane, shared
/// by import, upgrade, season-pack, rename and manual removal, so a slow or
/// down server queues behind itself and never behind - or in front of - the
/// import it followed.
/// </summary>
public static class MediaServerJobTypes
{
    public const string Refresh = "media-servers.refresh";

    /// <summary>
    /// A failed refresh's own retry (#920): re-tries one server's backlog with
    /// backoff until it answers, then re-enqueues itself if it is still down.
    /// Never the import path - a slow or dead server never holds up or re-runs
    /// the import that already landed.
    /// </summary>
    public const string RefreshRetry = "media-servers.refresh-retry";
}

/// <summary>The payload a <see cref="MediaServerJobTypes.Refresh"/> job carries.</summary>
/// <param name="MediaType"><c>movies</c> or <c>tv</c>, so a client can pick the right library section.</param>
public sealed record MediaServerRefreshJobPayload(string MediaType, IReadOnlyList<MediaServerPathChange> Changes);

/// <summary>The payload a <see cref="MediaServerJobTypes.RefreshRetry"/> job carries: which server and library to retry.</summary>
public sealed record MediaServerRefreshRetryJobPayload(string MediaServerId, string MediaType);

/// <summary>
/// A server and media type's missed refreshes, tracked since the first one
/// failed (#920): what System → Health shows as one line ("Plex hasn't
/// answered since 21:04"), and what a successful retry sends in one go to
/// catch up. Kept per media type because a client's own refresh call is
/// per-library, so a stuck movies backlog must never hold a TV catch-up
/// hostage or the other way round.
/// </summary>
public sealed record MediaServerRefreshBacklogItem(
    string MediaServerId,
    string MediaType,
    DateTimeOffset FirstFailedUtc,
    DateTimeOffset NextAttemptUtc,
    int AttemptCount,
    IReadOnlyList<MediaServerPathChange> Changes,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// How long to wait before the next retry: minutes at first, then less often,
/// until the server answers (#920 acceptance). Capped rather than growing
/// forever, because "less often" still means "still trying".
/// </summary>
public static class MediaServerRefreshBackoff
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2)
    ];

    /// <param name="attemptCount">Failures so far, including the one that just happened. 1 is the first.</param>
    public static TimeSpan Next(int attemptCount)
        => Steps[Math.Clamp(attemptCount, 1, Steps.Length) - 1];
}
