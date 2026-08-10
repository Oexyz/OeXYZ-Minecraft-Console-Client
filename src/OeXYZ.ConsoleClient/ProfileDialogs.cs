using System.Text.RegularExpressions;
using OeXYZ.Protocol;

namespace OeXYZ.ConsoleClient;

internal sealed class AccountDialog : Form
{
    private readonly TextBox nameBox = new();
    private readonly ComboBox kindBox = new();
    private readonly TextBox loginBox = new();
    private readonly Label loginLabel = new();
    private readonly AccountProfile? existing;

    public AccountDialog(AccountProfile? account)
    {
        existing = account;
        Text = account is null ? "Add account" : "Edit account";
        ClientSize = new Size(470, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);

        AddField("Friendly name", nameBox, 22);
        AddField("Account type", kindBox, 72);
        AddField(string.Empty, loginBox, 122, loginLabel);
        kindBox.DropDownStyle = ComboBoxStyle.DropDownList;
        kindBox.Items.AddRange(["Microsoft account", "Offline-mode name"]);
        kindBox.SelectedIndexChanged += (_, _) => UpdateLoginLabel();
        kindBox.SelectedIndex = account?.Kind == AccountKind.Offline ? 1 : 0;
        if (account is not null)
        {
            nameBox.Text = account.DisplayName;
            loginBox.Text = account.LoginHint;
        }

        Label safety = new()
        {
            Text = "Passwords are never requested or stored. Microsoft sign-in uses your browser.",
            ForeColor = Theme.Muted,
            Location = new Point(20, 158),
            Size = new Size(430, 34)
        };
        Button save = Theme.Button("Save", 90);
        save.Location = new Point(260, 198);
        Theme.Primary(save);
        Button cancel = Theme.Button("Cancel", 90);
        cancel.Location = new Point(358, 198);
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += SaveClicked;
        Controls.Add(safety);
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public AccountProfile? Result { get; private set; }

    private void AddField(string caption, Control control, int top, Label? suppliedLabel = null)
    {
        Label label = suppliedLabel ?? new Label();
        label.Text = caption;
        label.Location = new Point(20, top + 4);
        label.Size = new Size(170, 25);
        control.Location = new Point(194, top);
        control.Size = new Size(254, 26);
        Theme.Input(control);
        Controls.Add(label);
        Controls.Add(control);
    }

    private void UpdateLoginLabel()
    {
        bool offline = kindBox.SelectedIndex == 1;
        loginLabel.Text = offline ? "Player name" : "Email hint (optional)";
        loginBox.PlaceholderText = offline ? "Steve" : "Shown only in your profile list";
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        string name = nameBox.Text.Trim();
        string login = loginBox.Text.Trim();
        AccountKind kind = kindBox.SelectedIndex == 1 ? AccountKind.Offline : AccountKind.Microsoft;
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter a friendly profile name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (kind == AccountKind.Offline && !Regex.IsMatch(login, "^[A-Za-z0-9_]{1,16}$"))
        {
            MessageBox.Show(this, "Offline player names must contain 1-16 letters, numbers or underscores.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = new AccountProfile
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            DisplayName = name,
            Kind = kind,
            LoginHint = login,
            AccountIdentifier = existing?.Kind == kind ? existing.AccountIdentifier : null
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class ServerDialog : Form
{
    private readonly TextBox nameBox = new();
    private readonly TextBox addressBox = new();
    private readonly NumericUpDown portBox = new();
    private readonly ComboBox versionBox = new();
    private readonly CheckBox afkBox = new();
    private readonly CheckBox reconnectBox = new();
    private readonly CheckBox respawnBox = new();
    private readonly ServerProfile? existing;

    public ServerDialog(ServerProfile? server)
    {
        existing = server;
        Text = server is null ? "Add server" : "Edit server";
        ClientSize = new Size(500, 410);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);

        AddField("Friendly name", nameBox, 22);
        AddField("Server address", addressBox, 72);
        AddField("Custom port", portBox, 122);
        portBox.Minimum = 0;
        portBox.Maximum = 65535;
        portBox.ThousandsSeparator = false;
        Label portHelp = new()
        {
            Text = "0 = automatic SRV lookup, then default 25565",
            ForeColor = Theme.Muted,
            Location = new Point(190, 151),
            Size = new Size(285, 20)
        };
        Controls.Add(portHelp);
        AddField("Minecraft version", versionBox, 180);
        versionBox.DropDownStyle = ComboBoxStyle.DropDown;
        versionBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        versionBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        versionBox.Items.Add("auto");
        foreach (string version in ProtocolCatalog.LoadEmbedded().Versions
                     .Select(value => value.MinecraftVersion).Distinct().Reverse())
            versionBox.Items.Add(version);

        ConfigureCheckBox(afkBox, "Anti-AFK look movement every 45 seconds", 236);
        ConfigureCheckBox(reconnectBox, "Reconnect automatically with safe backoff", 268);
        ConfigureCheckBox(respawnBox, "Respawn automatically after death", 300);
        afkBox.Checked = true;
        reconnectBox.Checked = true;
        respawnBox.Checked = true;
        versionBox.Text = "auto";
        if (server is not null)
        {
            nameBox.Text = server.DisplayName;
            addressBox.Text = server.Address;
            portBox.Value = server.CustomPort;
            versionBox.Text = server.Version;
            afkBox.Checked = server.AntiAfk;
            reconnectBox.Checked = server.AutoReconnect;
            respawnBox.Checked = server.AutoRespawn;
        }

        Button save = Theme.Button("Save", 90);
        save.Location = new Point(290, 350);
        Theme.Primary(save);
        Button cancel = Theme.Button("Cancel", 90);
        cancel.Location = new Point(388, 350);
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += SaveClicked;
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public ServerProfile? Result { get; private set; }

    private void AddField(string caption, Control control, int top)
    {
        Label label = new() { Text = caption, Location = new Point(20, top + 4), Size = new Size(165, 25) };
        control.Location = new Point(190, top);
        control.Size = new Size(288, 26);
        Theme.Input(control);
        Controls.Add(label);
        Controls.Add(control);
    }

    private void ConfigureCheckBox(CheckBox checkBox, string text, int top)
    {
        checkBox.Text = text;
        checkBox.Location = new Point(190, top);
        checkBox.Size = new Size(288, 25);
        checkBox.ForeColor = Theme.Ink;
        Controls.Add(checkBox);
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        try
        {
            string name = nameBox.Text.Trim();
            string address = addressBox.Text.Trim();
            string version = versionBox.Text.Trim();
            if (name.Length == 0) throw new FormatException("Enter a friendly server name.");
            _ = ServerAddress.Parse(address, decimal.ToInt32(portBox.Value));
            if (!string.Equals(version, "auto", StringComparison.OrdinalIgnoreCase))
                _ = ProtocolCatalog.LoadEmbedded().Resolve(version);
            Result = new ServerProfile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                DisplayName = name,
                Address = address,
                CustomPort = decimal.ToInt32(portBox.Value),
                Version = version,
                AntiAfk = afkBox.Checked,
                AutoReconnect = reconnectBox.Checked,
                AutoRespawn = respawnBox.Checked
            };
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
