using OeXYZ.Core;
using OeXYZ.Protocol;
using OeXYZ.Session;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OeXYZ.ConsoleClient;

internal sealed class SessionTab : TabPage
{
    private const int MaximumLines = 5000;
    private const int TrimLines = 1000;
    internal const int MaximumPendingLines = 1024;
    private readonly ConsoleSession session;
    private readonly BoundedDropOldestQueue<SessionLine> pending = new(MaximumPendingLines);
    private readonly List<SessionLine> lines = [];
    private readonly CommandHistory commandHistory = new(200);
    private readonly Dictionary<FontStyle, Font> chatFonts = [];
    private readonly RichTextBox output = new();
    private readonly TextBox input = new();
    private readonly TextBox search = new();
    private readonly ComboBox filter = new();
    private readonly Button send = Theme.Button("Send", 82);
    private readonly Button respawn = Theme.Button("Respawn", 80);
    private readonly Button disconnect = Theme.Button("Disconnect", 100);
    private readonly Button openLog = Theme.Button("Log", 62);
    private readonly Button close = Theme.Button("Close", 68);
    private readonly Button more = Theme.Button("More", 70);
    private readonly Button playerToggle = Theme.Button("Players", 78);
    private readonly Label status = new();
    private readonly Label vitals = new();
    private readonly Label traffic = new();
    private readonly BrandListView playerList = new();
    private readonly SplitContainer content = new();
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer filterTimer = new() { Interval = 250 };
    private readonly SynchronizationContext uiContext = SynchronizationContext.Current
        ?? new WindowsFormsSynchronizationContext();
    private SessionSnapshot latestSnapshot;
    private IReadOnlyList<PlayerListEntry>? displayedPlayers;
    private int lineCount;
    private bool closing;

    public SessionTab(ConsoleSession session)
    {
        this.session = session;
        latestSnapshot = session.Snapshot;
        session.CodeOfConductApproval = AskCodeOfConductAsync;
        Text = session.Title;
        BackColor = Theme.Background;
        Padding = Padding.Empty;

        Control header = BuildHeader();
        Control filterBar = BuildFilterBar();
        ConfigureOutput();
        ConfigurePlayerList();

        content.Dock = DockStyle.Fill;
        content.Orientation = Orientation.Vertical;
        content.BackColor = Theme.Border;
        content.FixedPanel = FixedPanel.Panel2;
        content.SplitterWidth = 1;
        content.Panel1.Controls.Add(output);
        content.Panel2.Controls.Add(playerList);
        content.Panel2Collapsed = true;

        Panel inputPanel = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Theme.DarkSurface
        };
        send.Dock = DockStyle.Right;
        send.Margin = new Padding(8, 0, 0, 0);
        Theme.Primary(send);
        input.Dock = DockStyle.Fill;
        input.Font = AppFonts.Create(11F);
        input.BackColor = Theme.Surface;
        input.ForeColor = Theme.Ink;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.PlaceholderText = "Chat, /command, or /respawn";
        inputPanel.Controls.Add(input);
        inputPanel.Controls.Add(send);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Background
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(filterBar, 0, 1);
        layout.Controls.Add(content, 0, 2);
        layout.Controls.Add(inputPanel, 0, 3);
        Controls.Add(layout);

        WireEvents();
        input.Enabled = false;
        send.Enabled = false;
        respawn.Enabled = false;
        uiTimer.Start();
    }

    public event EventHandler? CloseRequested;
    public ConsoleSession Session => session;

    private Control BuildHeader()
    {
        Panel toolbar = new()
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Surface
        };

        TableLayoutPanel dashboard = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12, 7, 6, 5),
            Margin = Padding.Empty,
            BackColor = Theme.Surface
        };
        foreach (Label label in new[] { status, vitals, traffic })
        {
            label.Dock = DockStyle.Fill;
            label.AutoEllipsis = true;
            label.Font = AppFonts.Create(8.5F);
        }
        status.Font = AppFonts.Create(9F, FontStyle.Bold);
        status.ForeColor = Theme.BlueBright;
        vitals.ForeColor = Theme.Ink;
        traffic.ForeColor = Theme.Muted;
        dashboard.Controls.Add(status, 0, 0);
        dashboard.Controls.Add(vitals, 0, 1);
        dashboard.Controls.Add(traffic, 0, 2);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Right,
            Width = 450,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 18, 6, 0),
            Margin = Padding.Empty,
            BackColor = Theme.Surface
        };
        foreach (Button action in new[] { respawn, disconnect, openLog, more, close })
        {
            action.AutoSize = true;
            action.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            action.MinimumSize = new Size(action.Width, 34);
            action.Margin = new Padding(3, 0, 3, 0);
            actions.Controls.Add(action);
        }
        toolbar.Controls.Add(dashboard);
        toolbar.Controls.Add(actions);
        return toolbar;
    }

    private Control BuildFilterBar()
    {
        FlowLayoutPanel bar = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(9, 4, 8, 4),
            Margin = Padding.Empty,
            BackColor = Theme.DarkSurface
        };
        search.Width = 220;
        search.Height = 30;
        search.PlaceholderText = "Search chat and events (Ctrl+F)";
        Theme.Input(search);
        filter.Width = 118;
        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(["All", "Chat", "System", "Connection", "Error"]);
        filter.SelectedIndex = 0;
        Theme.Input(filter);
        Button copy = Theme.Button("Copy", 64);
        Button clear = Theme.Button("Clear", 64);
        foreach (Control control in new Control[] { search, filter, copy, clear, playerToggle })
            control.Margin = new Padding(3, 0, 3, 0);
        copy.Click += (_, _) => CopyOutput();
        clear.Click += (_, _) => ClearOutput();
        playerToggle.Click += (_, _) => TogglePlayerList();
        bar.Controls.Add(search);
        bar.Controls.Add(filter);
        bar.Controls.Add(copy);
        bar.Controls.Add(clear);
        bar.Controls.Add(playerToggle);
        return bar;
    }

    private void ConfigureOutput()
    {
        output.Dock = DockStyle.Fill;
        output.ReadOnly = true;
        output.BackColor = Theme.Dark;
        output.ForeColor = Color.FromArgb(211, 222, 235);
        output.Font = AppFonts.Create(10F);
        output.BorderStyle = BorderStyle.None;
        output.DetectUrls = true;
        output.WordWrap = true;
        output.HideSelection = false;
        ContextMenuStrip menu = new();
        Theme.Menu(menu);
        menu.Items.Add("Copy", null, (_, _) => CopyOutput());
        menu.Items.Add("Clear view", null, (_, _) => ClearOutput());
        output.ContextMenuStrip = menu;
    }

    private void ConfigurePlayerList()
    {
        playerList.Dock = DockStyle.Fill;
        playerList.View = View.Details;
        playerList.FullRowSelect = true;
        playerList.MultiSelect = false;
        playerList.HideSelection = false;
        playerList.Columns.Add("Player", 145);
        playerList.Columns.Add("Ping", 65);
        playerList.FitLastColumn = true;
        ContextMenuStrip menu = new();
        Theme.Menu(menu);
        menu.Items.Add("Copy name", null, (_, _) => CopyPlayerName());
        menu.Items.Add("Prepare /msg", null, (_, _) => PrepareMessage());
        playerList.ContextMenuStrip = menu;
        playerList.DoubleClick += (_, _) => PrepareMessage();
    }

    private void WireEvents()
    {
        send.Click += async (_, _) => await SendInputAsync();
        input.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Up)
            {
                eventArgs.SuppressKeyPress = true;
                input.Text = commandHistory.Previous(input.Text);
                input.SelectionStart = input.TextLength;
                return;
            }
            if (eventArgs.KeyCode == Keys.Down)
            {
                eventArgs.SuppressKeyPress = true;
                input.Text = commandHistory.Next();
                input.SelectionStart = input.TextLength;
                return;
            }
            if (eventArgs.KeyCode != Keys.Enter) return;
            eventArgs.SuppressKeyPress = true;
            await SendInputAsync();
        };
        respawn.Click += async (_, _) => await RunActionAsync(() => session.RespawnAsync(), "Respawn request failed");
        disconnect.Click += (_, _) => session.Stop();
        openLog.Click += (_, _) => OpenPath(session.LogPath);
        close.Click += async (_, _) => await CloseAsync();
        more.Click += (_, _) => ShowMoreMenu();
        output.LinkClicked += (_, eventArgs) => ConfirmAndOpenUrl(eventArgs.LinkText);
        uiTimer.Tick += (_, _) =>
        {
            DrainPendingLines();
            UpdateDashboard();
        };
        filterTimer.Tick += (_, _) =>
        {
            filterTimer.Stop();
            RebuildOutput();
        };
        search.TextChanged += (_, _) => QueueRebuild();
        filter.SelectedIndexChanged += (_, _) => QueueRebuild();

        session.LineAdded += pending.Enqueue;
        session.SnapshotChanged += snapshot => Volatile.Write(ref latestSnapshot, snapshot);
        session.StatusChanged += (_, _) => { };
        session.ConnectedChanged += connected => Post(() =>
        {
            input.Enabled = connected;
            send.Enabled = connected;
            respawn.Enabled = connected;
            disconnect.Enabled = connected || !closing;
        });
    }

    private async Task SendInputAsync()
    {
        string message = input.Text.Trim();
        if (message.Length == 0) return;
        commandHistory.Add(message);
        input.Clear();
        LocalSessionCommand localCommand = SessionInput.Classify(message);
        if (localCommand == LocalSessionCommand.Respawn)
        {
            await RunActionAsync(() => session.RespawnAsync(), "Respawn request failed");
            return;
        }
        if (localCommand == LocalSessionCommand.Disconnect)
        {
            session.Stop();
            return;
        }
        await RunActionAsync(() => session.SendAsync(message), "Message could not be sent");
    }

    private void ShowMoreMenu()
    {
        ContextMenuStrip menu = new();
        Theme.Menu(menu);
        ToolStripMenuItem quick = new("Quick commands");
        if (session.Server.QuickCommands.Count == 0)
            quick.DropDownItems.Add(new ToolStripMenuItem("No commands configured") { Enabled = false });
        else
            foreach (string command in session.Server.QuickCommands)
                quick.DropDownItems.Add(command, null, async (_, _) => await SendQuickCommandAsync(command));
        menu.Items.Add(quick);
        menu.Items.Add("Protocol inspector", null, (_, _) => OpenInspector());
        menu.Items.Add("Create support package", null, async (_, _) => await CreateSupportPackageAsync());
        menu.Items.Add("Open session log", null, (_, _) => OpenPath(session.LogPath));
        menu.Closed += (_, _) => BeginInvoke(() =>
        {
            if (!menu.IsDisposed) menu.Dispose();
        });
        menu.Show(more, new Point(0, more.Height));
    }

    private async Task SendQuickCommandAsync(string command)
    {
        if (SensitiveDataRedactor.IsSensitiveCommand(command) &&
            BrandMessageBox.Show(this,
                "This quick command appears to contain a login or registration secret. Send it once? It will be redacted from logs.",
                "Sensitive quick command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunActionAsync(() => session.SendAsync(command), "Quick command could not be sent");
    }

    private void OpenInspector()
    {
        if (!session.PacketInspectionEnabled)
        {
            BrandMessageBox.Show(this,
                "Packet inspection is disabled. Enable Developer protocol inspector in Settings, then start a new session.",
                "Protocol inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ProtocolInspectorForm inspector = new(session);
        inspector.Show(this);
    }

    private async Task CreateSupportPackageAsync()
    {
        using SaveFileDialog save = new()
        {
            Title = "Create sanitized OeXYZ support package",
            FileName = $"OeXYZ-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = "zip",
            OverwritePrompt = true
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            more.Enabled = false;
            await SupportPackageService.CreateAsync(new SupportPackageRequest(
                save.FileName,
                Application.ProductVersion,
                session.Server,
                session.TerminalException?.Message,
                session.RecentDiagnostics,
                session.UnknownPacketStatistics));
            BrandMessageBox.Show(this,
                "The sanitized package was created. It excludes account tokens, accounts.bin, passwords and full private chat history.",
                "Support package ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            BrandMessageBox.Show(this, exception.Message, "Support package failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (!IsDisposed) more.Enabled = true; }
    }

    private static async Task RunActionAsync(Func<Task> action, string caption)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            BrandMessageBox.Show($"{caption}:\n{exception.Message}", "OeXYZ Console Client",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private Task<bool> AskCodeOfConductAsync(string contents, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
            answer.TrySetCanceled(cancellationToken));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        uiContext.Post(_ =>
        {
            if (closing || IsDisposed)
            {
                answer.TrySetResult(false);
                return;
            }
            string shown = contents.Length <= 3500 ? contents : contents[..3500] + "\n\n[Text shortened]";
            DialogResult result = BrandMessageBox.Show(this,
                "This server requires you to accept its code of conduct before joining.\n\n" +
                shown + "\n\nAccept these server rules?",
                "Server code of conduct",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            answer.TrySetResult(result == DialogResult.Yes);
        }, null);
        return answer.Task;
    }

    private void DrainPendingLines()
    {
        if (pending.IsEmpty || output.IsDisposed) return;
        bool follow = IsAtBottom(output);
        int drained = 0;
        SendMessage(output.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            while (drained < 300 && pending.TryDequeue(out SessionLine line))
            {
                lines.Add(line);
                if (MatchesFilter(line)) AppendLine(line);
                drained++;
            }
            if (lines.Count > MaximumLines)
            {
                lines.RemoveRange(0, Math.Min(TrimLines, lines.Count));
                RebuildOutputCore();
            }
            if (follow)
            {
                output.SelectionStart = output.TextLength;
                output.SelectionLength = 0;
                output.ScrollToCaret();
            }
        }
        finally
        {
            SendMessage(output.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            output.Invalidate();
        }
    }

    private void AppendLine(SessionLine line)
    {
        int start = output.TextLength;
        output.SelectionStart = start;
        output.SelectionLength = 0;
        output.SelectionFont = GetChatFont(FontStyle.Regular);
        output.SelectionColor = line.Kind switch
        {
            SessionLineKind.Chat => Theme.Ink,
            SessionLineKind.Success => Theme.Green,
            SessionLineKind.Warning => Theme.Amber,
            SessionLineKind.Error => Theme.Danger,
            _ => Color.FromArgb(166, 183, 205)
        };
        output.AppendText($"{line.Timestamp:HH:mm:ss}  ");
        if (line.Formatting is { Runs.Count: > 0 })
        {
            foreach (ChatRun run in line.Formatting.Runs)
            {
                output.SelectionColor = MinecraftColor(run.Style.Color);
                output.SelectionFont = GetChatFont(ToFontStyle(run.Style));
                output.AppendText(run.Text);
            }
        }
        else
        {
            output.AppendText(line.Text);
        }
        output.SelectionFont = GetChatFont(FontStyle.Regular);
        output.SelectionColor = Theme.Ink;
        output.AppendText(Environment.NewLine);
        lineCount++;
        HighlightMatches(start, output.TextLength - start);
    }

    private void HighlightMatches(int start, int length)
    {
        string needle = search.Text.Trim();
        if (needle.Length == 0 || length <= 0) return;
        int end = start + length;
        int index = start;
        while (index < end)
        {
            int found = output.Find(needle, index, end, RichTextBoxFinds.None);
            if (found < 0) break;
            output.Select(found, needle.Length);
            output.SelectionBackColor = Color.FromArgb(126, 91, 23);
            index = found + Math.Max(needle.Length, 1);
        }
    }

    private void QueueRebuild()
    {
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void RebuildOutput()
    {
        if (output.IsDisposed) return;
        SendMessage(output.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            RebuildOutputCore();
            output.SelectionStart = output.TextLength;
            output.SelectionLength = 0;
            output.ScrollToCaret();
        }
        finally
        {
            SendMessage(output.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            output.Invalidate();
        }
    }

    private void RebuildOutputCore()
    {
        output.Clear();
        lineCount = 0;
        foreach (SessionLine line in lines.Where(MatchesFilter)) AppendLine(line);
    }

    private bool MatchesFilter(SessionLine line)
    {
        SessionLineCategory? required = filter.SelectedIndex switch
        {
            1 => SessionLineCategory.Chat,
            2 => SessionLineCategory.System,
            3 => SessionLineCategory.Connection,
            4 => SessionLineCategory.Error,
            _ => null
        };
        if (required.HasValue && line.Category != required.Value) return false;
        string needle = search.Text.Trim();
        return needle.Length == 0 || line.Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDashboard()
    {
        SessionSnapshot snapshot = Volatile.Read(ref latestSnapshot);
        TimeSpan uptime = snapshot.Metrics.Uptime(DateTimeOffset.UtcNow);
        string ping = snapshot.Metrics.PingMilliseconds is int pingMs ? $"{pingMs} ms" : "—";
        string version = snapshot.MinecraftVersion ?? "detecting";
        string protocol = snapshot.ProtocolVersion?.ToString() ?? "—";
        string reconnect = snapshot.NextReconnectAt is DateTimeOffset retry
            ? $" · Next {Math.Max(0, Math.Ceiling((retry - DateTimeOffset.UtcNow).TotalSeconds)):0}s"
            : string.Empty;
        status.Text = $"{snapshot.Status}  |  {version}  |  Protocol {protocol}  |  Ping {ping}  |  " +
                      $"Uptime {uptime:hh\\:mm\\:ss}  |  Reconnects {snapshot.ReconnectCount}{reconnect}";
        status.ForeColor = snapshot.StatusKind switch
        {
            SessionLineKind.Success => Theme.Green,
            SessionLineKind.Warning => Theme.Amber,
            SessionLineKind.Error => Theme.Danger,
            _ => Theme.BlueBright
        };
        string hp = snapshot.Health?.ToString("0.0") ?? "—";
        string foodText = snapshot.Food?.ToString() ?? "—";
        string xyz = snapshot.Position is PlayerPosition p
            ? $"{p.X:0.0} / {p.Y:0.0} / {p.Z:0.0}  |  Look {p.Yaw:0.0}° / {p.Pitch:0.0}°"
            : "— / — / —";
        vitals.Text = $"HP {hp}  |  Food {foodText}  |  XYZ {xyz}  |  Players {snapshot.Players.Count}";
        string last = snapshot.Metrics.LastReceivedAt is DateTimeOffset received
            ? received.ToLocalTime().ToString("HH:mm:ss")
            : "—";
        traffic.Text = $"Last packet {last}  |  RX {FormatBytes(snapshot.Metrics.BytesReceived)} / " +
                       $"{snapshot.Metrics.PacketsReceived:N0} packets  |  TX {FormatBytes(snapshot.Metrics.BytesSent)} / " +
                       $"{snapshot.Metrics.PacketsSent:N0} packets  |  {snapshot.ServerAddress}";
        if (!ReferenceEquals(displayedPlayers, snapshot.Players)) RefreshPlayers(snapshot.Players);
    }

    private void RefreshPlayers(IReadOnlyList<PlayerListEntry> players)
    {
        displayedPlayers = players;
        playerList.BeginUpdate();
        try
        {
            playerList.Items.Clear();
            foreach (PlayerListEntry player in players.Where(item => item.Listed))
            {
                ListViewItem item = new(player.Name) { Tag = player };
                item.SubItems.Add(player.PingMilliseconds >= 0 ? $"{player.PingMilliseconds} ms" : "—");
                playerList.Items.Add(item);
            }
        }
        finally
        {
            playerList.EndUpdate();
        }
        playerToggle.Text = $"Players ({playerList.Items.Count})";
    }

    private void TogglePlayerList()
    {
        if (content.Panel2Collapsed)
        {
            content.Panel2Collapsed = false;
            int maximum = Math.Max(0, content.Width - content.SplitterWidth);
            content.SplitterDistance = Math.Clamp(content.Width - 235, 0, maximum);
        }
        else
        {
            content.Panel2Collapsed = true;
        }
    }

    private void CopyPlayerName()
    {
        if (playerList.SelectedItems.Count == 0) return;
        Clipboard.SetText(playerList.SelectedItems[0].Text);
    }

    private void PrepareMessage()
    {
        if (playerList.SelectedItems.Count == 0) return;
        input.Text = $"/msg {playerList.SelectedItems[0].Text} ";
        input.Focus();
        input.SelectionStart = input.TextLength;
    }

    private void CopyOutput()
    {
        string text = output.SelectionLength > 0 ? output.SelectedText : output.Text;
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void ClearOutput()
    {
        lines.Clear();
        pending.Clear();
        output.Clear();
        lineCount = 0;
    }

    private Font GetChatFont(FontStyle style)
    {
        if (chatFonts.TryGetValue(style, out Font? cached)) return cached;
        Font created = new(output.Font.FontFamily, output.Font.Size, style, GraphicsUnit.Point);
        chatFonts.Add(style, created);
        return created;
    }

    private static FontStyle ToFontStyle(ChatStyle style)
    {
        FontStyle result = FontStyle.Regular;
        if (style.Bold) result |= FontStyle.Bold;
        if (style.Italic) result |= FontStyle.Italic;
        if (style.Underlined) result |= FontStyle.Underline;
        if (style.Strikethrough) result |= FontStyle.Strikeout;
        return result;
    }

    private static Color MinecraftColor(string? name) => name?.ToLowerInvariant() switch
    {
        "black" => Color.FromArgb(0, 0, 0),
        "dark_blue" => Color.FromArgb(0, 0, 170),
        "dark_green" => Color.FromArgb(0, 170, 0),
        "dark_aqua" => Color.FromArgb(0, 170, 170),
        "dark_red" => Color.FromArgb(170, 0, 0),
        "dark_purple" => Color.FromArgb(170, 0, 170),
        "gold" => Color.FromArgb(255, 170, 0),
        "gray" => Color.FromArgb(170, 170, 170),
        "dark_gray" => Color.FromArgb(85, 85, 85),
        "blue" => Color.FromArgb(85, 85, 255),
        "green" => Color.FromArgb(85, 255, 85),
        "aqua" => Color.FromArgb(85, 255, 255),
        "red" => Color.FromArgb(255, 85, 85),
        "light_purple" => Color.FromArgb(255, 85, 255),
        "yellow" => Color.FromArgb(255, 255, 85),
        "white" => Color.White,
        string hex when hex.Length == 7 && hex[0] == '#' && int.TryParse(hex[1..],
            System.Globalization.NumberStyles.HexNumber, null, out int rgb) => Color.FromArgb(
                (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF),
        _ => Theme.Ink
    };

    private void ConfirmAndOpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        if (BrandMessageBox.Show(this, $"Open this link in your browser?\n\n{uri}", "Open external link",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            OpenPath(uri.AbsoluteUri);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            search.Focus();
            search.SelectAll();
            return true;
        }
        if (keyData == (Keys.Control | Keys.L))
        {
            ClearOutput();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void Post(Action action)
    {
        if (IsDisposed || closing) return;
        if (IsHandleCreated) BeginInvoke(action);
    }

    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true;
        uiTimer.Stop();
        filterTimer.Stop();
        await session.DisposeAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            BrandMessageBox.Show(exception.Message, "OeXYZ Console Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsAtBottom(RichTextBox box)
    {
        if (!box.IsHandleCreated || box.TextLength == 0) return true;
        ScrollInfo info = new() { Size = Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 };
        if (!GetScrollInfo(box.Handle, 1, ref info)) return true;
        return info.Position >= info.Maximum - Math.Max((int)info.Page, 1) - 3;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            uiTimer.Dispose();
            filterTimer.Dispose();
            foreach (Font font in chatFonts.Values) font.Dispose();
            if (!closing) session.Stop();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public int Size;
        public int Mask;
        public int Minimum;
        public int Maximum;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }

    private const int WmSetRedraw = 0x000B;

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr handle, int bar, ref ScrollInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
