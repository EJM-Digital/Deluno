namespace Deluno.Contracts;

/// <summary>What ending a deleted thing's work came to, for the record.</summary>
public sealed record DeletionCleanup(
    int CancelledJobs,
    int ArchivedDownloads,
    int EndedHandoffs,
    int SettledCases,
    int ForgottenHealthRecords)
{
    public static readonly DeletionCleanup Nothing = new(0, 0, 0, 0, 0);

    public IReadOnlyDictionary<string, string> AsMetadata() => new Dictionary<string, string>
    {
        ["cancelledJobCount"] = CancelledJobs.ToString(),
        ["archivedDownloadCount"] = ArchivedDownloads.ToString(),
        ["endedProcessorHandoffCount"] = EndedHandoffs.ToString(),
        ["settledRecoveryCaseCount"] = SettledCases.ToString(),
        ["forgottenHealthRecordCount"] = ForgottenHealthRecords.ToString()
    };
}

/// <summary>
/// Ends the work a deleted thing left running.
///
/// <para><b>Why this exists.</b> Deleting a library, a download client or a
/// processor connection used to delete one row and nothing else. What it had
/// been doing carried on: downloads still polled although they could never be
/// detected, hand-offs still waited for a processor that could no longer be
/// asked, and both went on raising recovery cases about a thing nobody had any
/// more — each one counted under Needs attention, several of them unclearable
/// (#567, #568, #569). It is the same defect a removed title had (#563).</para>
///
/// <para>Declared here rather than beside any of them because the things that
/// need clearing up live in four different databases, and the endpoints that
/// delete are in three different modules, none of which can see the others.</para>
/// </summary>
public interface IDeletedThingCleanup
{
    /// <summary>
    /// A library is gone: its pending jobs, its downloads, the hand-offs about
    /// them, and the cases raised about any of it.
    /// </summary>
    Task<DeletionCleanup> LibraryDeletedAsync(string libraryId, string libraryName, CancellationToken cancellationToken);

    /// <summary>
    /// A download client is gone: the downloads it was holding can never be
    /// detected now, so they are archived rather than polled for ever, and what
    /// was recorded about their health is forgotten.
    /// </summary>
    Task<DeletionCleanup> DownloadClientDeletedAsync(string clientId, string clientName, CancellationToken cancellationToken);

    /// <summary>
    /// A processor is gone: the files still with it are dropped while there is
    /// still something to ask, rather than timing out hours later and raising
    /// cases naming a processor that was deliberately deleted.
    /// </summary>
    Task<DeletionCleanup> ProcessorDeletedAsync(string processorConnectionId, string processorName, CancellationToken cancellationToken);
}
