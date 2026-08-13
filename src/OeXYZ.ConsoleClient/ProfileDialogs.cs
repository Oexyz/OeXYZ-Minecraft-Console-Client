using System.Text.RegularExpressions;
using OeXYZ.Core;
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
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
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
            BrandMessageBox.Show(this, "Enter a friendly profile name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (kind == AccountKind.Offline && !Regex.IsMatch(login, "^[A-Za-z0-9_]{1,16}$"))
        {
            BrandMessageBox.Show(this, "Offline player names must contain 1-16 letters, numbers or underscores.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    private readonly TextBox groupBox = new();
    private readonly CheckBox afkBox = new();
    private readonly CheckBox reconnectBox = new();
    private readonly CheckBox respawnBox = new();
    private readonly NumericUpDown afkIntervalBox = new();
    private readonly NumericUpDown afkJitterBox = new();
    private readonly NumericUpDown afkYawBox = new();
    private readonly NumericUpDown reconnectInitialBox = new();
    private readonly NumericUpDown reconnectMaximumBox = new();
    private readonly NumericUpDown reconnectAttemptsBox = new();
    private readonly NumericUpDown staleTimeoutBox = new();
    private readonly TextBox quickCommandsBox = new();
    private readonly CheckBox startupCommandsBox = new();
    private readonly NumericUpDown startupDelayBox = new();
    private readonly TextBox startupCommandsText = new();
    private readonly ServerProfile? existing;

    public ServerDialog(ServerProfile? server)
    {
        existing = server;
        Text = server is null ? "Add server" : "Edit server";
        ClientSize = new Size(500, 700);
        AutoScroll = true;
        AutoScrollMinSize = new Size(480, 1110);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);

        AddField("Friendly name", nameBox, 22);
        AddField("Server address", addressBox, 72);
        AddField("Custom port", portBox, 122);
        portBox.Minimum = 0;
        portBox.Maximum = 65535;
        portBox.ThousandsSeparator = false;
        Label portHelp = new()
        {
            Text = "0 = automatic SRV lookup, then 25565",
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

        AddField("Session group", groupBox, 226);
        groupBox.PlaceholderText = "Optional, for example AFK or Survival";
        ConfigureCheckBox(afkBox, "Enable harmless Anti-AFK look updates", 276);
        ConfigureCheckBox(reconnectBox, "Reconnect transient failures automatically", 308);
        ConfigureCheckBox(respawnBox, "Respawn automatically after death", 340);
        ConfigureNumeric("Anti-AFK interval", afkIntervalBox, 380, 10, 3600, "seconds");
        ConfigureNumeric("Anti-AFK jitter", afkJitterBox, 430, 0, 300, "± seconds");
        ConfigureNumeric("Anti-AFK yaw", afkYawBox, 480, 0.5M, 45, "degrees");
        afkYawBox.DecimalPlaces = 1;
        afkYawBox.Increment = 0.5M;
        ConfigureNumeric("Reconnect initial", reconnectInitialBox, 530, 1, 300, "seconds");
        ConfigureNumeric("Reconnect maximum", reconnectMaximumBox, 580, 1, 3600, "seconds");
        ConfigureNumeric("Reconnect attempts", reconnectAttemptsBox, 630, 0, 9999, "0 = unlimited");
        ConfigureNumeric("Stale timeout", staleTimeoutBox, 680, 60, 900, "seconds idle");
        ConfigureMultiline("Quick commands", quickCommandsBox, 730, 78,
            "One per line; clicking a generated button sends it once (max 12)");
        ConfigureCheckBox(startupCommandsBox, "Run startup commands on connect (opt-in)", 850);
        ConfigureNumeric("Startup delay", startupDelayBox, 886, 500, 30000, "milliseconds");
        startupDelayBox.Increment = 500;
        ConfigureMultiline("Startup commands", startupCommandsText, 936, 78,
            "One per line, max 8; no repeat loop or automatic registration");
        afkBox.Checked = true;
        reconnectBox.Checked = true;
        respawnBox.Checked = true;
        afkIntervalBox.Value = 45;
        afkJitterBox.Value = 5;
        afkYawBox.Value = 7.5M;
        reconnectInitialBox.Value = 5;
        reconnectMaximumBox.Value = 60;
        staleTimeoutBox.Value = 120;
        startupDelayBox.Value = 1000;
        versionBox.Text = "auto";
        if (server is not null)
        {
            nameBox.Text = server.DisplayName;
            addressBox.Text = server.Address;
            portBox.Value = server.CustomPort;
            versionBox.Text = server.Version;
            groupBox.Text = server.Group;
            afkBox.Checked = server.AntiAfk;
            reconnectBox.Checked = server.AutoReconnect;
            respawnBox.Checked = server.AutoRespawn;
            afkIntervalBox.Value = Math.Clamp(server.AntiAfkIntervalSeconds, 10, 3600);
            afkJitterBox.Value = Math.Clamp(server.AntiAfkJitterSeconds, 0, 300);
            afkYawBox.Value = Math.Clamp((decimal)server.AntiAfkYawDegrees, 0.5M, 45M);
            reconnectInitialBox.Value = Math.Clamp(server.ReconnectInitialDelaySeconds, 1, 300);
            reconnectMaximumBox.Value = Math.Clamp(server.ReconnectMaximumDelaySeconds, 1, 3600);
            reconnectAttemptsBox.Value = Math.Clamp(server.ReconnectMaximumAttempts, 0, 9999);
            staleTimeoutBox.Value = Math.Clamp(server.StaleConnectionTimeoutSeconds, 60, 900);
            quickCommandsBox.Text = string.Join(Environment.NewLine, server.QuickCommands);
            startupCommandsBox.Checked = server.StartupCommandsEnabled;
            startupDelayBox.Value = Math.Clamp(server.StartupCommandDelayMilliseconds, 500, 30000);
            startupCommandsText.Text = string.Join(Environment.NewLine, server.StartupCommands);
        }

        Button save = Theme.Button("Save", 90);
        save.Location = new Point(290, 1044);
        Theme.Primary(save);
        Button cancel = Theme.Button("Cancel", 90);
        cancel.Location = new Point(388, 1044);
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

    private void ConfigureNumeric(
        string caption,
        NumericUpDown control,
        int top,
        decimal minimum,
        decimal maximum,
        string suffix)
    {
        Label label = new() { Text = caption, Location = new Point(20, top + 4), Size = new Size(165, 25) };
        control.Location = new Point(190, top);
        control.Size = new Size(130, 26);
        control.Minimum = minimum;
        control.Maximum = maximum;
        Theme.Input(control);
        Label help = new()
        {
            Text = suffix,
            Location = new Point(330, top + 4),
            Size = new Size(148, 24),
            ForeColor = Theme.Muted
        };
        Controls.Add(label);
        Controls.Add(control);
        Controls.Add(help);
    }

    private void ConfigureMultiline(string caption, TextBox control, int top, int height, string helpText)
    {
        Label label = new() { Text = caption, Location = new Point(20, top + 4), Size = new Size(165, 25) };
        control.Location = new Point(190, top);
        control.Size = new Size(288, height);
        control.Multiline = true;
        control.ScrollBars = ScrollBars.Vertical;
        Theme.Input(control);
        Label help = new()
        {
            Text = helpText,
            Location = new Point(190, top + height + 3),
            Size = new Size(288, 34),
            ForeColor = Theme.Muted
        };
        Controls.Add(label);
        Controls.Add(control);
        Controls.Add(help);
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
            if (reconnectMaximumBox.Value < reconnectInitialBox.Value)
                throw new FormatException("The maximum reconnect delay cannot be shorter than the initial delay.");
            ServerProfile basis = existing ?? new ServerProfile { Id = Guid.NewGuid() };
            Result = basis with
            {
                DisplayName = name,
                Address = address,
                CustomPort = decimal.ToInt32(portBox.Value),
                Version = version,
                Group = groupBox.Text.Trim(),
                AntiAfk = afkBox.Checked,
                AntiAfkIntervalSeconds = decimal.ToInt32(afkIntervalBox.Value),
                AntiAfkJitterSeconds = decimal.ToInt32(afkJitterBox.Value),
                AntiAfkYawDegrees = decimal.ToSingle(afkYawBox.Value),
                AutoReconnect = reconnectBox.Checked,
                ReconnectInitialDelaySeconds = decimal.ToInt32(reconnectInitialBox.Value),
                ReconnectMaximumDelaySeconds = decimal.ToInt32(reconnectMaximumBox.Value),
                ReconnectMaximumAttempts = decimal.ToInt32(reconnectAttemptsBox.Value),
                StaleConnectionTimeoutSeconds = decimal.ToInt32(staleTimeoutBox.Value),
                AutoRespawn = respawnBox.Checked,
                QuickCommands = ParseCommands(quickCommandsBox.Text, 12),
                StartupCommandsEnabled = startupCommandsBox.Checked,
                StartupCommandDelayMilliseconds = decimal.ToInt32(startupDelayBox.Value),
                StartupCommands = ParseCommands(startupCommandsText.Text, 8, rejectSensitive: true)
            };
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            BrandMessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static List<string> ParseCommands(string text, int maximum, bool rejectSensitive = false)
    {
        List<string> commands = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(command => command.Length <= 256)
            .Take(maximum)
            .ToList();
        if (commands.Any(command => !command.StartsWith("/", StringComparison.Ordinal)))
            throw new FormatException("Quick and startup commands must start with '/'.");
        if (rejectSensitive && commands.Any(SensitiveDataRedactor.IsSensitiveCommand))
            throw new FormatException("Login, registration and password commands cannot run automatically. Send them manually instead.");
        return commands;
    }
}

internal sealed class SettingsDialog : Form
{
    private readonly ApplicationSettings existing;
    private readonly CheckBox minimizeToTray = new();
    private readonly CheckBox keepRunningOnClose = new();
    private readonly CheckBox notifications = new();
    private readonly CheckBox disconnect = new();
    private readonly CheckBox reconnect = new();
    private readonly CheckBox death = new();
    private readonly CheckBox mention = new();
    private readonly CheckBox privateMessage = new();
    private readonly ComboBox retention = new();
    private readonly CheckBox restoreSessions = new();
    private readonly CheckBox protocolInspector = new();

    public SettingsDialog(ApplicationSettings settings)
    {
        existing = settings;
        Text = "OeXYZ settings";
        ClientSize = new Size(680, 570);
        MinimumSize = new Size(620, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 14,
            Padding = new Padding(24, 20, 24, 18),
            BackColor = Theme.Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        for (int index = 1; index < 12; index++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        Label heading = Theme.Heading("Windows & notifications", 15F);
        heading.Text = "Windows & notifications";
        layout.Controls.Add(heading, 0, 0);
        AddCheck(layout, minimizeToTray, "Minimize the window to the system tray", 1, settings.MinimizeToTray);
        AddCheck(layout, keepRunningOnClose, "Keep sessions running when the window is closed", 2, settings.KeepRunningOnClose);
        AddCheck(layout, notifications, "Enable local Windows notifications", 3, settings.NotificationsEnabled);
        AddCheck(layout, disconnect, "Notify on disconnect", 4, settings.NotifyDisconnect);
        AddCheck(layout, reconnect, "Notify after a successful reconnect", 5, settings.NotifyReconnect);
        AddCheck(layout, death, "Notify when the player dies", 6, settings.NotifyDeath);
        AddCheck(layout, mention, "Notify when the player name is mentioned", 7, settings.NotifyMention);
        AddCheck(layout, privateMessage, "Notify for recognized private messages", 8, settings.NotifyPrivateMessage);
        AddCheck(layout, restoreSessions, "Restore the previous sessions automatically on startup (opt-in)", 9,
            settings.RestoreSessionsOnStartup);
        AddCheck(layout, protocolInspector, "Developer protocol inspector for newly opened sessions", 10,
            settings.ProtocolInspectorEnabled);

        FlowLayoutPanel retentionRow = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background
        };
        Label retentionLabel = new()
        {
            Text = "Keep logs (max 300 MB):",
            Width = 180,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Ink
        };
        retention.Items.AddRange(["30 days", "90 days", "Unlimited"]);
        retention.DropDownStyle = ComboBoxStyle.DropDownList;
        retention.Width = 160;
        retention.SelectedIndex = settings.LogRetentionDays switch { 30 => 0, 0 => 2, _ => 1 };
        Theme.Input(retention);
        retentionRow.Controls.Add(retentionLabel);
        retentionRow.Controls.Add(retention);
        layout.Controls.Add(retentionRow, 0, 11);

        Label explanation = new()
        {
            Dock = DockStyle.Fill,
            Text = "Closing only continues in the tray when you explicitly enable it. " +
                   "Notification event preferences can be selected while notifications are off. " +
                   "The oldest closed logs are removed automatically above 300 MB. " +
                   "Exit from the tray menu always shuts down sessions cleanly.",
            ForeColor = Theme.Muted
        };
        layout.Controls.Add(explanation, 0, 12);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Background
        };
        Button cancel = Theme.Button("Cancel", 90);
        cancel.DialogResult = DialogResult.Cancel;
        Button save = Theme.Button("Save", 90);
        Theme.Primary(save);
        save.Click += (_, _) => Save();
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        layout.Controls.Add(actions, 0, 13);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

    }

    public ApplicationSettings? Result { get; private set; }

    private static void AddCheck(
        TableLayoutPanel layout,
        CheckBox checkBox,
        string text,
        int row,
        bool value)
    {
        checkBox.Text = text;
        checkBox.Checked = value;
        checkBox.Dock = DockStyle.Fill;
        checkBox.ForeColor = Theme.Ink;
        layout.Controls.Add(checkBox, 0, row);
    }

    private void Save()
    {
        Result = existing with
        {
            MinimizeToTray = minimizeToTray.Checked,
            KeepRunningOnClose = keepRunningOnClose.Checked,
            NotificationsEnabled = notifications.Checked,
            NotifyDisconnect = disconnect.Checked,
            NotifyReconnect = reconnect.Checked,
            NotifyDeath = death.Checked,
            NotifyMention = mention.Checked,
            NotifyPrivateMessage = privateMessage.Checked,
            RestoreSessionsOnStartup = restoreSessions.Checked,
            ProtocolInspectorEnabled = protocolInspector.Checked,
            LogRetentionDays = retention.SelectedIndex switch { 0 => 30, 2 => 0, _ => 90 }
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
