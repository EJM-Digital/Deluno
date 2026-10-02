namespace Deluno.Jobs.Contracts;

/// <summary>
/// Which problems Deluno has already told someone about for each download.
///
/// <para>The poller and the import pipeline run over and over, and a download
/// that stays stuck would otherwise be reported again on every pass: a new
/// activity entry and a new import issue each time. This is the memory that
/// stops that. It is kept on the download's own timeline (rows whose type
/// starts <c>problem-</c>, which the timeline reads hide), so there is no
/// table of its own and nothing here is ever shown as a list.</para>
///
/// <para>A problem is <i>recorded</i> from the moment it is raised until its
/// download moves on without anyone having dismissed it. It is <i>open</i> -
/// waiting on the owner - until the issue written beside it is dismissed.
/// Dismissing keeps it recorded, so a stuck download is not reported again
/// every hour; moving on while still open releases it, and the issue written
/// beside it settles with it.</para>
/// </summary>
public interface IDispatchProblemLog
{
    /// <summary>Whether this download already has this kind of problem recorded.</summary>
    Task<bool> IsRecordedAsync(string dispatchId, string kind, CancellationToken cancellationToken);

    /// <summary>Records that this kind of problem has been reported for the download.</summary>
    Task RecordAsync(string dispatchId, string kind, CancellationToken cancellationToken);

    /// <summary>
    /// The issue written about this download was dismissed: its problems stay
    /// recorded, but are no longer waiting on anyone. Returns how many.
    /// </summary>
    Task<int> AcknowledgeForDispatchAsync(string dispatchId, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for every download of one release sent to one client: for an
    /// issue recorded before it kept its download's id, which cannot say which
    /// of a release's grabs it was about.
    /// </summary>
    Task<int> AcknowledgeForReleaseAsync(string downloadClientId, string releaseName, CancellationToken cancellationToken);

    /// <summary>
    /// Releases every open problem whose download has moved past it, and
    /// returns them, so the issues written beside them can settle too.
    /// </summary>
    Task<IReadOnlyList<SettledDispatchProblem>> SettleMovedOnAsync(CancellationToken cancellationToken);

    /// <summary>How many problems are still waiting on the owner.</summary>
    Task<int> CountOpenAsync(CancellationToken cancellationToken);
}

/// <summary>A problem released because its download moved on, and why.</summary>
public sealed record SettledDispatchProblem(string DispatchId, string Kind, string Reason);
