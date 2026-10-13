using Deluno.Contracts;

namespace Deluno.Tray;

/// <summary>
/// Keeps the list of apps still to install (<see cref="PendingComponentInstalls"/>) true after the picker ran:
/// an app that went in, or was already there, comes off it; an app that only failed because GitHub's limit on
/// anonymous lookups was used up goes on it, with when the limit lifts, so the tray can offer Try again.
/// </summary>
public static class PendingInstallRecorder
{
    /// <summary>
    /// Returns the problems (in everyday words) of any change to the list that could not be kept, or none. A change that could
    /// not be kept leaves the list as it was, and the problem goes to the install log: it is never dropped silently (#1260).
    /// </summary>
    public static IReadOnlyList<string> Record(IReadOnlyList<ComponentInstallResult> results, string? dataRoot = null, IStateFileSystem? io = null)
    {
        dataRoot ??= ComponentPaths.DataRoot();
        var problems = new List<string>();
        void Try(Action change)
        {
            try
            {
                change();
            }
            catch (StateFileException exception)
            {
                problems.Add(exception.Message);
                HookLog.Write($"The list of apps waiting to be installed was not changed: {exception.Message}");
            }
        }

        var done = results
            .Where(r => r.Outcome is ComponentInstallOutcome.Installed or ComponentInstallOutcome.AlreadyInstalled or ComponentInstallOutcome.InstalledNeedsSetup)
            .Select(r => r.Kind)
            .ToArray();
        if (done.Length > 0)
        {
            Try(() => PendingComponentInstalls.Remove(dataRoot, done, io));
        }

        // Whatever the person asked for and the picker has now run is no longer an unanswered ask.
        Try(() => PendingComponentInstalls.MarkAsked(dataRoot, io));

        var waiting = results.Where(r => r.RetryAfterUtc is not null).ToArray();
        if (waiting.Length > 0)
        {
            Try(() => PendingComponentInstalls.Add(dataRoot, waiting.Select(r => r.Kind), waiting.Max(r => r.RetryAfterUtc), askedByPerson: false,
                reason: "GitHub's hourly limit on looking up releases was used up.", io));
        }

        return problems;
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
