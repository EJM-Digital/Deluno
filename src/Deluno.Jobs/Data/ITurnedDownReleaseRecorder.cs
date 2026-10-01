namespace Deluno.Jobs.Data;

/// <summary>One release a search looked at and did not take (#1098): its name, the plain group and the raw reason it came from.</summary>
public sealed record TurnedDownRelease(string Release, string Group, string? RawReason, string? IndexerName);

/// <summary>
/// Remembers, once per search, the releases that search turned down, so the Live screen can say how many
/// were turned down today and why. A search that turned nothing down writes nothing.
/// </summary>
public interface ITurnedDownReleaseRecorder
{
    Task RecordAsync(
        string? title,
        string? entityType,
        string? entityId,
        IReadOnlyList<TurnedDownRelease> releases,
        CancellationToken cancellationToken);
}
