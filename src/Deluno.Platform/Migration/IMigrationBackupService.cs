namespace Deluno.Platform.Migration;

/// <summary>
/// The migration layer depends on a verified backup boundary, not on the API
/// backup implementation. This keeps the platform module independent while
/// allowing the host's configured backup store to be used before any write.
/// </summary>
public interface IMigrationBackupService
{
    Task<MigrationBackupReceipt> CreateVerifiedBackupAsync(
        string reason,
        CancellationToken cancellationToken);
}

public sealed record MigrationBackupReceipt(
    string BackupId,
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    string Reason,
    string Verification);
