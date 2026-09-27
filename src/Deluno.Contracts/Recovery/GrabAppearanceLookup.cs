namespace Deluno.Contracts;

/// <summary>
/// What happened to a release Deluno sent to a download client and then never
/// saw appear, once it goes looking for the reason (#911, cleanup sheet B4).
/// </summary>
public enum GrabAppearanceOutcome
{
    /// <summary>
    /// It is there, filed under the category Deluno expected. Ordinary
    /// detection should have caught this already — a slow or missed poll is
    /// the usual reason it did not.
    /// </summary>
    FoundAsExpected,

    /// <summary>
    /// It is there, but filed under a different category than the one this
    /// grab asked for. The category setting for this client is wrong.
    /// </summary>
    FoundUnderAnotherCategory,

    /// <summary>
    /// The client's own history says this is a duplicate of something it
    /// already holds. Nobody's fault (#877/#886) — Deluno follows the copy it
    /// has.
    /// </summary>
    Duplicate,

    /// <summary>
    /// The client's own history says it rejected this file. Blocklisted with
    /// the #874 key and searched again — never left to fail the same way on
    /// its own retry cycle.
    /// </summary>
    Rejected,

    /// <summary>
    /// The client could not be reached, or answered with a configuration
    /// problem rather than a queue. A setup problem (#880): retried, never
    /// blocklisted, because nothing here says anything about the release.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The client was reached and has nothing under this hash, id or name in
    /// its whole queue and history, in any category. Per the issue: this ends
    /// the download the ordinary way.
    /// </summary>
    NotThere
}

/// <param name="Outcome">What the lookup found.</param>
/// <param name="FoundCategory">The category the release was actually filed under, when found.</param>
/// <param name="ExpectedCategory">The category this grab asked for, when Deluno could work it out.</param>
/// <param name="QueueItemId">The client's own id for the item found, when one was found — how Deluno follows it.</param>
/// <param name="Detail">Whatever the client said, for the activity line and recovery case.</param>
public sealed record GrabAppearanceLookupResult(
    GrabAppearanceOutcome Outcome,
    string? FoundCategory = null,
    string? ExpectedCategory = null,
    string? QueueItemId = null,
    string? Detail = null);

/// <summary>
/// Looks a sent release up across a download client's whole queue and
/// history, in every category, once the appearance timeout has expired and
/// ordinary polling still has not seen it (#911).
///
/// <para>Reuses each client adapter's own <c>GetSnapshotAsync</c>/
/// <c>GetHistoryAsync</c> calls — the same ones behind Queue and the
/// telemetry drawer — rather than opening a second connection to the same
/// client.</para>
/// </summary>
public interface IGrabAppearanceLookupService
{
    Task<GrabAppearanceLookupResult> LookupAsync(
        string downloadClientId,
        string mediaType,
        string? category,
        string? torrentHashOrItemId,
        string releaseName,
        CancellationToken cancellationToken);
}
