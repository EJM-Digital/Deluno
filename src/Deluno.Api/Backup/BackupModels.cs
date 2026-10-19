namespace Deluno.Api.Backup;

public sealed record BackupSettingsSnapshot(
    bool Enabled,
    string Frequency,
    string TimeOfDay,
    int RetentionCount,
    string BackupFolder,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc,
    string? Problem = null,
    bool PasswordSet = false);

public sealed record UpdateBackupSettingsRequest(
    bool Enabled,
    string? Frequency,
    string? TimeOfDay,
    int? RetentionCount,
    string? BackupFolder,
    string? BackupPassword = null,
    bool ClearBackupPassword = false);

public sealed record BackupManifest(
    string App,
    string Version,
    DateTimeOffset CreatedUtc,
    string Reason,
    IReadOnlyList<string> Files,
    bool KeysIncluded = false);

public sealed record BackupItem(
    string Id,
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    string Reason,
    bool KeysIncluded = false);

public sealed record BackupCreateRequest(string? Reason);

public sealed record BackupCreateResponse(BackupItem Backup);

public sealed record RestorePreviewResponse(
    bool Valid,
    string Message,
    BackupManifest? Manifest,
    IReadOnlyList<string> Warnings,
    bool NewerThanThisVersion = false,
    bool NeedsPassword = false);

public sealed record RestoreResultResponse(
    bool Restored,
    string Message,
    string RestoreFolder,
    IReadOnlyList<string> RestoredFiles,
    bool NeedsPassword = false,
    bool SecretsNeedEntering = false);
