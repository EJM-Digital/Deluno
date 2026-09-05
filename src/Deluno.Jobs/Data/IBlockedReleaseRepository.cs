using Deluno.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The list of releases Deluno will not use again.
///
/// <para>Blocking is idempotent: the same release failing twice is one entry,
/// not two, and the first reason is kept. A person reading the list wants to
/// know what happened, and the second occurrence tells them nothing the first
/// did not.</para>
/// </summary>
public interface IBlockedReleaseRepository
{
    Task<BlockedRelease> BlockAsync(BlockedRelease release, CancellationToken cancellationToken);

    Task<IReadOnlyList<BlockedRelease>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The keys a search should skip. Returned as a set because a search asks
    /// this once and then tests every candidate against it.
    ///
    /// <para>Proposals are not in it. "Ask me" means nothing has been decided,
    /// and a search that skipped an undecided release would be deciding by
    /// omission.</para>
    /// </summary>
    Task<IReadOnlySet<string>> ListKeysAsync(CancellationToken cancellationToken);

    Task<bool> UnblockAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Answers a proposal with "yes, refuse it". Does nothing to an entry that
    /// is already refused, so a double click cannot move the date it happened.
    /// </summary>
    Task<bool> RefuseAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Blocked releases whose leftovers are still in a download client.
    ///
    /// <para>Only the ones the table says should be cleared — a release refused
    /// because Deluno could not identify it is not one whose file should be
    /// destroyed.</para>
    /// </summary>
    Task<IReadOnlyList<BlockedRelease>> ListAwaitingCleanupAsync(CancellationToken cancellationToken);

    /// <summary>What has been refused for one title.</summary>
    Task<IReadOnlyList<BlockedRelease>> ListForAsync(string mediaType, string entityId, CancellationToken cancellationToken);

    Task MarkCleanedUpAsync(string id, CancellationToken cancellationToken);
}
