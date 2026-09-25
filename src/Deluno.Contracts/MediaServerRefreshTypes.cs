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
}

/// <summary>The payload a <see cref="MediaServerJobTypes.Refresh"/> job carries.</summary>
/// <param name="MediaType"><c>movies</c> or <c>tv</c>, so a client can pick the right library section.</param>
public sealed record MediaServerRefreshJobPayload(string MediaType, IReadOnlyList<MediaServerPathChange> Changes);
