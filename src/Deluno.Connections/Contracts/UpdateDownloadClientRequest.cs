namespace Deluno.Connections.Contracts;

/// <summary>
/// Patch-style request — null values mean "leave unchanged".
/// Sent by PUT /api/download-clients/{id}
/// </summary>
public sealed record UpdateDownloadClientRequest(
    string? Name,
    string? Protocol,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    string? EndpointUrl,
    string? MoviesCategory,
    string? TvCategory,
    string? CategoryTemplate,
    int? Priority,
    bool? IsEnabled,
    bool? UseSsl = null,
    string? UrlBase = null,
    bool? AddPaused = null,
    /// <summary>Comma-separated tag names. Null leaves the saved tags unchanged; an empty string clears them.</summary>
    string? Tags = null,
    /// <summary>Remove a download from this client once Deluno has imported it successfully (#806). Null leaves the saved value unchanged.</summary>
    bool? RemoveCompleted = null,
    /// <summary>Remove a download from this client once Deluno records it as failed (#806). Null leaves the saved value unchanged.</summary>
    bool? RemoveFailed = null,
    /// <summary>#811 (qBittorrent only): skip hash-checking a grab at add time. Null leaves the saved value unchanged.</summary>
    bool? SkipHashCheck = null,
    /// <summary>#811 (qBittorrent only): download pieces in order rather than rarest-first. Null leaves the saved value unchanged.</summary>
    bool? SequentialDownload = null,
    /// <summary>#811 (qBittorrent only): prioritize the first and last pieces. Null leaves the saved value unchanged.</summary>
    bool? FirstLastPiecePrio = null,
    /// <summary>#811 (qBittorrent only): ratio at which this client's own share limits stop seeding. Null leaves the saved value unchanged; combine with ClearShareRatioLimit to clear it.</summary>
    double? ShareRatioLimit = null,
    bool ClearShareRatioLimit = false,
    /// <summary>#811 (qBittorrent only): minutes to seed before the client's own share limits stop it. Null leaves the saved value unchanged; combine with ClearShareSeedingTimeLimitMinutes to clear it.</summary>
    int? ShareSeedingTimeLimitMinutes = null,
    bool ClearShareSeedingTimeLimitMinutes = false,
    /// <summary>#811 (qBittorrent only): minutes of no upload activity before the client's own share limits stop seeding. Null leaves the saved value unchanged; combine with ClearShareInactiveSeedingTimeLimitMinutes to clear it.</summary>
    int? ShareInactiveSeedingTimeLimitMinutes = null,
    bool ClearShareInactiveSeedingTimeLimitMinutes = false);
