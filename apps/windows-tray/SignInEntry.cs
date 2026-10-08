using Microsoft.Win32;

namespace Deluno.Tray;

/// <summary>
/// Whether Deluno starts when the person signs in to Windows. One per-user entry (the Run key under HKCU), never
/// machine-wide and never on unless the person said Yes. The tray menu's tick and the first-run question both use it.
/// </summary>
public interface ISignInEntry
{
    bool IsEnabled();
    void Enable();
    void Disable();
}

public sealed class RunKeySignInEntry(string? exePath = null, string? subKey = null) : ISignInEntry
{
    public const string ValueName = "Deluno";
    private const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _exe = exePath ?? Path.Combine(AppContext.BaseDirectory, "Deluno.exe");
    private readonly string _subKey = subKey ?? DefaultSubKey;

    /// <summary>The stored command: the exe path in quotes, with no flags (a sign-in start never opens a browser).</summary>
    public string Command => $"\"{_exe}\"";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true);
        key.SetValue(ValueName, Command);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Flips the entry and returns the new state.</summary>
    public static bool Toggle(ISignInEntry entry)
    {
        if (entry.IsEnabled())
        {
            entry.Disable();
            return false;
        }

        entry.Enable();
        return true;
    }
}

/// <summary>"Start Deluno when you sign in to Windows?" - asked once, at first run. Nothing is on unless the answer is Yes.</summary>
public static class StartWithWindowsQuestion
{
    public const string Title = "Deluno";
    public const string Text = "Start Deluno when you sign in to Windows?";

    /// <summary>Applies an answer: Yes turns the entry on, No or no answer changes nothing.</summary>
    public static void Apply(bool? answeredYes, ISignInEntry entry)
    {
        if (answeredYes == true)
        {
            entry.Enable();
        }
    }

    /// <summary>The question as a Yes/No box. Returns null when it could not be shown.</summary>
    public static bool? AskWithMessageBox()
    {
        try
        {
            var answer = MessageBox.Show(Text, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            return answer == DialogResult.Yes;
        }
        catch
        {
            return null;
        }
    }
}
