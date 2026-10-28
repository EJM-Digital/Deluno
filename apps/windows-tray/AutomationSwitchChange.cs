namespace Deluno.Tray;

/// <summary>
/// What the tray's Pause automation / Resume automation item does: it flips the same Automation switch the Dashboard's
/// Resume button flips, then publishes a Settings change so the web screens follow, and says which way the switch now is
/// so the pause bars can follow. A failure is left to the caller, which shows it as a balloon.
/// </summary>
public static class AutomationSwitchChange
{
    /// <param name="resume">True to switch automation on (Resume), false to switch it off (Pause).</param>
    /// <param name="setEnabled">Saves the switch and returns whether automation is now on.</param>
    /// <param name="publishSettingsChanged">Tells the open web screens that Settings changed.</param>
    /// <returns>True when automation is now paused.</returns>
    public static async Task<bool> ApplyAsync(bool resume, Func<bool, Task<bool>> setEnabled, Func<Task> publishSettingsChanged)
    {
        var isOn = await setEnabled(resume).ConfigureAwait(false);
        await publishSettingsChanged().ConfigureAwait(false);
        return !isOn;
    }
}

/// <summary>What a click on Open Deluno says when there is no live address to open: why, and what to do.</summary>
public static class OpenDelunoAnswer
{
    public const string Title = "Deluno can't open yet";

    public static string Why(TrayState state) => state switch
    {
        TrayState.Starting => "Deluno is still starting. Click the icon again in a moment.",
        TrayState.Updating => "Deluno is updating. Click the icon again in a moment.",
        _ => "Deluno is not running. Choose Restart Deluno from the icon menu."
    };
}
