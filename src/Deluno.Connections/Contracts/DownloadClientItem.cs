namespace Deluno.Connections.Contracts;

using System.Text.Json.Serialization;
using Deluno.Contracts;

public sealed record DownloadClientItem(
    string Id,
    string Name,
    string Protocol,
    string? Host,
    int? Port,
    string? Username,
    [property: JsonIgnore] string? Secret,
    string? EndpointUrl,
    bool UseSsl,
    string? UrlBase,
    string? MoviesCategory,
    string? TvCategory,
    string? CategoryTemplate,
    int Priority,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    string? LastHealthFailureCategory,
    int? LastHealthLatencyMs,
    DateTimeOffset? LastHealthTestUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    bool AddPaused = false,
    /// <summary>Comma-separated tag names (#808). Empty means the client is used for any title; otherwise it is used only for a title sharing one of these tags.</summary>
    string Tags = "",
    /// <summary>Remove a download from this client once Deluno has imported it successfully (#806). Off by default (#936); replaces the old platform-wide RemoveCompletedDownloads flag per client.</summary>
    bool RemoveCompleted = false,
    /// <summary>Remove a download from this client once Deluno records it as failed (#806). Distinct from ImportFailurePolicy, which decides whether the release is blocklisted and searched again; this only decides whether the client keeps the failed entry. Off by default (#936).</summary>
    bool RemoveFailed = false,
    /// <summary>#811 (qBittorrent only): skip hash-checking a grab at add time. Off by default.</summary>
    bool SkipHashCheck = false,
    /// <summary>#811 (qBittorrent only): download pieces in order rather than rarest-first. Off by default.</summary>
    bool SequentialDownload = false,
    /// <summary>#811 (qBittorrent only): prioritize the first and last pieces, so a video can be previewed before it finishes. Off by default.</summary>
    bool FirstLastPiecePrio = false,
    /// <summary>#811 (qBittorrent only): ratio at which this client's own share limits stop seeding a grab. Null leaves the client's own default; -1 tells qBittorrent "no limit" the same way it does.</summary>
    double? ShareRatioLimit = null,
    /// <summary>#811 (qBittorrent only): minutes to seed before the client's own share limits stop it, independent of ratio.</summary>
    int? ShareSeedingTimeLimitMinutes = null,
    /// <summary>#811 (qBittorrent only): minutes of no upload activity before the client's own share limits stop seeding, regardless of ratio or total seeding time.</summary>
    int? ShareInactiveSeedingTimeLimitMinutes = null,
    /// <summary>#811 (qBittorrent only): save path sent at add time and reasserted with setLocation once the grab has a hash. Null leaves the client's own default (or its category's save path).</summary>
    string? Location = null,
    /// <summary>#999 (qBittorrent only): how a torrent's files land in the save folder: "default" (leave it to qBittorrent's own setting), "original", "subfolder" (always a folder named after the torrent) or "no-subfolder" (files straight into the save folder). See <see cref="ContentLayouts"/>.</summary>
    string ContentLayout = ContentLayouts.Default)
{
    /// <summary>The saved secret was stored on another computer, account or key and cannot be read here (#1094). It is listed as missing; entering it again clears this.</summary>
    public bool SecretUnreadable { get; init; }

    /// <summary>Plain-words reason when <see cref="SecretUnreadable"/> is set, for the screen and Health.</summary>
    public string? SecretProblem { get; init; }

    /// <summary>Last typed health failure, when the client did not pass its most recent test.</summary>
    public IntegrationFailure? LastHealthFailure { get; init; }
}
