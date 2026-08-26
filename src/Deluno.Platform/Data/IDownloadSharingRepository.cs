using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// The last sharing pass, kept so the dashboard can show it (#288).
///
/// Deliberately a replace, never an append: this is a picture of what the
/// clients hold right now, not a history. The history of what was let go of is
/// the activity feed's job, and keeping two of them would let them disagree.
/// </summary>
public interface IDownloadSharingRepository
{
    Task ReplaceHoldsAsync(
        IReadOnlyList<DownloadSharingHold> holds,
        string? driveNote,
        CancellationToken cancellationToken);

    Task<DownloadSharingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
