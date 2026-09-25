using Deluno.Api.Network;

namespace Deluno.Tray;

/// <summary>
/// The installer's component picker (#640): tick boxes for the five download
/// clients Deluno can dispatch to, plus Weir. Everything starts unticked.
/// Something already on this machine is shown, disabled and marked so, rather
/// than offered again -- re-running an installer over an already-configured
/// client is exactly the surprise a "super easy" installer should never
/// produce.
///
/// <para>Shown from <see cref="InstallHooks.OnFirstRun"/>, after the firewall
/// prompt: first run is Velopack's one interactive moment (its normal
/// install/update callbacks must return in seconds and show nothing), so it
/// is the only place a picker like this can appear at all.</para>
/// </summary>
public sealed class ComponentPickerDialog : Form
{
    private readonly CheckedListBox _list;
    private readonly Label _status;
    private readonly Button _btnInstall;
    private readonly Button _btnSkip;
    private readonly Button _btnLicences;
    private readonly List<(BundledComponentSpec Spec, bool AlreadyInstalled)> _entries;
    private readonly ComponentInstallService _installer;

    public IReadOnlyList<ComponentInstallResult> Results { get; private set; } = Array.Empty<ComponentInstallResult>();

    public ComponentPickerDialog(ComponentInstallService? installer = null)
    {
        _installer = installer ?? new ComponentInstallService();

        Text = "Set up download clients and Weir";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 400);
        Font = new Font("Segoe UI", 9f);

        var lblTitle = new Label
        {
            Text = "Deluno can install these for you. Nothing is ticked by default.",
            AutoSize = true,
            Location = new Point(16, 16),
            Size = new Size(430, 20)
        };

        var specs = BundledComponents.Load();
        _entries = specs.Select(spec => (spec, AlreadyInstalled: _installer.IsAlreadyInstalled(spec))).ToList();

        _list = new CheckedListBox
        {
            Location = new Point(16, 44),
            Size = new Size(428, 200),
            CheckOnClick = true
        };
        foreach (var (spec, alreadyInstalled) in _entries)
        {
            var label = alreadyInstalled
                ? $"{spec.DisplayName} ({spec.Licence}) — already installed, will not be touched"
                : $"{spec.DisplayName} ({spec.Licence})";
            _list.Items.Add(label);
        }
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].AlreadyInstalled)
            {
                // ItemCheck below refuses a tick on a disabled row; visually
                // it reads as unavailable rather than merely unticked.
                _list.SetItemCheckState(i, CheckState.Unchecked);
            }
        }
        _list.ItemCheck += (_, e) =>
        {
            if (_entries[e.Index].AlreadyInstalled)
            {
                e.NewValue = CheckState.Unchecked;
            }
        };

        _btnLicences = new Button { Text = "View licences", Location = new Point(16, 252), Size = new Size(130, 26) };
        _btnLicences.Click += (_, _) => ShowLicences();

        _status = new Label
        {
            Location = new Point(16, 288),
            Size = new Size(428, 60),
            ForeColor = SystemColors.GrayText,
            Text = "Each ticked item runs its own installer, downloaded from the project's official source and verified before it runs."
        };

        _btnInstall = new Button { Text = "Install selected", Location = new Point(216, 360), Size = new Size(120, 28) };
        _btnInstall.Click += async (_, _) => await RunInstallAsync();

        _btnSkip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(344, 360), Size = new Size(100, 28) };

        Controls.AddRange([lblTitle, _list, _btnLicences, _status, _btnInstall, _btnSkip]);
        CancelButton = _btnSkip;
    }

    private void ShowLicences()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, _entries.Select(e =>
            $"{e.Spec.DisplayName} — {e.Spec.Licence}{Environment.NewLine}{e.Spec.LicenceUrl}"));
        MessageBox.Show(text, "Third-party licences", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RunInstallAsync()
    {
        var ticked = new List<BundledComponentSpec>();
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_list.GetItemCheckState(i) == CheckState.Checked)
            {
                ticked.Add(_entries[i].Spec);
            }
        }

        if (ticked.Count == 0)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        _btnInstall.Enabled = false;
        _btnSkip.Enabled = false;
        var results = new List<ComponentInstallResult>();
        foreach (var spec in ticked)
        {
            _status.Text = $"Installing {spec.DisplayName}…";
            var result = await _installer.InstallAsync(spec, CancellationToken.None);
            results.Add(result);
        }

        Results = results;
        var failed = results.Where(r => r.Outcome is ComponentInstallOutcome.DownloadFailed or ComponentInstallOutcome.VerificationFailed or ComponentInstallOutcome.InstallerFailed).ToList();
        _status.Text = failed.Count == 0
            ? "Done. Deluno's setup wizard will pick these up automatically."
            : $"{results.Count - failed.Count} of {results.Count} finished. {string.Join(" ", failed.Select(f => f.Message))}";

        if (failed.Count == 0)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _btnSkip.Text = "Close";
            _btnSkip.Enabled = true;
            _btnInstall.Visible = false;
        }
    }

    /// <summary>Shown once, from first run. Never blocks first run itself on a failure here.</summary>
    public static IReadOnlyList<ComponentInstallResult> ShowOnce()
    {
        try
        {
            using var dialog = new ComponentPickerDialog();
            dialog.ShowDialog();
            return dialog.Results;
        }
        catch
        {
            return Array.Empty<ComponentInstallResult>();
        }
    }
}
