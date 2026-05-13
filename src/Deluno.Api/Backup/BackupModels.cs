namespace Deluno.Api.Backup;

public sealed record BackupSettingsSnapshot(
    bool Enabled,
    string Frequency,
    string TimeOfDay,
    int RetentionCount,
    string BackupFolder,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc);

public sealed record UpdateBackupSettingsRequest(
    bool Enabled,
    string? Frequency,
    string? TimeOfDay,
    int? RetentionCount,
    string? BackupFolder);

public sealed record BackupManifest(
    string App,
    string Version,
    DateTimeOffset CreatedUtc,
    string Reason,
    IReadOnlyList<string> Files);

public sealed record BackupItem(
    string Id,
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    string Reason);

public sealed record BackupCreateRequest(string? Reason);

public sealed record BackupCreateResponse(BackupItem Backup);

public sealed record RestorePreviewResponse(
    bool Valid,
    string Message,
    BackupManifest? Manifest,
    IReadOnlyList<string> Warnings);

public sealed record RestoreResultResponse(
    bool Restored,
    string Message,
    string RestoreFolder,
    IReadOnlyList<string> RestoredFiles);
