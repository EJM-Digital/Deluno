namespace Deluno.Tray;

/// <summary>The two things of the shell's notification-area icon that decide what its hover text and accessible name say.</summary>
public interface ITrayShellIcon
{
    bool Visible { get; set; }

    string Text { get; set; }
}

/// <summary>
/// Puts the hover text on the tray icon (#1246). Windows reads an icon's accessible name as the tip it was added with
/// followed by its current tip, so an icon that is added saying "Starting..." and later told "Running at ..." reads
/// "Deluno - Starting... Deluno - Running at ..." to a screen reader and to UI Automation. Taking the icon off and
/// putting it back with the new text starts the name afresh, so the accessible name is the hover text in every state.
/// </summary>
public sealed class TrayIconTip(ITrayShellIcon icon)
{
    private string? _published;

    /// <summary>The text last handed to the shell, or null before the first.</summary>
    public string? Published => _published;

    public void Publish(string hover)
    {
        if (string.Equals(hover, _published, StringComparison.Ordinal))
        {
            return;
        }

        _published = hover;
        icon.Text = hover;
    }
}
