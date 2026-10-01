namespace Deluno.Jobs.Data;

/// <summary>One release a search looked at and did not take (#1098): its name, the plain group and the raw reason it came from.</summary>
public sealed record TurnedDownRelease(string Release, string Group, string? RawReason, string? IndexerName);

/// <summary>
/// Remembers, once per search, the releases that search turned down, so the Live screen can say how many
/// were turned down today and why. A search that turned nothing down writes nothing. A release is one
/// release per day (by name, under its latest reason): meeting it again in a later search adds nothing
/// to the count, and the same reason within the hour is not written at all.
/// </summary>
public interface ITurnedDownReleaseRecorder
{
    /// <summary>Returns the names (case-insensitive) of the releases that were new today, which the pushed event flags so a screen never counts one twice.</summary>
    Task<IReadOnlySet<string>> RecordAsync(
        string? title,
        string? entityType,
        string? entityId,
        IReadOnlyList<TurnedDownRelease> releases,
        CancellationToken cancellationToken);
}
