using System.Diagnostics;
using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.ConsoleClient;

internal sealed class MainForm : Form
{
    private readonly ProfileRepository repository = new(AppPaths.Profiles);
    private readonly AuthenticationService authentication = new();
    private readonly object saveLock = new();
    private readonly bool demoMode;
    private readonly bool publicDemoMode;
    private readonly BrandListView accounts = new();
    private readonly BrandListView servers = new();
    private readonly BrandTabControl sessions = new();
    private readonly Label summary = new();
    private readonly Button connect = Theme.Button("Connect", 130);
    private readonly CancellationTokenSource formLifetime = new();
    private readonly Dictionary<Guid, CachedServerStatus> serverStatusCache = [];
    private readonly NotifyIcon trayIcon = new();
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ImageList serverIcons = new()
    {
        ImageSize = new Size(32, 32),
        ColorDepth = ColorDepth.Depth32Bit,
        TransparentColor = Color.Transparent
    };
    private ProfileDocument profiles;
    private bool closing;
    private bool allowExit;
    private bool trayHintShown;

    public MainForm(string[] args)
    {
        bool integrationDemoMode = args.Any(argument =>
            string.Equals(argument, "--integration-demo", StringComparison.OrdinalIgnoreCase));
        publicDemoMode = args.Any(argument =>
            string.Equals(argument, "--public-demo", StringComparison.OrdinalIgnoreCase));
        demoMode = integrationDemoMode || publicDemoMode;
        profiles = publicDemoMode
            ? CreatePublicDemoProfiles()
            : integrationDemoMode
                ? CreateDemoProfiles()
                : LoadProfiles();
        Text = publicDemoMode
            ? "OeXYZ Console Client · Public Compatibility Demo"
            : integrationDemoMode
                ? "OeXYZ Console Client · Integration Demo"
                : "OeXYZ Console Client";
        ClientSize = new Size(1220, 760);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = LoadIcon();
        BuildTray();

        Panel sidebar = BuildSidebar();
        BuildWorkspace();
        Controls.Add(sessions);
        Controls.Add(sidebar);
        FormClosing += FormIsClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && profiles.Settings.MinimizeToTray)
                HideToTray();
        };
        Shown += (_, _) =>
        {
            Theme.ApplyDarkTitleBar(this);
            QueueStatusRefresh();
            if (demoMode)
            {
                WindowState = FormWindowState.Maximized;
                BeginInvoke(ConnectSelected);
            }
        };
        RefreshProfiles();
    }

    private Panel BuildSidebar()
    {
        Panel sidebar = new()
        {
            Dock = DockStyle.Left,
            Width = 360,
            BackColor = Theme.Sidebar,
            Padding = new Padding(20, 16, 20, 16)
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            BackColor = Theme.Sidebar,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        Panel brand = new() { Dock = DockStyle.Fill, BackColor = Theme.Sidebar };
        LogoControl logo = new() { Location = new Point(0, 1), Size = new Size(64, 64) };
        WordmarkControl wordmark = new() { Location = new Point(76, -3), Size = new Size(180, 52) };
        Label subtitle = new()
        {
            Text = "CONSOLE CLIENT",
            Font = new Font("Consolas", 9F, FontStyle.Bold),
            ForeColor = Theme.BlueBright,
            Location = new Point(79, 51),
            AutoSize = true
        };
        brand.Controls.Add(logo);
        brand.Controls.Add(wordmark);
        brand.Controls.Add(subtitle);
        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(Heading("Accounts"), 0, 1);
        ConfigureList(accounts, "Profile", "Authentication");
        accounts.DoubleClick += (_, _) => EditAccount();
        layout.Controls.Add(accounts, 0, 2);
        layout.Controls.Add(ButtonRow(
            ("Add", AddAccount),
            ("Edit", EditAccount),
            ("Remove", RemoveAccount)), 0, 3);
        layout.Controls.Add(Heading("Servers"), 0, 4);
        ConfigureList(servers, "Server", "Address");
        servers.SmallImageList = serverIcons;
        servers.DoubleClick += (_, _) => EditServer();
        layout.Controls.Add(servers, 0, 5);
        layout.Controls.Add(ButtonRow(
            ("Add", AddServer),
            ("Edit", EditServer),
            ("Remove", RemoveServer)), 0, 6);
        summary.Dock = DockStyle.Fill;
        summary.ForeColor = Theme.Muted;
        summary.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(summary, 0, 7);

        FlowLayoutPanel mainActions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 3),
            BackColor = Theme.Sidebar
        };
        connect.Height = 40;
        Theme.Primary(connect);
        connect.Click += (_, _) => ConnectSelected();
        Button logs = Theme.Button("Logs", 78);
        logs.Height = 40;
        logs.Click += (_, _) => OpenPath(AppPaths.Logs);
        Button data = Theme.Button("Data", 78);
        data.Height = 40;
        data.Click += (_, _) => OpenPath(AppPaths.Root);
        mainActions.Controls.Add(connect);
        mainActions.Controls.Add(logs);
        mainActions.Controls.Add(data);
        layout.Controls.Add(mainActions, 0, 8);

        FlowLayoutPanel auxiliary = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Sidebar
        };
        Button settings = Theme.Button("Settings", 92);
        Button updates = Theme.Button("Updates", 92);
        Button about = Theme.Button("About", 92);
        settings.Click += (_, _) => ShowSettings();
        updates.Click += (_, _) => UpdateDialog.ShowFor(this);
        about.Click += (_, _) => ShowAbout();
        auxiliary.Controls.Add(settings);
        auxiliary.Controls.Add(updates);
        auxiliary.Controls.Add(about);
        layout.Controls.Add(auxiliary, 0, 9);
        sidebar.Controls.Add(layout);
        return sidebar;
    }

    private void BuildWorkspace()
    {
        sessions.Dock = DockStyle.Fill;
        sessions.Font = new Font("Bahnschrift SemiBold", 9F, FontStyle.Bold);
        sessions.HotTrack = true;
        TabPage welcome = new("Welcome") { BackColor = Theme.Background };
        TableLayoutPanel center = new()
        {
            Anchor = AnchorStyles.None,
            Size = new Size(690, 460),
            ColumnCount = 2,
            RowCount = 5,
            BackColor = Theme.Surface,
            Padding = new Padding(38)
        };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Panel titlePanel = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        LogoControl mark = new() { Location = new Point(0, 2), Size = new Size(88, 88) };
        Label title = new()
        {
            Text = "Stay connected.\nRender nothing.",
            ForeColor = Theme.Ink,
            Font = new Font("Bahnschrift SemiBold", 25F, FontStyle.Bold),
            Location = new Point(108, 0),
            Size = new Size(480, 104)
        };
        titlePanel.Controls.Add(mark);
        titlePanel.Controls.Add(title);
        center.Controls.Add(titlePanel, 0, 0);
        center.SetColumnSpan(titlePanel, 2);
        Label intro = new()
        {
            Text = "A native Minecraft Java console for chat and reliable AFK sessions.",
            ForeColor = Color.FromArgb(185, 196, 211),
            Font = new Font("Segoe UI", 11F),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        center.Controls.Add(intro, 0, 1);
        center.SetColumnSpan(intro, 2);
        center.Controls.Add(Feature("NO RENDERER", "Low CPU and memory use"), 0, 2);
        center.Controls.Add(Feature("MC 1.8 - 26.2", "Protocol auto-detection"), 1, 2);
        center.Controls.Add(Feature("CHAT READY", "Messages, commands, respawn"), 0, 3);
        center.Controls.Add(Feature("AFK SAFE", "Keepalive and reconnect"), 1, 3);
        Label foot = new()
        {
            Text = "Select an account and server, then click Connect.",
            ForeColor = Theme.Green,
            Font = new Font("Consolas", 9F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        center.Controls.Add(foot, 0, 4);
        center.SetColumnSpan(foot, 2);
        welcome.Controls.Add(center);
        welcome.Resize += (_, _) =>
        {
            center.Left = Math.Max(20, (welcome.ClientSize.Width - center.Width) / 2);
            center.Top = Math.Max(20, (welcome.ClientSize.Height - center.Height) / 2);
        };
        sessions.TabPages.Add(welcome);
    }

    private static Control Heading(string text)
    {
        Label label = Theme.Heading(text, 10.5F);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.BottomLeft;
        return label;
    }

    private static Panel Feature(string heading, string detail)
    {
        Panel panel = new() { Dock = DockStyle.Fill, Margin = new Padding(5), BackColor = Theme.Raised };
        Label headingLabel = new()
        {
            Text = heading,
            ForeColor = Theme.BlueBright,
            Font = new Font("Consolas", 9F, FontStyle.Bold),
            Location = new Point(12, 10),
            AutoSize = true
        };
        Label detailLabel = new()
        {
            Text = detail,
            ForeColor = Color.FromArgb(198, 204, 215),
            Location = new Point(12, 34),
            AutoSize = true
        };
        panel.Controls.Add(headingLabel);
        panel.Controls.Add(detailLabel);
        return panel;
    }

    private static FlowLayoutPanel ButtonRow(params (string Text, Action Action)[] buttons)
    {
        FlowLayoutPanel row = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 3),
            BackColor = Theme.Sidebar
        };
        foreach ((string text, Action action) in buttons)
        {
            Button button = Theme.Button(text, text == "Remove" ? 84 : 74);
            button.Click += (_, _) => action();
            row.Controls.Add(button);
        }
        return row;
    }

    private static void ConfigureList(ListView list, string firstColumn, string secondColumn)
    {
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.ShowItemToolTips = true;
        list.Columns.Add(firstColumn, 145);
        list.Columns.Add(secondColumn, 190);
        list.Resize += (_, _) =>
        {
            if (list.Columns.Count > 1) list.Columns[1].Width = Math.Max(80, list.ClientSize.Width - list.Columns[0].Width - 2);
        };
    }

    private ProfileDocument LoadProfiles()
    {
        try { return repository.Load(); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "OeXYZ Console Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return new ProfileDocument();
        }
    }

    private void RefreshProfiles()
    {
        Guid? selectedAccount = SelectedId(accounts);
        Guid? selectedServer = SelectedId(servers);
        accounts.BeginUpdate();
        servers.BeginUpdate();
        try
        {
            accounts.Items.Clear();
            foreach (AccountProfile account in profiles.Accounts)
            {
                ListViewItem item = new(account.DisplayName) { Tag = account.Id };
                item.SubItems.Add(account.Kind == AccountKind.Offline ? $"Offline · {account.LoginHint}" : "Microsoft · browser sign-in");
                accounts.Items.Add(item);
            }
            servers.Items.Clear();
            foreach (ServerProfile server in profiles.Servers)
            {
                ListViewItem item = new(server.DisplayName) { Tag = server.Id };
                item.SubItems.Add(server.Address + (server.CustomPort > 0 ? $":{server.CustomPort}" : " · auto port"));
                servers.Items.Add(item);
            }
            RestoreSelection(accounts, selectedAccount);
            RestoreSelection(servers, selectedServer);
        }
        finally
        {
            accounts.EndUpdate();
            servers.EndUpdate();
        }
        summary.Text = $"{profiles.Accounts.Count} account{(profiles.Accounts.Count == 1 ? string.Empty : "s")}  ·  " +
                       $"{profiles.Servers.Count} server{(profiles.Servers.Count == 1 ? string.Empty : "s")}";
        QueueStatusRefresh();
    }

    private void AddAccount()
    {
        using AccountDialog dialog = new(null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        profiles.Accounts.Add(dialog.Result);
        SaveAndRefresh(dialog.Result.Id, null);
    }

    private void EditAccount()
    {
        AccountProfile? profile = SelectedAccount();
        if (profile is null) { SelectHint("account"); return; }
        using AccountDialog dialog = new(profile);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        profiles.Accounts[profiles.Accounts.IndexOf(profile)] = dialog.Result;
        SaveAndRefresh(dialog.Result.Id, null);
    }

    private void RemoveAccount()
    {
        AccountProfile? profile = SelectedAccount();
        if (profile is null) { SelectHint("account"); return; }
        if (MessageBox.Show(this,
                $"Remove '{profile.DisplayName}' from the profile list?\n\nMicrosoft tokens are stored separately with Windows encryption.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        profiles.Accounts.Remove(profile);
        SaveAndRefresh(null, null);
    }

    private void AddServer()
    {
        using ServerDialog dialog = new(null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        profiles.Servers.Add(dialog.Result);
        SaveAndRefresh(null, dialog.Result.Id);
    }

    private void EditServer()
    {
        ServerProfile? profile = SelectedServer();
        if (profile is null) { SelectHint("server"); return; }
        using ServerDialog dialog = new(profile);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        profiles.Servers[profiles.Servers.IndexOf(profile)] = dialog.Result;
        SaveAndRefresh(null, dialog.Result.Id);
    }

    private void RemoveServer()
    {
        ServerProfile? profile = SelectedServer();
        if (profile is null) { SelectHint("server"); return; }
        if (MessageBox.Show(this, $"Remove server profile '{profile.DisplayName}'?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        profiles.Servers.Remove(profile);
        SaveAndRefresh(null, null);
    }

    private void SaveAndRefresh(Guid? accountId, Guid? serverId)
    {
        try
        {
            if (!demoMode)
            {
                lock (saveLock) repository.Save(profiles);
            }
            RefreshProfiles();
            RestoreSelection(accounts, accountId);
            RestoreSelection(servers, serverId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Profiles could not be saved:\n" + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ConnectSelected()
    {
        AccountProfile? account = SelectedAccount();
        ServerProfile? server = SelectedServer();
        if (account is null || server is null)
        {
            MessageBox.Show(this, "Select one account and one server first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ConnectProfile(account, server);
    }

    private void ConnectProfile(AccountProfile account, ServerProfile server)
    {
        SessionTab? existing = sessions.TabPages.OfType<SessionTab>().FirstOrDefault(page =>
            page.Session.Account.Id == account.Id && page.Session.Server.Id == server.Id);
        if (existing is not null)
        {
            sessions.SelectedTab = existing;
            return;
        }

        ConsoleSession session = new(account, server, authentication, ProfilesChangedFromSession);
        SessionTab page = new(session);
        session.NotificationRequested += ShowSessionNotification;
        session.ConnectedChanged += _ => PostToUi(UpdateTrayMenu);
        page.CloseRequested += (_, _) =>
        {
            sessions.TabPages.Remove(page);
            page.Dispose();
            UpdateTrayMenu();
        };
        sessions.TabPages.Add(page);
        sessions.SelectedTab = page;
        session.Start();
        UpdateTrayMenu();
    }

    private void ProfilesChangedFromSession()
    {
        if (demoMode) return;
        lock (saveLock) repository.Save(profiles);
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshProfiles);
    }

    private AccountProfile? SelectedAccount()
    {
        Guid? id = SelectedId(accounts);
        return profiles.Accounts.FirstOrDefault(profile => profile.Id == id);
    }

    private ServerProfile? SelectedServer()
    {
        Guid? id = SelectedId(servers);
        return profiles.Servers.FirstOrDefault(profile => profile.Id == id);
    }

    private static Guid? SelectedId(ListView list) =>
        list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as Guid?;

    private static void RestoreSelection(ListView list, Guid? id)
    {
        if (list.Items.Count == 0) return;
        ListViewItem selected = list.Items.Cast<ListViewItem>()
            .FirstOrDefault(item => id.HasValue && item.Tag is Guid value && value == id.Value)
            ?? list.Items[0];
        selected.Selected = true;
        selected.Focused = true;
    }

    private void SelectHint(string kind) => MessageBox.Show(this, $"Select a {kind} profile first.", Text,
        MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "OeXYZ Console Client\n\n" +
            "Native .NET desktop application with an independent Minecraft protocol implementation.\n" +
            "No renderer, no Node.js, no Java and no game files are required by end users.\n\n" +
            "Microsoft passwords are never handled. Account sessions are encrypted by Windows DPAPI for the current user. " +
            "Server chat is stored only in the local logs folder.",
            "About & safety", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowSettings()
    {
        using SettingsDialog dialog = new(profiles.Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        profiles = profiles with { Settings = dialog.Result };
        SaveAndRefresh(SelectedId(accounts), SelectedId(servers));
    }

    private void BuildTray()
    {
        trayIcon.Icon = Icon ?? SystemIcons.Application;
        trayIcon.Text = "OeXYZ Minecraft Console Client";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayMenu.Opening += (_, _) => UpdateTrayMenu();
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (trayMenu.IsDisposed) return;
        trayMenu.Items.Clear();
        ToolStripMenuItem title = new("OeXYZ Minecraft Console Client") { Enabled = false };
        trayMenu.Items.Add(title);
        SessionTab[] pages = sessions.TabPages.OfType<SessionTab>().ToArray();
        if (pages.Length == 0)
        {
            trayMenu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
        }
        else
        {
            foreach (SessionTab page in pages.Take(8))
            {
                SessionSnapshot snapshot = page.Session.Snapshot;
                ToolStripMenuItem item = new(
                    $"{(snapshot.IsConnected ? "●" : "○")} {page.Session.Title}",
                    null,
                    (_, _) =>
                    {
                        RestoreFromTray();
                        sessions.SelectedTab = page;
                    });
                trayMenu.Items.Add(item);
            }
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Connect all profiles", null, (_, _) => ConnectAll());
        trayMenu.Items.Add("Disconnect all", null, (_, _) => DisconnectAll());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Open window", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => RequestExit());
    }

    private void ConnectAll()
    {
        AccountProfile? account = SelectedAccount() ?? profiles.Accounts.FirstOrDefault();
        if (account is null)
        {
            RestoreFromTray();
            MessageBox.Show(this, "Add an account profile first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (ServerProfile server in profiles.Servers) ConnectProfile(account, server);
    }

    private void DisconnectAll()
    {
        foreach (SessionTab page in sessions.TabPages.OfType<SessionTab>()) page.Session.Stop();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        if (trayHintShown) return;
        trayHintShown = true;
        trayIcon.BalloonTipTitle = "OeXYZ is still running";
        trayIcon.BalloonTipText = "Sessions continue in the background. Use the tray icon to reopen or exit.";
        trayIcon.ShowBalloonTip(5000);
    }

    private void RequestExit()
    {
        allowExit = true;
        Close();
    }

    private void ShowSessionNotification(SessionNotification notification)
    {
        if (closing || !profiles.Settings.NotificationsEnabled || !NotificationEnabled(notification.Kind)) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowSessionNotification(notification));
            return;
        }
        trayIcon.BalloonTipTitle = notification.Title;
        trayIcon.BalloonTipText = notification.Message.Length <= 240
            ? notification.Message
            : notification.Message[..240] + "…";
        trayIcon.ShowBalloonTip(5000);
    }

    private bool NotificationEnabled(SessionNotificationKind kind) => kind switch
    {
        SessionNotificationKind.Disconnect => profiles.Settings.NotifyDisconnect,
        SessionNotificationKind.Reconnect => profiles.Settings.NotifyReconnect,
        SessionNotificationKind.Death => profiles.Settings.NotifyDeath,
        SessionNotificationKind.Mention => profiles.Settings.NotifyMention,
        SessionNotificationKind.PrivateMessage => profiles.Settings.NotifyPrivateMessage,
        _ => true
    };

    private void QueueStatusRefresh()
    {
        if (!IsHandleCreated || IsDisposed || closing) return;
        Observe(RefreshServerStatusesAsync(formLifetime.Token));
    }

    private async Task RefreshServerStatusesAsync(CancellationToken cancellationToken)
    {
        ServerProfile[] snapshot = profiles.Servers.ToArray();
        using SemaphoreSlim concurrency = new(4);
        Task[] tasks = snapshot.Select(async server =>
        {
            CachedServerStatus? cached;
            lock (serverStatusCache) serverStatusCache.TryGetValue(server.Id, out cached);
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(45))
            {
                PostServerStatus(server.Id, cached);
                return;
            }
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CachedServerStatus result;
                try
                {
                    MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(
                        server.Address, server.CustomPort, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
                    result = new CachedServerStatus(DateTimeOffset.UtcNow, status, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    result = new CachedServerStatus(DateTimeOffset.UtcNow, null, exception.Message);
                }
                lock (serverStatusCache) serverStatusCache[server.Id] = result;
                PostServerStatus(server.Id, result);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void PostServerStatus(Guid serverId, CachedServerStatus result)
    {
        if (!IsHandleCreated || IsDisposed || closing) return;
        BeginInvoke(() => ApplyServerStatus(serverId, result));
    }

    private void PostToUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) when (closing || IsDisposed) { }
    }

    private void ApplyServerStatus(Guid serverId, CachedServerStatus cached)
    {
        ListViewItem? item = servers.Items.Cast<ListViewItem>().FirstOrDefault(candidate =>
            candidate.Tag is Guid id && id == serverId);
        ServerProfile? profile = profiles.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (item is null || profile is null || item.SubItems.Count < 2) return;
        string address = profile.Address + (profile.CustomPort > 0 ? $":{profile.CustomPort}" : string.Empty);
        if (cached.Status is MinecraftServerStatus statusResult)
        {
            item.SubItems[1].Text = $"{address} · ONLINE {statusResult.PingMilliseconds} ms · " +
                                    $"{statusResult.PlayersOnline}/{statusResult.PlayersMaximum} · {statusResult.VersionName}";
            item.ToolTipText = $"{statusResult.Description}\nProtocol {statusResult.ProtocolVersion}\n" +
                               $"Resolved endpoint {statusResult.Address.NetworkHost}:{statusResult.Address.Port}";
            if (statusResult.ServerIconPng is { Length: > 0 })
            {
                try
                {
                    using MemoryStream stream = new(statusResult.ServerIconPng, writable: false);
                    using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                    using Bitmap scaled = new(source, serverIcons.ImageSize);
                    string key = serverId.ToString("N");
                    if (serverIcons.Images.ContainsKey(key)) serverIcons.Images.RemoveByKey(key);
                    serverIcons.Images.Add(key, scaled);
                    item.ImageKey = key;
                }
                catch (ArgumentException)
                {
                    item.ImageIndex = -1;
                }
            }
        }
        else
        {
            item.SubItems[1].Text = $"{address} · OFFLINE";
            item.ToolTipText = cached.Error ?? "The server did not answer the Minecraft status ping.";
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "OeXYZ Console Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Icon? LoadIcon()
    {
        try
        {
            string asset = Path.Combine(AppContext.BaseDirectory, "assets", "oexyz.ico");
            return File.Exists(asset) ? new Icon(asset) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch { return null; }
    }

    private static ProfileDocument CreateDemoProfiles() => new()
    {
        Accounts =
        [
            new AccountProfile
            {
                DisplayName = "OeXYZ Test",
                Kind = AccountKind.Offline,
                LoginHint = "OeXYZTest"
            }
        ],
        Servers =
        [
            new ServerProfile
            {
                DisplayName = "Local 26.2 Test",
                Address = "127.0.0.1",
                CustomPort = 25566,
                Version = "auto",
                AntiAfk = true,
                AutoReconnect = true,
                AutoRespawn = true
            }
        ]
    };

    private static ProfileDocument CreatePublicDemoProfiles() => new()
    {
        Accounts =
        [
            new AccountProfile
            {
                DisplayName = "OeXYZ Public Test",
                Kind = AccountKind.Offline,
                LoginHint = "OeXYZDemo" + Random.Shared.Next(1000, 9999)
            }
        ],
        Servers =
        [
            new ServerProfile
            {
                DisplayName = "Minecraft Anarchy",
                Address = "play.minecraftanarchy.com",
                Version = "auto",
                AntiAfk = false,
                AutoReconnect = false,
                AutoRespawn = true
            }
        ]
    };

    private async void FormIsClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (closing) return;
        if (!allowExit && eventArgs.CloseReason == CloseReason.UserClosing && profiles.Settings.KeepRunningOnClose)
        {
            eventArgs.Cancel = true;
            HideToTray();
            return;
        }
        eventArgs.Cancel = true;
        closing = true;
        formLifetime.Cancel();
        SessionTab[] open = sessions.TabPages.OfType<SessionTab>().ToArray();
        foreach (SessionTab page in open) await page.CloseAsync();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        trayMenu.Dispose();
        serverIcons.Dispose();
        formLifetime.Dispose();
        Close();
    }

    private sealed record CachedServerStatus(
        DateTimeOffset CheckedAt,
        MinecraftServerStatus? Status,
        string? Error);
}
