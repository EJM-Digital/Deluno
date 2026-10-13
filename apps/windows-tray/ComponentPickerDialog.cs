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
    private readonly Panel _usenetPanel;
    private readonly Label _usenetHeading;
    private readonly Label _sabLabel;
    private readonly TextBox _sabUser;
    private readonly TextBox _sabPassword;
    private readonly Label _nzbLabel;
    private readonly TextBox _nzbUser;
    private readonly TextBox _nzbPassword;
    private readonly CheckBox _sameLogin;
    private readonly Button _btnInstall;
    private readonly Button _btnSkip;
    private readonly Button _btnLicences;
    private readonly Label _lblFolders;
    private readonly Label _lblCompleted;
    private readonly TextBox _completedRoot;
    private readonly Button _btnBrowseCompleted;
    private readonly Label _lblIncomplete;
    private readonly TextBox _incompleteRoot;
    private readonly Button _btnBrowseIncomplete;
    private readonly ListView _progressList;
    private readonly Label _summary;
    private readonly Button _btnOpen;
    private readonly Button _btnClose;
    private readonly Dictionary<string, ListViewItem> _progressRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(BundledComponentSpec Spec, bool AlreadyInstalled)> _entries;
    private readonly ComponentInstallService _installer;

    public IReadOnlyList<ComponentInstallResult> Results { get; private set; } = Array.Empty<ComponentInstallResult>();

    /// <param name="preTick">Apps to tick when it opens: a retry after GitHub's limit, or an app asked for on the Companion Tools page. Empty on first run, where nothing is ticked.</param>
    public ComponentPickerDialog(ComponentInstallService? installer = null, IReadOnlyCollection<string>? preTick = null)
    {
        _installer = installer ?? new ComponentInstallService();

        Text = "Set up download clients and Weir";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 500);
        Font = new Font("Segoe UI", 9f);

        var lblTitle = new Label
        {
            Text = preTick is { Count: > 0 }
                ? "These are ticked because you asked for them, or because an earlier try had to wait. Press Install selected to try again."
                : "Deluno can install these for you. Nothing is ticked by default.",
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
            else if (preTick is not null && preTick.Contains(_entries[i].Spec.Kind, StringComparer.OrdinalIgnoreCase))
            {
                _list.SetItemCheckState(i, CheckState.Checked);
            }
        }
        _list.ItemCheck += (_, e) =>
        {
            if (_entries[e.Index].AlreadyInstalled)
            {
                e.NewValue = CheckState.Unchecked;
            }

            // The tick has not landed yet; lay the login boxes out once it has.
            BeginInvoke(UpdateUsenetPanel);
        };

        _btnLicences = new Button { Text = "View licences", Location = new Point(16, 250), Size = new Size(130, 26) };
        _btnLicences.Click += (_, _) => ShowLicences();

        var layout = DownloadLayout.Default();
        _lblFolders = new Label
        {
            Text = "Where the download clients you tick save their files. Deluno sets every one of them to these.",
            Location = new Point(16, 288),
            Size = new Size(428, 34)
        };
        _lblCompleted = new Label { Text = "Finished downloads (a Movies and a TV folder go inside)", AutoSize = true, Location = new Point(16, 326) };
        _completedRoot = new TextBox { Text = layout.CompletedRoot, Location = new Point(16, 346), Size = new Size(338, 23) };
        _btnBrowseCompleted = new Button { Text = "Browse…", Location = new Point(362, 345), Size = new Size(82, 25) };
        _btnBrowseCompleted.Click += (_, _) => Browse(_completedRoot);
        _lblIncomplete = new Label { Text = "Downloads in progress (one folder per client goes inside)", AutoSize = true, Location = new Point(16, 376) };
        _incompleteRoot = new TextBox { Text = layout.IncompleteRoot, Location = new Point(16, 396), Size = new Size(338, 23) };
        _btnBrowseIncomplete = new Button { Text = "Browse…", Location = new Point(362, 395), Size = new Size(82, 25) };
        _btnBrowseIncomplete.Click += (_, _) => Browse(_incompleteRoot);

        _status = new Label
        {
            Location = new Point(16, 426),
            Size = new Size(428, 32),
            ForeColor = SystemColors.GrayText,
            Text = "Each ticked item runs its own installer, downloaded from the project's official source and verified before it runs."
        };

        // The logins for the two Usenet clients (#1142): shown only while SABnzbd or NZBGet is ticked.
        _usenetPanel = new Panel { Location = new Point(16, 426), Size = new Size(428, 0), Visible = false };
        _usenetHeading = new Label { Text = "Choose the login you will use to sign in to the Usenet client's own web page.", Location = new Point(0, 0), Size = new Size(428, 32) };
        _sabLabel = new Label { Text = "SABnzbd username and password", AutoSize = true };
        _sabUser = new TextBox { Size = new Size(206, 23), AccessibleName = "SABnzbd username" };
        _sabPassword = new TextBox { Size = new Size(206, 23), UseSystemPasswordChar = true, AccessibleName = "SABnzbd password" };
        _nzbLabel = new Label { Text = "NZBGet username and password", AutoSize = true };
        _nzbUser = new TextBox { Size = new Size(206, 23), AccessibleName = "NZBGet username" };
        _nzbPassword = new TextBox { Size = new Size(206, 23), UseSystemPasswordChar = true, AccessibleName = "NZBGet password" };
        _sameLogin = new CheckBox { Text = "Use the same login for both", AutoSize = true, AccessibleName = "Use the same login for both" };
        _sameLogin.CheckedChanged += (_, _) => UpdateUsenetPanel();
        _usenetPanel.Controls.AddRange([_usenetHeading, _sabLabel, _sabUser, _sabPassword, _nzbLabel, _nzbUser, _nzbPassword, _sameLogin]);

        _btnInstall = new Button { Text = "Install selected", Location = new Point(216, 462), Size = new Size(120, 28) };
        _btnInstall.Click += async (_, _) => await RunInstallAsync();

        _btnSkip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(344, 462), Size = new Size(100, 28) };

        // The progress and finish view (#1140), shown in place of the picker once Install is pressed.
        _progressList = new ListView
        {
            Location = new Point(16, 44),
            Size = new Size(428, 220),
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Visible = false
        };
        _progressList.Columns.Add("Component", 110);
        _progressList.Columns.Add("What is happening", 300);
        _summary = new Label { Location = new Point(16, 272), Size = new Size(428, 150), Visible = false };
        _btnOpen = new Button { Text = "Open Deluno", Location = new Point(216, 462), Size = new Size(120, 28), Visible = false, Enabled = false };
        _btnOpen.Click += (_, _) => OpenDeluno();
        _btnClose = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(344, 462), Size = new Size(100, 28), Visible = false, Enabled = false };

        Controls.AddRange([lblTitle, _list, _btnLicences, _lblFolders, _lblCompleted, _completedRoot, _btnBrowseCompleted, _lblIncomplete, _incompleteRoot, _btnBrowseIncomplete,
            _status, _usenetPanel, _btnInstall, _btnSkip, _progressList, _summary, _btnOpen, _btnClose]);
        CancelButton = _btnSkip;
        _title = lblTitle;
        UpdateUsenetPanel();
    }

    private Label? _title;

    private bool IsTicked(string kind)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Spec.Kind == kind && _list.GetItemCheckState(i) == CheckState.Checked)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shows a username and password for each ticked Usenet client, and moves the buttons down to make room (#1142).</summary>
    private void UpdateUsenetPanel()
    {
        var sab = IsTicked("sabnzbd");
        var nzb = IsTicked("nzbget");
        _usenetPanel.Visible = sab || nzb;

        var same = sab && nzb;
        _sameLogin.Visible = same;
        var showNzb = nzb && !(same && _sameLogin.Checked);

        var y = _usenetHeading.Bottom;
        void Place(Label label, TextBox user, TextBox password, bool visible)
        {
            label.Visible = user.Visible = password.Visible = visible;
            if (!visible)
            {
                return;
            }

            label.Location = new Point(0, y);
            user.Location = new Point(0, y + 18);
            password.Location = new Point(214, y + 18);
            y += 48;
        }

        Place(_sabLabel, _sabUser, _sabPassword, sab);
        Place(_nzbLabel, _nzbUser, _nzbPassword, showNzb);
        if (same)
        {
            _sameLogin.Location = new Point(0, y);
            y += 26;
        }

        var extra = _usenetPanel.Visible ? y + 6 : 0;
        _usenetPanel.Height = extra;
        _status.Top = 426 + extra;
        _btnInstall.Top = 462 + extra;
        _btnSkip.Top = 462 + extra;
        ClientSize = new Size(460, 500 + extra);
    }

    private static void Browse(TextBox target)
    {
        using var folders = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = "Choose a folder" };
        if (folders.ShowDialog() == DialogResult.OK)
        {
            target.Text = folders.SelectedPath;
        }
    }

    private static void OpenDeluno()
    {
        // Only because the person pressed the button. Deluno may still be starting; the page loads once it is up.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{AppSettings.Load().Port}") { UseShellExecute = true });
        }
        catch
        {
            // Nothing to do: the tray's Open Deluno does the same.
        }
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

        var layout = new DownloadLayout(_completedRoot.Text.Trim(), _incompleteRoot.Text.Trim());
        if (ticked.Any(spec => spec.Category == BundledComponentSpec.DownloadClientCategory) && layout.Problem() is { } problem)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = problem;
            return;
        }

        var (logins, loginProblem) = UsenetLogins.Resolve(
            ticked.Any(spec => spec.Kind == "sabnzbd"), ticked.Any(spec => spec.Kind == "nzbget"), _sameLogin.Checked,
            _sabUser.Text.Trim(), _sabPassword.Text, _nzbUser.Text.Trim(), _nzbPassword.Text);
        if (loginProblem is not null)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = loginProblem;
            return;
        }

        // The logins are held by the installer from here; clear the boxes so nothing is left on the form.
        _sabPassword.Clear();
        _nzbPassword.Clear();

        ShowProgressView(ticked);
        var results = new List<ComponentInstallResult>();
        foreach (var spec in ticked)
        {
            // On a worker thread so the window keeps repainting while an installer is hashed, run or waited on.
            var result = await Task.Run(() => _installer.InstallAsync(spec, CancellationToken.None, progress => BeginInvoke(() => ShowProgress(progress)), layout, logins.GetValueOrDefault(spec.Kind)));
            results.Add(result);
        }

        Results = results;
        ShowFinish(ticked, results);
    }

    private void ShowProgressView(IReadOnlyList<BundledComponentSpec> ticked)
    {
        foreach (var control in new Control[] { _list, _btnLicences, _lblFolders, _lblCompleted, _completedRoot, _btnBrowseCompleted, _lblIncomplete, _incompleteRoot, _btnBrowseIncomplete, _status, _usenetPanel, _btnInstall, _btnSkip })
        {
            control.Visible = false;
        }

        _title!.Text = "Installing what you ticked. This can take a few minutes.";
        _progressList.Visible = true;
        _summary.Visible = true;
        _btnOpen.Visible = true;
        _btnClose.Visible = true;
        // Closing the window mid-install would leave an installer running with nobody told how it went.
        ControlBox = false;
        CancelButton = null;
        foreach (var spec in ticked)
        {
            var row = new ListViewItem(spec.DisplayName);
            row.SubItems.Add("Waiting");
            _progressList.Items.Add(row);
            _progressRows[spec.Kind] = row;
        }
    }

    private void ShowProgress(ComponentProgress progress)
    {
        if (!_progressRows.TryGetValue(progress.Kind, out var row))
        {
            return;
        }

        row.SubItems[1].Text = Describe(progress);
        row.ForeColor = progress.Stage == ComponentStage.Failed ? Color.Firebrick : SystemColors.WindowText;
    }

    /// <summary>The words shown for one step: what is being done, or, for a failure, why.</summary>
    internal static string Describe(ComponentProgress progress) => progress.Stage switch
    {
        ComponentStage.Waiting => "Waiting",
        ComponentStage.Downloading => "Downloading…",
        ComponentStage.Checking => "Checking it is the genuine installer…",
        ComponentStage.Installing => "Installing…",
        ComponentStage.Starting => string.Equals(progress.Kind, "weir", StringComparison.OrdinalIgnoreCase) ? Deluno.Contracts.WeirStartWait.Notice : "Started, waiting until it answers…",
        ComponentStage.SettingUp => "Setting up its categories and folders…",
        ComponentStage.Done => string.IsNullOrWhiteSpace(progress.Detail) ? "Done" : progress.Detail!,
        _ => $"Failed: {progress.Detail}"
    };

    private void ShowFinish(IReadOnlyList<BundledComponentSpec> ticked, IReadOnlyList<ComponentInstallResult> results)
    {
        _title!.Text = "Finished.";
        _summary.Text = Summarise(ticked, results);
        _btnOpen.Enabled = true;
        _btnClose.Enabled = true;
        ControlBox = true;
        AcceptButton = _btnOpen;
    }

    /// <summary>What was installed and what was not, in plain words, for the finish window (#1140).</summary>
    internal static string Summarise(IReadOnlyList<BundledComponentSpec> ticked, IReadOnlyList<ComponentInstallResult> results)
    {
        string NameOf(ComponentInstallResult result) => ticked.FirstOrDefault(spec => spec.Kind == result.Kind)?.DisplayName ?? result.Kind;
        var installed = results.Where(r => r.Outcome is ComponentInstallOutcome.Installed or ComponentInstallOutcome.AlreadyInstalled).ToList();
        var needsSetup = results.Where(r => r.Outcome == ComponentInstallOutcome.InstalledNeedsSetup).ToList();
        var failed = results.Except(installed).Except(needsSetup).ToList();

        var lines = new List<string>();
        if (installed.Count > 0)
        {
            lines.Add("Installed: " + string.Join(", ", installed.Select(NameOf)) + ".");
        }

        foreach (var result in needsSetup)
        {
            lines.Add($"Installed, but needs a look: {result.Message}");
        }

        foreach (var result in failed)
        {
            lines.Add($"Not installed: {NameOf(result)}. {result.Message}");
        }

        lines.Add(string.Empty);
        lines.Add("Open Deluno to connect what was installed. If the page does not load yet, give Deluno a moment and refresh.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The picker again, with these apps ticked (a retry after GitHub's limit, or an ask from the Companion Tools page).
    /// What was installed, and what has to wait again, is written back to the pending list.
    /// </summary>
    public static IReadOnlyList<ComponentInstallResult> ShowFor(IReadOnlyCollection<string> kinds)
    {
        try
        {
            using var dialog = new ComponentPickerDialog(preTick: kinds);
            dialog.ShowDialog();
            PendingInstallRecorder.Record(dialog.Results);
            return dialog.Results;
        }
        catch
        {
            return Array.Empty<ComponentInstallResult>();
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
