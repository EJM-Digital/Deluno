namespace Deluno.Tray;

public sealed class ServiceModeDialog : Form
{
    public ServiceMode SelectedMode { get; private set; } = ServiceMode.Tray;
    public string? ServiceUsername { get; private set; }
    public string? ServicePassword { get; private set; }

    private readonly RadioButton _rbTray;
    private readonly RadioButton _rbLocalSystem;
    private readonly RadioButton _rbRunAsUser;
    private readonly TextBox _tbUsername;
    private readonly TextBox _tbPassword;
    private readonly Label _lblCredentials;
    private readonly Button _btnOk;

    public ServiceModeDialog()
    {
        Text = "Startup Mode";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 340);
        Font = new Font("Segoe UI", 9f);

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

        var lblTitle = new Label
        {
            Text = "Choose how Deluno starts with Windows:",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16)
        };

        _rbTray = new RadioButton
        {
            Text = "Start at login (recommended)",
            Location = new Point(16, 48),
            AutoSize = true,
            Checked = true
        };

        var lblTrayDesc = new Label
        {
            Text = "Runs when you log in. Full access to network drives and mapped folders.",
            ForeColor = SystemColors.GrayText,
            Location = new Point(34, 68),
            Size = new Size(370, 30)
        };

        _rbLocalSystem = new RadioButton
        {
            Text = "Windows Service — Local System",
            Location = new Point(16, 108),
            AutoSize = true
        };

        var lblLocalSystemDesc = new Label
        {
            Text = "Starts at boot before login. Cannot access mapped network drives or NAS shares.",
            ForeColor = SystemColors.GrayText,
            Location = new Point(34, 128),
            Size = new Size(370, 30)
        };

        _rbRunAsUser = new RadioButton
        {
            Text = "Windows Service — Run as user",
            Location = new Point(16, 168),
            AutoSize = true
        };

        var lblRunAsUserDesc = new Label
        {
            Text = "Starts at boot with a specific account. Use this for NAS access at boot time.",
            ForeColor = SystemColors.GrayText,
            Location = new Point(34, 188),
            Size = new Size(370, 30)
        };

        _lblCredentials = new Label
        {
            Text = "Windows account credentials:",
            Location = new Point(34, 222),
            AutoSize = true,
            Visible = false
        };

        _tbUsername = new TextBox
        {
            PlaceholderText = "DOMAIN\\username or .\\localuser",
            Location = new Point(34, 242),
            Size = new Size(220, 23),
            Visible = false
        };

        _tbPassword = new TextBox
        {
            PlaceholderText = "Password",
            Location = new Point(262, 242),
            Size = new Size(130, 23),
            UseSystemPasswordChar = true,
            Visible = false
        };

        _btnOk = new Button
        {
            Text = "Apply",
            DialogResult = DialogResult.OK,
            Location = new Point(240, 296),
            Size = new Size(80, 28)
        };
        _btnOk.Click += OnOk;

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(328, 296),
            Size = new Size(80, 28)
        };

        _rbRunAsUser.CheckedChanged += (_, _) =>
        {
            var show = _rbRunAsUser.Checked;
            _lblCredentials.Visible = show;
            _tbUsername.Visible     = show;
            _tbPassword.Visible     = show;
            ClientSize = show ? new Size(420, 380) : new Size(420, 340);
        };

        Controls.AddRange([
            lblTitle,
            _rbTray, lblTrayDesc,
            _rbLocalSystem, lblLocalSystemDesc,
            _rbRunAsUser, lblRunAsUserDesc,
            _lblCredentials, _tbUsername, _tbPassword,
            _btnOk, btnCancel
        ]);

        AcceptButton = _btnOk;
        CancelButton = btnCancel;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_rbRunAsUser.Checked)
        {
            if (string.IsNullOrWhiteSpace(_tbUsername.Text))
            {
                MessageBox.Show("Please enter a Windows username.", "Deluno",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            SelectedMode = ServiceMode.ServiceRunAsUser;
            ServiceUsername = _tbUsername.Text.Trim();
            ServicePassword = _tbPassword.Text;
        }
        else if (_rbLocalSystem.Checked)
        {
            SelectedMode = ServiceMode.ServiceLocalSystem;
        }
        else
        {
            SelectedMode = ServiceMode.Tray;
        }
    }
}
