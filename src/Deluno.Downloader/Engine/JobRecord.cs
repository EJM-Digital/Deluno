namespace Deluno.Downloader.Engine;

/// <summary>
/// Row-level shape of the shared <c>jobs</c> table. Protocol-specific
/// extension data lives in separate tables (nzb_segments / torrent_pieces
/// etc.) and is loaded on demand by the protocol implementation.
/// </summary>
public sealed record JobRecord(
    string Id,
    DownloadProtocol Protocol,
    string DisplayName,
    string SourcePath,
    string SourceKind,          // "nzb" | "torrent_file" | "magnet"
    string? Category,
    int Priority,
    JobLifecycleState State,
    string? StateReason,
    bool Paused,
    string? PasswordProtected,  // ISecretProtector ciphertext when set
    string DownloadDir,
    string? OutputDir,
    long TotalBytes,
    long DownloadedBytes,
    long UploadedBytes,
    string? DispatchId,
    string? LibraryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Row-level shape of <c>files</c>.</summary>
public sealed record FileRecord(
    string Id,
    string JobId,
    int FileIndex,
    string Name,
    bool IsPar2,
    bool IsMetadata,
    string Priority,            // "skip" | "low" | "normal" | "high"
    long TotalBytes,
    string State,
    string? OutputPath);

/// <summary>Row-level shape of <c>state_transitions</c>.</summary>
public sealed record StateTransitionRecord(
    long Id,
    string JobId,
    JobLifecycleState? FromState,
    JobLifecycleState ToState,
    string? Reason,
    DateTimeOffset OccurredAt);
