namespace Deluno.Platform.Contracts;

/// <summary>
/// Durable, observational evidence for a problematic download. It is deliberately
/// separate from cleanup actions: a blocked candidate cannot cause file removal.
/// </summary>
public sealed record DownloadHealthObservation(
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string Kind,
    string Severity,
    string Evidence);

public sealed record DownloadHealthRecord(
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string ReleaseKey,
    string Kind,
    string Severity,
    string Evidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int StrikeCount,
    DateTimeOffset? IgnoredUntilUtc)
{
    public bool IsIgnored(DateTimeOffset now) => IgnoredUntilUtc is { } ignoredUntil && ignoredUntil > now;

    public bool BlocksCandidate(DateTimeOffset now, int threshold = 3)
        => StrikeCount >= Math.Clamp(threshold, 1, 20) && !IsIgnored(now);
}
