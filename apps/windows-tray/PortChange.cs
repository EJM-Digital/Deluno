using System.Globalization;
using System.Net.NetworkInformation;

namespace Deluno.Tray;

/// <summary>Choosing and checking the port Deluno answers on (the number at the end of its address).</summary>
public static class PortChoice
{
    public const int DefaultPort = 7879;

    /// <summary>A whole number from 1 to 65535, or null.</summary>
    public static int? Parse(string? text)
        => text is not null && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;

    /// <summary>What is wrong with a typed port, in words a person can act on, or null when it is fine. Deluno's own current port is not "in use by another program".</summary>
    public static string? Validate(string? text, int currentPort, Func<int, bool> isInUse)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Enter a port number.";
        }

        if (Parse(text) is not { } port)
        {
            return "A port is a whole number from 1 to 65535.";
        }

        if (port != currentPort && isInUse(port))
        {
            return $"Another program on this computer is already using port {port}. Choose a different number.";
        }

        return null;
    }

    /// <summary>True when something on this computer is already listening on the port.</summary>
    public static bool IsInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }
}

public enum PortChangeOutcome
{
    /// <summary>Nothing to do: the same port.</summary>
    Unchanged,
    /// <summary>Deluno now answers on the new port.</summary>
    Changed,
    /// <summary>The port was refused before anything stopped (in use, or not a port).</summary>
    Refused,
    /// <summary>Deluno could not start on the new port, so it went back to the old one.</summary>
    RolledBack,
    /// <summary>It could not start on either port.</summary>
    Failed
}

public sealed record PortChangeResult(PortChangeOutcome Outcome, int Port, string Message);

/// <summary>
/// Moves Deluno to another port: checks the port is free, restarts on it, and goes back to the old port if the restart
/// fails. The tray supplies the restart (save the port, stop the host, start it). Nothing here opens a browser.
/// </summary>
public sealed class PortChanger(Func<int, bool> isInUse, Func<int, Task> restartOnPort)
{
    public async Task<PortChangeResult> ChangeAsync(int currentPort, int requestedPort)
    {
        if (requestedPort == currentPort)
        {
            return new(PortChangeOutcome.Unchanged, currentPort, $"Deluno is already on port {currentPort}.");
        }

        if (PortChoice.Validate(requestedPort.ToString(CultureInfo.InvariantCulture), currentPort, isInUse) is { } problem)
        {
            return new(PortChangeOutcome.Refused, currentPort, problem);
        }

        try
        {
            await restartOnPort(requestedPort).ConfigureAwait(false);
            return new(PortChangeOutcome.Changed, requestedPort, $"Deluno is now at port {requestedPort}. Bookmarks to the old address will stop working.");
        }
        catch (Exception ex)
        {
            try
            {
                await restartOnPort(currentPort).ConfigureAwait(false);
                return new(PortChangeOutcome.RolledBack, currentPort, $"Deluno could not start on port {requestedPort} ({ex.Message}), so it is back on port {currentPort}.");
            }
            catch (Exception back)
            {
                return new(PortChangeOutcome.Failed, currentPort, $"Deluno could not start on port {requestedPort} or go back to {currentPort}: {back.Message}. Choose Restart Deluno from the icon menu.");
            }
        }
    }
}

/// <summary>
/// The one window the tray owns for this: asks which port Deluno's web app should be at. Plain WinForms in the system
/// font, like the rest of the tray.
/// </summary>
internal sealed class PortDialog : Form
{
    private readonly int _currentPort;
    private readonly Func<int, bool> _isInUse;
    private readonly RadioButton _useDefault;
    private readonly RadioButton _useCustom;
    private readonly TextBox _customPort;
    private readonly Label _error;
    private readonly Label _address;

    public int? ChosenPort { get; private set; }

    internal PortDialog(int currentPort, Func<int, bool> isInUse, Icon? icon)
    {
        _currentPort = currentPort;
        _isInUse = isInUse;

        Text = "Deluno";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 14, 16, 12);
        if (icon is not null)
        {
            Icon = icon;
        }

        const int width = 400;
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };

        layout.Controls.Add(new Label
        {
            Text = "Change Deluno's port",
            Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Regular),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, 8)
        });
        layout.Controls.Add(Paragraph(
            $"The port is the number at the end of Deluno's web address, like http://localhost:{PortChoice.DefaultPort}. "
            + $"Deluno is at port {currentPort} now. It will restart on the new port, and bookmarks to the old address will stop working.",
            width));

        var defaultBusy = PortChoice.DefaultPort != currentPort && isInUse(PortChoice.DefaultPort);
        _useDefault = new RadioButton
        {
            Text = defaultBusy
                ? $"Use Deluno's default port, {PortChoice.DefaultPort} (in use by another program)"
                : $"Use Deluno's default port, {PortChoice.DefaultPort}",
            AutoSize = true,
            Enabled = !defaultBusy,
            Margin = new Padding(0, 10, 0, 2)
        };
        layout.Controls.Add(_useDefault);

        var customRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        _useCustom = new RadioButton { Text = "Use this port:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) };
        _customPort = new TextBox { Width = 80, MaxLength = 5 };
        customRow.Controls.Add(_useCustom);
        customRow.Controls.Add(_customPort);
        layout.Controls.Add(customRow);

        _error = new Label { AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Color.Firebrick, Margin = new Padding(0, 6, 0, 0), Visible = false };
        layout.Controls.Add(_error);
        _address = new Label { AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 0) };
        layout.Controls.Add(_address);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var ok = new Button { Text = "Restart on this port", AutoSize = true, MinimumSize = new Size(96, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        if (!defaultBusy && currentPort == PortChoice.DefaultPort)
        {
            _useDefault.Checked = true;
        }
        else
        {
            _useCustom.Checked = true;
            _customPort.Text = currentPort.ToString(CultureInfo.InvariantCulture);
        }

        // The two options sit in different containers, and WinForms only makes radio buttons exclusive within one.
        _useDefault.CheckedChanged += (_, _) => { if (_useDefault.Checked) { _useCustom.Checked = false; } RefreshAddress(); };
        _useCustom.CheckedChanged += (_, _) => { if (_useCustom.Checked) { _useDefault.Checked = false; } RefreshAddress(); };
        _customPort.TextChanged += (_, _) =>
        {
            if (!_useCustom.Checked) { _useCustom.Checked = true; }
            _error.Visible = false;
            _error.Text = string.Empty;
            RefreshAddress();
        };
        ok.Click += (_, _) => Accept();
        Shown += (_, _) =>
        {
            Activate();
            if (_useCustom.Checked) { _customPort.Focus(); _customPort.SelectAll(); } else { ok.Focus(); }
        };
        RefreshAddress();
    }

    private static Label Paragraph(string text, int width)
        => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 6) };

    private int? Selected() => _useDefault.Checked ? PortChoice.DefaultPort : PortChoice.Parse(_customPort.Text);

    private void RefreshAddress()
        => _address.Text = Selected() is { } port ? $"Deluno will be at http://localhost:{port}/" : "Deluno will be at http://localhost:<port>/";

    private void Accept()
    {
        var text = _useDefault.Checked ? PortChoice.DefaultPort.ToString(CultureInfo.InvariantCulture) : _customPort.Text;
        if (PortChoice.Validate(text, _currentPort, _isInUse) is { } problem)
        {
            _error.Text = problem;
            _error.Visible = true;
            if (_useCustom.Checked) { _customPort.Focus(); _customPort.SelectAll(); }
            return;
        }

        ChosenPort = PortChoice.Parse(text);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Shows the dialog and returns the chosen port, or null when cancelled.</summary>
    internal static int? Ask(int currentPort, Func<int, bool> isInUse, Icon? icon)
    {
        using var dialog = new PortDialog(currentPort, isInUse, icon);
        return dialog.ShowDialog() == DialogResult.OK ? dialog.ChosenPort : null;
    }
}
