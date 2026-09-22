namespace Deluno.Contracts;

/// <summary>
/// Which open recovery cases something settles.
///
/// <para>A case records what it is about in free-text details and nothing else:
/// the download poller writes a <c>DispatchId</c>, the processor watcher writes
/// a <c>releaseName</c> and a <c>handoffId</c>, and no case records which film
/// or show it belongs to. So there are two ways in, and which ones apply says
/// what happened.</para>
///
/// <para>A download that recovers is one dispatch id (#559). A title that is
/// removed is every dispatch it ever made, plus the releases those dispatches
/// named — which is the only path from a title to the cases its processor
/// hand-offs raised (#563).</para>
/// </summary>
public sealed record ImportRecoveryCaseScope(
    IReadOnlyCollection<string> DispatchIds,
    IReadOnlyCollection<string> ReleaseNames,
    /// <summary>
    /// Also settle a case that names its dispatch only inside the retry it
    /// offers — an import that failed. A download recovering does not make a
    /// failed import true again, so only a removed title asks for these.
    /// </summary>
    bool IncludingFailedImports = false)
{
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

    /// <summary>Nothing to settle, so nothing is asked of the database.</summary>
    public bool IsEmpty => DispatchIds.Count == 0 && ReleaseNames.Count == 0;
}
