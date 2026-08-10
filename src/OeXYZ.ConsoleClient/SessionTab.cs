using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OeXYZ.ConsoleClient;

internal sealed class SessionTab : TabPage
{
    private const int MaximumLines = 5000;
    private const int TrimLines = 1000;
    private readonly ConsoleSession session;
    private readonly ConcurrentQueue<SessionLine> pending = new();
    private readonly RichTextBox output = new();
    private readonly TextBox input = new();
    private readonly Button send = Theme.Button("Send", 82);
    private readonly Button respawn = Theme.Button("Respawn", 80);
    private readonly Button disconnect = Theme.Button("Disconnect", 90);
    private readonly Button openLog = Theme.Button("Log", 68);
    private readonly Button close = Theme.Button("Close", 70);
    private readonly Label status = new();
    private readonly System.Windows.Forms.Timer drainTimer = new() { Interval = 100 };
    private readonly SynchronizationContext uiContext = SynchronizationContext.Current
        ?? new WindowsFormsSynchronizationContext();
    private int lineCount;
    private bool closing;

    public SessionTab(ConsoleSession session)
    {
        this.session = session;
        session.CodeOfConductApproval = AskCodeOfConductAsync;
        Text = session.Title;
        BackColor = Theme.Background;
        Padding = Padding.Empty;

        Panel toolbar = new() { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Surface };
        status.Text = "STARTING";
        status.Font = new Font("Consolas", 9F, FontStyle.Bold);
        status.ForeColor = Theme.Blue;
        status.Dock = DockStyle.Fill;
        status.Padding = new Padding(12, 16, 0, 0);
        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Right,
            Width = 340,
            Padding = new Padding(0, 7, 6, 0),
            WrapContents = false,
            BackColor = Theme.Surface
        };
        actions.Controls.Add(respawn);
        actions.Controls.Add(disconnect);
        actions.Controls.Add(openLog);
        actions.Controls.Add(close);
        toolbar.Controls.Add(status);
        toolbar.Controls.Add(actions);

        Panel inputPanel = new()
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(10),
            BackColor = Theme.DarkSurface
        };
        send.Dock = DockStyle.Right;
        Theme.Primary(send);
        input.Dock = DockStyle.Fill;
        input.Font = new Font("Segoe UI", 11F);
        input.BackColor = Theme.Surface;
        input.ForeColor = Theme.Ink;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.PlaceholderText = "Chat, /command, or /respawn";
        inputPanel.Controls.Add(input);
        inputPanel.Controls.Add(send);

        output.Dock = DockStyle.Fill;
        output.ReadOnly = true;
        output.BackColor = Theme.Dark;
        output.ForeColor = Color.FromArgb(211, 222, 235);
        output.Font = new Font("Cascadia Mono", 10F);
        output.BorderStyle = BorderStyle.None;
        output.DetectUrls = true;
        output.WordWrap = true;
        output.HideSelection = false;
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Background
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(output, 0, 1);
        layout.Controls.Add(inputPanel, 0, 2);
        Controls.Add(layout);

        send.Click += async (_, _) => await SendInputAsync();
        input.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter) return;
            eventArgs.SuppressKeyPress = true;
            await SendInputAsync();
        };
        respawn.Click += async (_, _) => await RunActionAsync(() => session.RespawnAsync(), "Respawn request failed");
        disconnect.Click += (_, _) => session.Stop();
        openLog.Click += (_, _) => OpenPath(session.LogPath);
        close.Click += async (_, _) => await CloseAsync();
        output.LinkClicked += (_, eventArgs) => OpenPath(eventArgs.LinkText);
        drainTimer.Tick += (_, _) => DrainPendingLines();

        session.LineAdded += pending.Enqueue;
        session.StatusChanged += (text, kind) => Post(() => SetStatus(text, kind));
        session.ConnectedChanged += connected => Post(() =>
        {
            input.Enabled = connected;
            send.Enabled = connected;
            respawn.Enabled = connected;
        });
        input.Enabled = false;
        send.Enabled = false;
        respawn.Enabled = false;
        drainTimer.Start();
    }

    public event EventHandler? CloseRequested;

    private async Task SendInputAsync()
    {
        string message = input.Text.Trim();
        if (message.Length == 0) return;
        input.Clear();
        if (string.Equals(message, "/respawn", StringComparison.OrdinalIgnoreCase))
        {
            await RunActionAsync(() => session.RespawnAsync(), "Respawn request failed");
            return;
        }
        if (string.Equals(message, "/disconnect", StringComparison.OrdinalIgnoreCase))
        {
            session.Stop();
            return;
        }
        await RunActionAsync(() => session.SendAsync(message), "Message could not be sent");
    }

    private static async Task RunActionAsync(Func<Task> action, string caption)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"{caption}:\n{exception.Message}", "OeXYZ Console Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            DialogResult result = MessageBox.Show(this,
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
        int firstVisibleLine = follow ? 0 : (int)SendMessage(output.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
        int selectionStart = output.SelectionStart;
        int selectionLength = output.SelectionLength;
        int drained = 0;
        SendMessage(output.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            while (drained < 300 && pending.TryDequeue(out SessionLine? line))
            {
                AppendLine(line);
                lineCount++;
                drained++;
            }
            int removedCharacters = 0;
            int removedLines = 0;
            if (lineCount > MaximumLines) TrimOldLines(out removedCharacters, out removedLines);
            if (follow)
            {
                output.SelectionStart = output.TextLength;
                output.SelectionLength = 0;
                output.ScrollToCaret();
            }
            else
            {
                int safeStart = Math.Max(0, Math.Min(output.TextLength, selectionStart - removedCharacters));
                int safeLength = Math.Max(0, Math.Min(output.TextLength - safeStart, selectionLength));
                output.Select(safeStart, safeLength);
                int targetLine = Math.Max(0, firstVisibleLine - removedLines);
                int currentLine = (int)SendMessage(output.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
                SendMessage(output.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(targetLine - currentLine));
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
        output.SelectionStart = output.TextLength;
        output.SelectionLength = 0;
        output.SelectionColor = line.Kind switch
        {
            SessionLineKind.Chat => Theme.Ink,
            SessionLineKind.Success => Theme.Green,
            SessionLineKind.Warning => Theme.Amber,
            SessionLineKind.Error => Theme.Danger,
            _ => Color.FromArgb(166, 183, 205)
        };
        output.AppendText($"{line.Timestamp:HH:mm:ss}  {line.Text}{Environment.NewLine}");
    }

    private void SetStatus(string text, SessionLineKind kind)
    {
        status.Text = text.ToUpperInvariant();
        status.ForeColor = kind switch
        {
            SessionLineKind.Success => Theme.Green,
            SessionLineKind.Warning => Theme.Amber,
            SessionLineKind.Error => Theme.Danger,
            _ => Theme.BlueBright
        };
    }

    private void TrimOldLines(out int removedCharacters, out int removedLines)
    {
        string text = output.Text;
        int index = 0;
        int found = 0;
        while (index < text.Length && found < TrimLines)
        {
            if (text[index++] == '\n') found++;
        }
        output.Select(0, index);
        output.SelectedText = string.Empty;
        lineCount -= found;
        removedCharacters = index;
        removedLines = found;
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
        drainTimer.Stop();
        await session.DisposeAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "OeXYZ Console Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsAtBottom(RichTextBox box)
    {
        if (!box.IsHandleCreated || box.TextLength == 0) return true;
        ScrollInfo info = new() { Size = Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 };
        if (!GetScrollInfo(box.Handle, 1, ref info)) return true;
        return info.Position >= info.Maximum - Math.Max((int)info.Page, 1) - 3;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            drainTimer.Dispose();
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
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr handle, int bar, ref ScrollInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
