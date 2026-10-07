namespace Deluno.Connections.Contracts;

public sealed record CreateDownloadClientRequest(
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
    bool IsEnabled,
    bool UseSsl = false,
    string? UrlBase = null,
    bool AddPaused = false,
    /// <summary>Comma-separated tag names. A client with no tags is used for any title; a tagged client is used only for a title sharing one of its tags (#808).</summary>
    string? Tags = null,
    /// <summary>Remove a download from this client once Deluno has imported it successfully (#806). Off by default.</summary>
    bool RemoveCompleted = false,
    /// <summary>Remove a download from this client once Deluno records it as failed (#806). Off by default.</summary>
    bool RemoveFailed = false,
    /// <summary>#811 (qBittorrent only): skip hash-checking a grab at add time. Off by default.</summary>
    bool SkipHashCheck = false,
    /// <summary>#811 (qBittorrent only): download pieces in order rather than rarest-first. Off by default.</summary>
    bool SequentialDownload = false,
    /// <summary>#811 (qBittorrent only): prioritize the first and last pieces. Off by default.</summary>
    bool FirstLastPiecePrio = false,
    /// <summary>#811 (qBittorrent only): ratio at which this client's own share limits stop seeding. Null leaves the client's default.</summary>
    double? ShareRatioLimit = null,
    /// <summary>#811 (qBittorrent only): minutes to seed before the client's own share limits stop it.</summary>
    int? ShareSeedingTimeLimitMinutes = null,
    /// <summary>#811 (qBittorrent only): minutes of no upload activity before the client's own share limits stop seeding.</summary>
    int? ShareInactiveSeedingTimeLimitMinutes = null,
    /// <summary>#811 (qBittorrent only): save path sent at add time. Null leaves the client's own default (or its category's save path).</summary>
    string? Location = null,
    /// <summary>#999 (qBittorrent only): default, original, subfolder or no-subfolder. Omitted means the client's own setting.</summary>
    string? ContentLayout = null,
    /// <summary>#1135: route this client to each library of a type it serves that has no client yet. The drawer opt-out sends false.</summary>
    bool RouteToLibraries = true);
