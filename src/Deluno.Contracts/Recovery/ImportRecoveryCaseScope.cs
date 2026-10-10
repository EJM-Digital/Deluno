namespace Deluno.Contracts;

/// <summary>
/// Which open recovery cases something settles.
///
/// <para>A case records what it is about in free-text details and nothing else:
/// the download poller writes a <c>DispatchId</c>, the processor watcher writes
/// a <c>releaseName</c> and a <c>handoffId</c>, and no case records which film
/// or show it belongs to. So there are three ways in, and which ones apply says
/// what happened.</para>
///
/// <para>A download that recovers is one dispatch id (#559). A title that is
/// removed is every dispatch it ever made, plus the releases those dispatches
/// named — which is the only path from a title to the cases its processor
/// hand-offs raised (#563). A processor, download client or library that is
/// deleted is everything that named it (#567, #568, #569).</para>
/// </summary>
public sealed record ImportRecoveryCaseScope(
    IReadOnlyCollection<string> DispatchIds,
    IReadOnlyCollection<string> ReleaseNames,
    /// <summary>
    /// Also settle a case that names its dispatch only inside the retry it
    /// offers — an import that failed. A download recovering does not make a
    /// failed import true again, so only a deletion asks for these.
    /// </summary>
    bool IncludingFailedImports = false)
{
    /// <summary>
    /// The hand-offs a case may name instead of a download. A processor case
    /// carries one of these and no dispatch at all, so it is the only way to
    /// reach the cases a deleted processor leaves.
    /// </summary>
    public IReadOnlyCollection<string> HandoffIds { get; init; } = [];

    /// <summary>
    /// Cases by their own id, for a case that names nothing else Deluno can follow: an unmatched processor output
    /// names only a file (#1242), and the file was imported.
    /// </summary>
    public IReadOnlyCollection<string> CaseIds { get; init; } = [];

    /// <summary>One download, by the dispatch that sent it.</summary>
    public static ImportRecoveryCaseScope ForDispatch(string dispatchId) => new([dispatchId], []);

    /// <summary>
    /// Everything a removed title left behind: the dispatches it made and the
    /// releases they named, including the imports that failed.
    /// </summary>
    public static ImportRecoveryCaseScope ForRemovedTitle(
        IReadOnlyCollection<string> dispatchIds,
        IReadOnlyCollection<string> releaseNames)
        => new(dispatchIds, releaseNames, IncludingFailedImports: true);

    /// <summary>
    /// Everything a deleted library, download client or processor left behind.
    /// </summary>
    public static ImportRecoveryCaseScope ForDeleted(
        IReadOnlyCollection<string> dispatchIds,
        IReadOnlyCollection<string> releaseNames,
        IReadOnlyCollection<string> handoffIds)
        => new(dispatchIds, releaseNames, IncludingFailedImports: true) { HandoffIds = handoffIds };

    /// <summary>Nothing to settle, so nothing is asked of the database.</summary>
    public bool IsEmpty => DispatchIds.Count == 0 && ReleaseNames.Count == 0 && HandoffIds.Count == 0 && CaseIds.Count == 0;
}

/// <summary>An open recovery case Deluno closed because its film or episode now holds its file (#1167).</summary>
public sealed record SettledRecoveryCase(string CaseId, string Title, string FailureKind);

/// <summary>
/// Which film or episode ids hold a file, and since when (#1167). A null time is a file whose arrival was never
/// dated, such as a relinked one.
/// </summary>
public sealed record TitleFileHolds(IReadOnlyDictionary<string, DateTimeOffset?> ImportedByEntityId)
{
    public static TitleFileHolds None { get; } = new(new Dictionary<string, DateTimeOffset?>());
}
