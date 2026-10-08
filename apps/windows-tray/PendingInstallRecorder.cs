using Deluno.Contracts;

namespace Deluno.Tray;

/// <summary>
/// Keeps the list of apps still to install (<see cref="PendingComponentInstalls"/>) true after the picker ran:
/// an app that went in, or was already there, comes off it; an app that only failed because GitHub's limit on
/// anonymous lookups was used up goes on it, with when the limit lifts, so the tray can offer Try again.
/// </summary>
public static class PendingInstallRecorder
{
    public static void Record(IReadOnlyList<ComponentInstallResult> results, string? dataRoot = null)
    {
        dataRoot ??= ComponentPaths.DataRoot();
        var done = results
            .Where(r => r.Outcome is ComponentInstallOutcome.Installed or ComponentInstallOutcome.AlreadyInstalled or ComponentInstallOutcome.InstalledNeedsSetup)
            .Select(r => r.Kind)
            .ToArray();
        if (done.Length > 0)
        {
            PendingComponentInstalls.Remove(dataRoot, done);
        }

        // Whatever the person asked for and the picker has now run is no longer an unanswered ask.
        PendingComponentInstalls.MarkAsked(dataRoot);

        var waiting = results.Where(r => r.RetryAfterUtc is not null).ToArray();
        if (waiting.Length > 0)
        {
            PendingComponentInstalls.Add(dataRoot, waiting.Select(r => r.Kind), waiting.Max(r => r.RetryAfterUtc), askedByPerson: false,
                reason: "GitHub's hourly limit on looking up releases was used up.");
        }
    }

    /// <summary>The words for the tray menu: what Try again would install, and whether it has to wait.</summary>
    public static string MenuText(PendingComponentInstalls pending, DateTimeOffset now, Func<string, string> displayName)
    {
        var names = string.Join(", ", pending.Kinds.Select(displayName));
        return pending.RetryAfterUtc is { } at && at > now
            ? $"Try again: install {names} (GitHub allows it from {at.ToLocalTime():h:mm tt})..."
            : $"Try again: install {names}...";
    }

    /// <summary>True once the wait is over (or there was none) and the person has not just been told.</summary>
    public static bool ReadyToOffer(PendingComponentInstalls pending, DateTimeOffset now)
        => pending.RetryAfterUtc is null || pending.RetryAfterUtc <= now;
}
