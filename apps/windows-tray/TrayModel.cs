using System.Globalization;

namespace Deluno.Tray;

// The tray standard (docs/tray-standard.md) as plain data, so the icon mark, the hover text and the menu are decided
// in one place and tested without a desktop. TrayApplication only draws what these return.

/// <summary>Where the host is. There is no mark for busy: downloading and processing are normal.</summary>
public enum TrayState
{
    Starting,
    Running,
    /// <summary>The host failed to start, or stopped by itself and the restarts ran out.</summary>
    Error,
    /// <summary>Applying a start-up mode change.</summary>
    Updating,
    Stopped
}

/// <summary>The corner mark on the brand icon. None means all is well.</summary>
public enum TrayBadge
{
    None,
    Starting,
    Paused,
    NeedsYou,
    UpdateReady
}

/// <summary>What the update menu item is, and so what a click on it does.</summary>
public enum UpdateMenuAction
{
    /// <summary>Not an installed copy: open System, Updates.</summary>
    OpenUpdatePage,
    Check,
    Wait,
    Download,
    Restart
}

/// <summary>The update menu item's wording and rule, from the orchestrator's state.</summary>
public sealed record TrayUpdate(UpdateMenuAction Action, string Text, bool Enabled, string? Version)
{
    public static TrayUpdate None { get; } = new(UpdateMenuAction.Check, "Check for updates", true, null);

    /// <summary>True when an update is waiting on the person: one to download, or one downloaded and waiting for a restart.</summary>
    public bool HasUpdate => Action is UpdateMenuAction.Download or UpdateMenuAction.Restart;

    /// <summary>
    /// What a check that failed says, in the sentence the Updates page uses ("Couldn't check for updates (GitHub limit,
    /// retry after 10:35 am)."). Null when the check did not fail.
    /// </summary>
    public static string? CheckProblem(string state, string? message)
        => state == Deluno.Api.Updates.UpdateStates.Error && !string.IsNullOrWhiteSpace(message) ? message.Trim() : null;

    /// <summary>The five states: Check, Checking (grey), Downloading (grey), Download, Restart.</summary>
    public static TrayUpdate Describe(bool isInstalled, string state, string? latestVersion, bool updateAvailable, bool restartRequired)
    {
        if (!isInstalled)
        {
            return new(UpdateMenuAction.OpenUpdatePage, "Check for updates", true, null);
        }

        var version = string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion.Trim();
        var suffix = version is null ? string.Empty : $" (v{version})";
        if (state == Deluno.Api.Updates.UpdateStates.Checking)
        {
            return new(UpdateMenuAction.Wait, "Checking for updates...", false, version);
        }

        if (state == Deluno.Api.Updates.UpdateStates.Downloading)
        {
            return new(UpdateMenuAction.Wait, "Downloading update...", false, version);
        }

        if (restartRequired || state == Deluno.Api.Updates.UpdateStates.ReadyToRestart)
        {
            return new(UpdateMenuAction.Restart, $"Restart to update{suffix}", true, version);
        }

        if (updateAvailable || state == Deluno.Api.Updates.UpdateStates.UpdateAvailable)
        {
            return new(UpdateMenuAction.Download, $"Download update{suffix}", true, version);
        }

        return new(UpdateMenuAction.Check, "Check for updates", true, null);
    }
}

/// <summary>Whether Windows Firewall lets other devices in, as far as the menu cares.</summary>
public enum TrayLanState
{
    /// <summary>No firewall step is possible here, so no firewall items.</summary>
    NotAvailable,
    Open,
    Blocked
}

/// <summary>Everything the icon mark, hover text and menu are decided from.</summary>
public sealed record TrayStatus
{
    public TrayState State { get; init; } = TrayState.Starting;

    /// <summary>The live address Deluno answers on, such as http://localhost:7879. Null until the host is listening.</summary>
    public string? Address { get; init; }

    public int Port { get; init; } = 7879;

    /// <summary>The bell's "things need a look" count, built from the server's own counts.</summary>
    public int NeedsLookCount { get; init; }

    /// <summary>Health has a problem (a client, indexer or processor that can't be reached, or anything else it flags).</summary>
    public bool HealthProblem { get; init; }

    public bool AutomationPaused { get; init; }

    public TrayUpdate Update { get; init; } = TrayUpdate.None;

    /// <summary>Why the last "Check for updates" failed, until the next check starts. Null when it did not fail.</summary>
    public string? UpdateCheckProblem { get; init; }

    public bool StartsWithWindows { get; init; }

    public TrayLanState Lan { get; init; } = TrayLanState.NotAvailable;

    /// <summary>The Allow-other-devices step is running now.</summary>
    public bool LanBusy { get; init; }

    /// <summary>Shown while the supervisor starts the host again, such as "try 2 of 5".</summary>
    public string? RestartNote { get; init; }

    public string Version { get; init; } = "0.0.0";

    /// <summary>A Transmission re-add is waiting (shown at the top of the menu).</summary>
    public bool TransmissionReaddPending { get; init; }

    /// <summary>Apps are still to install; the text is what the item says.</summary>
    public string? InstallAppsText { get; init; }

    public bool NeedsYou => State is TrayState.Error or TrayState.Stopped || HealthProblem || NeedsLookCount > 0;
}

public static class TrayBadges
{
    /// <summary>One mark shows. The order is red dot, grey ring, two bars, blue dot.</summary>
    public static TrayBadge Choose(TrayStatus status)
    {
        if (status.NeedsYou)
        {
            return TrayBadge.NeedsYou;
        }

        if (status.State is TrayState.Starting or TrayState.Updating)
        {
            return TrayBadge.Starting;
        }

        if (status.AutomationPaused)
        {
            return TrayBadge.Paused;
        }

        return status.Update.HasUpdate ? TrayBadge.UpdateReady : TrayBadge.None;
    }
}

public static class TrayHoverText
{
    /// <summary>The longest text Windows shows for a tray icon (127 characters, WinForms' own limit).</summary>
    public const int Limit = 127;

    /// <summary>What needs the person, in words: "2 things need a look".</summary>
    public static string? NeedsText(TrayStatus status)
    {
        if (status.State is TrayState.Error or TrayState.Stopped)
        {
            return null;
        }

        if (status.NeedsLookCount > 0)
        {
            return status.NeedsLookCount == 1
                ? "1 thing needs a look"
                : string.Create(CultureInfo.InvariantCulture, $"{status.NeedsLookCount} things need a look");
        }

        return status.HealthProblem ? "Health has a problem" : null;
    }

    /// <summary>The status words after "Deluno - ", also the greyed status line at the top of the menu.</summary>
    public static string StatusWords(TrayStatus status, bool withAddress = true)
    {
        var primary = status.State switch
        {
            TrayState.Starting => status.RestartNote is { } note ? $"Stopped by itself - {note}" : "Starting...",
            TrayState.Updating => "Updating...",
            TrayState.Stopped => "Stopped - choose Restart Deluno",
            TrayState.Error => "Stopped - choose Restart Deluno",
            _ => status.AutomationPaused
                ? "Paused"
                : withAddress && status.Address is { } address ? $"Running at {address}" : "Running"
        };

        var parts = new List<string> { primary };
        if (NeedsText(status) is { } needs)
        {
            parts.Add(needs);
        }

        if (status.Update.HasUpdate)
        {
            parts.Add(status.Update.Action == UpdateMenuAction.Restart
                ? $"Update ready{VersionSuffix(status.Update.Version)}"
                : $"Update available{VersionSuffix(status.Update.Version)}");
        }

        // Last, so it is the first to go when the text is too long; the balloon and the Updates page say it as well.
        if (status.UpdateCheckProblem is { } checkProblem)
        {
            parts.Add(checkProblem.TrimEnd('.'));
        }

        return Fit(parts, Limit - "Deluno - ".Length, status);
    }

    /// <summary>The whole hover text: <c>Deluno - &lt;status&gt;</c>.</summary>
    public static string Build(TrayStatus status) => "Deluno - " + StatusWords(status);

    private static string VersionSuffix(string? version) => version is null ? string.Empty : $" (v{version})";

    /// <summary>
    /// Keeps the text within the limit without cutting a word: the update note goes first, then the address, and only
    /// then is the last whole word before the limit the end, with "...".
    /// </summary>
    private static string Fit(List<string> parts, int limit, TrayStatus status)
    {
        string Join() => string.Join(" - ", parts);
        if (Join().Length <= limit)
        {
            return Join();
        }

        // The address is the longest, least urgent part.
        if (status.State == TrayState.Running && !status.AutomationPaused)
        {
            parts[0] = "Running";
        }

        if (Join().Length <= limit)
        {
            return Join();
        }

        while (parts.Count > 1 && Join().Length > limit)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        var text = Join();
        if (text.Length <= limit)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', limit - 4);
        return (cut > 0 ? text[..cut] : text[..(limit - 3)]) + "...";
    }
}

public enum TrayMenuId
{
    Separator,
    ReaddTransmission,
    InstallApps,
    Open,
    StatusLine,
    PauseResume,
    Restart,
    CopyAddress,
    AllowLan,
    OnlyThisPc,
    ChangePort,
    OpenData,
    OpenLogs,
    StartWithWindows,
    Update,
    Version,
    Report,
    Advanced,
    RunAsService,
    LetAnyAddressIn,
    Quit
}

public sealed record TrayMenuEntry(
    TrayMenuId Id,
    string Text,
    bool Enabled = true,
    bool Visible = true,
    bool Checked = false,
    bool Bold = false,
    string? ToolTip = null);

/// <summary>
/// The right-click menu, in the standard's order and wording, with each item's enabled rule. TrayApplication applies it
/// to the real menu every time the status changes or the menu opens.
/// </summary>
public sealed record TrayMenuState(IReadOnlyList<TrayMenuEntry> Entries, IReadOnlyList<TrayMenuEntry> AdvancedEntries)
{
    public const string QuitToolTip = "Stops Deluno and closes this icon. Download clients Deluno started keep running.";

    public TrayMenuEntry Get(TrayMenuId id) => Entries.Concat(AdvancedEntries).First(entry => entry.Id == id);

    /// <summary>The texts of the items a person sees, top to bottom, with "-" for a separator that shows.</summary>
    public IReadOnlyList<string> VisibleTexts()
        => Entries.Where(entry => entry.Visible).Select(entry => entry.Id == TrayMenuId.Separator ? "-" : entry.Text).ToList();

    public static TrayMenuState Build(TrayStatus status)
    {
        var running = status.State == TrayState.Running;
        var canRestart = status.State is TrayState.Running or TrayState.Error or TrayState.Stopped;
        var firewall = status.Lan != TrayLanState.NotAvailable;
        var topVisible = status.TransmissionReaddPending || status.InstallAppsText is not null;

        var entries = new List<TrayMenuEntry>
        {
            new(TrayMenuId.ReaddTransmission, "Add Transmission's torrents back...", Visible: status.TransmissionReaddPending),
            new(TrayMenuId.InstallApps, status.InstallAppsText ?? "Try again: install apps...", Visible: status.InstallAppsText is not null),
            new(TrayMenuId.Separator, string.Empty, Visible: topVisible),
            new(TrayMenuId.Open, "Open Deluno", Enabled: true, Bold: true),
            new(TrayMenuId.StatusLine, TrayHoverText.Build(status), Enabled: false),
            new(TrayMenuId.Separator, string.Empty),
            new(TrayMenuId.PauseResume, status.AutomationPaused ? "Resume automation" : "Pause automation", Enabled: running),
            new(TrayMenuId.Restart, "Restart Deluno", Enabled: canRestart),
            new(TrayMenuId.Separator, string.Empty),
            new(TrayMenuId.CopyAddress, "Copy address", Enabled: status.Address is not null),
            new(TrayMenuId.AllowLan, "Allow other devices on your network...", Enabled: !status.LanBusy, Visible: firewall),
            new(TrayMenuId.OnlyThisPc, "Only allow this PC", Enabled: !status.LanBusy && status.Lan == TrayLanState.Open, Visible: firewall),
            new(TrayMenuId.ChangePort, string.Create(CultureInfo.InvariantCulture, $"Change port ({status.Port})..."), Enabled: status.State != TrayState.Updating),
            new(TrayMenuId.Separator, string.Empty),
            new(TrayMenuId.OpenData, "Open data folder"),
            new(TrayMenuId.OpenLogs, "Open logs folder"),
            new(TrayMenuId.StartWithWindows, "Start with Windows", Checked: status.StartsWithWindows),
            new(TrayMenuId.Separator, string.Empty),
            new(TrayMenuId.Update, status.Update.Text, Enabled: status.Update.Enabled),
            new(TrayMenuId.Version, $"Deluno v{status.Version}", Enabled: false),
            new(TrayMenuId.Report, "Report a problem..."),
            new(TrayMenuId.Advanced, "Advanced"),
            new(TrayMenuId.Separator, string.Empty),
            new(TrayMenuId.Quit, "Quit Deluno", ToolTip: QuitToolTip)
        };

        var advanced = new List<TrayMenuEntry>
        {
            new(TrayMenuId.RunAsService, "Run as a Windows service..."),
            new(TrayMenuId.LetAnyAddressIn, "Let any address in...")
        };

        return new TrayMenuState(entries, advanced);
    }
}

/// <summary>
/// Decides whether a click opens Deluno. A left click opens once, and the double-click that follows it
/// (a second click inside the system double-click time) does not open again. A middle click does nothing.
/// </summary>
public sealed class OpenClickGate(TimeSpan window)
{
    private DateTimeOffset? _lastOpen;

    public bool ShouldOpen(DateTimeOffset now)
    {
        if (_lastOpen is { } last && now - last <= window)
        {
            return false;
        }

        _lastOpen = now;
        return true;
    }
}

/// <summary>One rule for how long a balloon stays: information 10 seconds, a problem the person must read 30.</summary>
public static class TrayBalloons
{
    public const int InformationMilliseconds = 10_000;
    public const int ProblemMilliseconds = 30_000;

    public static int DurationFor(bool problem) => problem ? ProblemMilliseconds : InformationMilliseconds;
}
