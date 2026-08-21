using System.Collections.Concurrent;
using System.Diagnostics;
using OeXYZ.Protocol;
using OeXYZ.Session;

namespace OeXYZ.ConsoleClient;

internal sealed class LogViewerForm : Form
{
    private readonly string logDirectory;
    private readonly ListBox files = new();
    private readonly TextBox search = new();
    private readonly RichTextBox contents = new();
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 250 };

    public LogViewerForm(string logDirectory)
    {
        this.logDirectory = logDirectory;
        Text = "OeXYZ Log Viewer";
        ClientSize = new Size(1000, 650);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;

        ToolStrip tools = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = Theme.Surface };
        Theme.ToolStrip(tools);
        tools.Items.Add("Refresh", null, (_, _) => RefreshFiles());
        tools.Items.Add("Export", null, (_, _) => ExportSelected());
        tools.Items.Add("Delete", null, (_, _) => DeleteSelected());
        tools.Items.Add("Open folder", null, (_, _) => OpenFolder());
        ToolStripControlHost searchHost = new(search) { AutoSize = false, Width = 280 };
        search.PlaceholderText = "Search the selected log";
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(searchHost);

        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            BackColor = Theme.Border
        };
        files.Dock = DockStyle.Fill;
        Theme.Input(files);
        contents.Dock = DockStyle.Fill;
        contents.ReadOnly = true;
        contents.BackColor = Theme.Dark;
        contents.ForeColor = Theme.Ink;
        contents.Font = AppFonts.Create(9.5F);
        contents.BorderStyle = BorderStyle.None;
        split.Panel1.Controls.Add(files);
        split.Panel2.Controls.Add(contents);
        Controls.Add(split);
        Controls.Add(tools);

        files.SelectedIndexChanged += (_, _) => LoadSelected();
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); LoadSelected(); };
        Shown += (_, _) =>
        {
            Theme.ApplyDarkTitleBar(this);
            int minimum = (int)Math.Round(220D * DeviceDpi / 96D);
            int panel2Minimum = (int)Math.Round(360D * DeviceDpi / 96D);
            int maximum = split.ClientSize.Width - panel2Minimum - split.SplitterWidth;
            if (maximum >= minimum)
            {
                split.SplitterDistance = Math.Clamp((int)(split.ClientSize.Width * 0.30), minimum, maximum);
                split.Panel1MinSize = minimum;
                split.Panel2MinSize = panel2Minimum;
            }
            RefreshFiles();
        };
    }

    private void RefreshFiles()
    {
        Directory.CreateDirectory(logDirectory);
        string? selected = (files.SelectedItem as LogItem)?.Path;
        files.BeginUpdate();
        try
        {
            files.Items.Clear();
            foreach (FileInfo file in new DirectoryInfo(logDirectory).EnumerateFiles("*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc))
                files.Items.Add(new LogItem(file.FullName, $"{file.LastWriteTime:yyyy-MM-dd HH:mm}  {file.Name}"));
            if (files.Items.Count > 0)
                files.SelectedIndex = Math.Max(0, files.Items.Cast<LogItem>().ToList().FindIndex(item => item.Path == selected));
        }
        finally { files.EndUpdate(); }
    }

    private void LoadSelected()
    {
        if (files.SelectedItem is not LogItem selected || !File.Exists(selected.Path)) { contents.Clear(); return; }
        try
        {
            string needle = search.Text.Trim();
            using FileStream stream = new(selected.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            using StreamReader reader = new(stream);
            Queue<string> tail = new(5000);
            while (reader.ReadLine() is { } line)
            {
                if (needle.Length > 0 && !line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                if (tail.Count == 5000) tail.Dequeue();
                tail.Enqueue(line);
            }
            contents.Text = string.Join(Environment.NewLine, tail);
        }
        catch (IOException exception) { contents.Text = exception.Message; }
    }

    private void ExportSelected()
    {
        if (files.SelectedItem is not LogItem selected || !File.Exists(selected.Path)) return;
        using SaveFileDialog save = new() { FileName = Path.GetFileName(selected.Path), Filter = "Log files (*.log)|*.log", OverwritePrompt = true };
        if (save.ShowDialog(this) == DialogResult.OK) File.Copy(selected.Path, save.FileName, overwrite: true);
    }

    private void DeleteSelected()
    {
        if (files.SelectedItem is not LogItem selected || !File.Exists(selected.Path)) return;
        if (BrandMessageBox.Show(this, $"Delete {Path.GetFileName(selected.Path)}?", Text, MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { File.Delete(selected.Path); RefreshFiles(); }
        catch (Exception exception) { BrandMessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(logDirectory);
        Process.Start(new ProcessStartInfo(logDirectory) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) searchTimer.Dispose();
        base.Dispose(disposing);
    }

    private sealed record LogItem(string Path, string Label)
    {
        public override string ToString() => Label;
    }
}

internal sealed class ProtocolInspectorForm : Form
{
    private readonly ConsoleSession session;
    private readonly ConcurrentQueue<PacketTrace> pending = new();
    private readonly BrandListView packets = new();
    private readonly TextBox statistics = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };

    public ProtocolInspectorForm(ConsoleSession session)
    {
        this.session = session;
        Text = $"Protocol Inspector · {session.Title}";
        ClientSize = new Size(940, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;

        BrandTabControl tabs = new() { Dock = DockStyle.Fill, Font = AppFonts.Create(9F, FontStyle.Bold) };
        TabPage packetPage = new("Packet metadata") { BackColor = Theme.Background };
        packets.Dock = DockStyle.Fill;
        packets.View = View.Details;
        packets.FullRowSelect = true;
        packets.Columns.Add("Timestamp", 125);
        packets.Columns.Add("Dir", 50);
        packets.Columns.Add("State", 105);
        packets.Columns.Add("ID", 70);
        packets.Columns.Add("Name", 300);
        packets.Columns.Add("Payload", 90);
        packets.Columns.Add("Wire", 90);
        packets.FitLastColumn = true;
        Theme.Input(packets);
        packetPage.Controls.Add(packets);
        TabPage statsPage = new("Unknown statistics") { BackColor = Theme.Background };
        statistics.Dock = DockStyle.Fill;
        statistics.Multiline = true;
        statistics.ReadOnly = true;
        statistics.ScrollBars = ScrollBars.Both;
        statistics.Font = AppFonts.Create(10F);
        Theme.Input(statistics);
        statsPage.Controls.Add(statistics);
        tabs.TabPages.Add(packetPage);
        tabs.TabPages.Add(statsPage);
        Controls.Add(tabs);

        session.PacketTraced += PacketTraced;
        timer.Tick += (_, _) => Drain();
        timer.Start();
        Shown += (_, _) => Theme.ApplyDarkTitleBar(this);
    }

    private void PacketTraced(PacketTrace trace) => pending.Enqueue(trace);

    private void Drain()
    {
        packets.BeginUpdate();
        try
        {
            int count = 0;
            while (count++ < 500 && pending.TryDequeue(out PacketTrace? trace))
            {
                ListViewItem item = new(trace.Timestamp.ToString("HH:mm:ss.fff"));
                item.SubItems.Add(trace.Direction == PacketDirection.Clientbound ? "←" : "→");
                item.SubItems.Add(trace.State.ToString());
                item.SubItems.Add($"0x{trace.PacketId:X2}");
                item.SubItems.Add(trace.Name);
                item.SubItems.Add(trace.PayloadBytes.ToString());
                item.SubItems.Add(trace.WireBytes.ToString());
                if (!trace.Known) item.ForeColor = Theme.Amber;
                packets.Items.Add(item);
            }
            while (packets.Items.Count > 5000) packets.Items.RemoveAt(0);
            if (packets.Items.Count > 0) packets.EnsureVisible(packets.Items.Count - 1);
        }
        finally { packets.EndUpdate(); }
        SessionSnapshot snapshot = session.Snapshot;
        string counters = $"Dropped events: {snapshot.DroppedEvents} | Dropped logs: {snapshot.DroppedLogLines} | " +
                          $"Subscriber failures: {snapshot.SubscriberFailures} | Outbound rejections: {snapshot.OutboundRejections} | " +
                          $"Unknown overflow: {snapshot.UnknownPacketOverflow}";
        statistics.Text = counters + Environment.NewLine + string.Join(Environment.NewLine, session.UnknownPacketStatistics
            .OrderByDescending(item => item.Value)
            .Select(item => $"{item.Key,-40} : {item.Value}"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            session.PacketTraced -= PacketTraced;
            timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
