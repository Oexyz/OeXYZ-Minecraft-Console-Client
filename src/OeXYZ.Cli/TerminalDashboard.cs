using System.Collections.Concurrent;
using System.Text;
using OeXYZ.Authentication;
using OeXYZ.Core;
using OeXYZ.Protocol;
using OeXYZ.Session;

namespace OeXYZ.Cli;

internal sealed class TerminalDashboard : IAsyncDisposable
{
    private readonly SessionRuntimeRegistry registry;
    private readonly bool acceptsInput;
    private readonly ConcurrentQueue<string> events = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object consoleGate = new();
    private Task? renderTask;
    private string[] lastFrame = [];
    private int lastFrameWidth;
    private int lastFrameHeight;
    private string input = string.Empty;
    private MicrosoftDeviceCodePrompt? deviceCodePrompt;
    private bool terminalModeEntered;

    public TerminalDashboard(SessionRuntimeRegistry registry, bool acceptsInput)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.acceptsInput = acceptsInput;
        ValidateTerminal(Console.IsOutputRedirected, Console.IsInputRedirected, acceptsInput);
    }

    public void Start()
    {
        if (renderTask is not null) throw new InvalidOperationException("The terminal dashboard is already running.");
        ValidateTerminal(Console.IsOutputRedirected, Console.IsInputRedirected, acceptsInput);
        lock (consoleGate)
        {
            terminalModeEntered = true;
            Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
            Console.Out.Flush();
        }
        renderTask = RenderLoopAsync(lifetime.Token);
    }

    internal static void ValidateTerminal(bool outputRedirected, bool inputRedirected, bool acceptsInput)
    {
        if (outputRedirected)
            throw new InvalidOperationException("--dashboard requires an interactive terminal; stdout is redirected.");
        if (acceptsInput && inputRedirected)
            throw new InvalidOperationException(
                "Interactive --dashboard input requires a terminal; use --no-input when stdin is redirected.");
    }

    public void AddEvent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        events.Enqueue(text.Replace('\r', ' ').Replace('\n', ' '));
        while (events.Count > 500 && events.TryDequeue(out _)) { }
    }

    public void SetInput(string value) => Volatile.Write(ref input, value ?? string.Empty);

    public void SetDeviceCodePrompt(MicrosoftDeviceCodePrompt? prompt) =>
        Volatile.Write(ref deviceCodePrompt, prompt);

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        Render();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Render();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Render()
    {
        RuntimeHealthSnapshot snapshot = registry.Snapshot();
        int width = 120;
        int height = 30;
        try { width = Math.Clamp(Console.WindowWidth, 20, 200); }
        catch (IOException) { }
        try { height = Math.Clamp(Console.WindowHeight, 10, 100); }
        catch (IOException) { }
        string[] frame = ComposeFrame(
            snapshot,
            events.ToArray(),
            acceptsInput,
            Volatile.Read(ref input),
            Volatile.Read(ref deviceCodePrompt),
            width,
            height);

        lock (consoleGate)
        {
            bool fullRedraw = lastFrameWidth != width || lastFrameHeight != height || lastFrame.Length != frame.Length;
            string output = BuildTerminalUpdate(frame, lastFrame, fullRedraw);
            if (output.Length > 0)
            {
                Console.Write(output);
                Console.Out.Flush();
            }
            lastFrame = frame;
            lastFrameWidth = width;
            lastFrameHeight = height;
        }
    }

    internal static string[] ComposeFrame(
        RuntimeHealthSnapshot snapshot,
        IReadOnlyList<string> recentEvents,
        bool acceptsInput,
        string currentInput,
        MicrosoftDeviceCodePrompt? deviceCodePrompt,
        int width,
        int height)
    {
        width = Math.Clamp(width, 20, 200);
        height = Math.Clamp(height, 10, 100);
        string uptime = TimeSpan.FromSeconds(snapshot.UptimeSeconds).ToString(@"d\.hh\:mm\:ss");
        List<string> lines = [];
        lines.Add(Fit("OeXYZ Minecraft Console Client · terminal dashboard", width - 1));
        lines.Add(Fit(
            $"{(snapshot.Healthy ? "HEALTHY" : "UNHEALTHY")} | Connected {snapshot.ConnectedSessions}/{snapshot.ActiveSessions} | " +
            $"Uptime {uptime} | " +
            $"RAM {snapshot.WorkingSetBytes / 1024D / 1024D:0.0} MiB | CPU {snapshot.CpuPercent:0.0}% | TTY {width}x{height}", width - 1));
        MicrosoftDeviceCodePrompt? prompt = deviceCodePrompt;
        if (prompt is not null)
        {
            lines.Add(new string('─', Math.Max(1, width - 1)));
            lines.Add("MICROSOFT DEVICE SIGN-IN · this temporary code is shown on screen only and is not logged");
            lines.Add(Fit($"Open {prompt.VerificationUrl} and enter code {prompt.UserCode}", width - 1));
            lines.Add($"Expires {prompt.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        }
        lines.Add(new string('─', Math.Max(1, width - 1)));
        lines.Add(Fit("SESSION               STATUS               VERSION / PROTOCOL       PING    HP / FOOD   RECONNECT", width - 1));

        int fixedLinesBeforePrompt = lines.Count + 2; // section divider and event heading
        int contentBudget = Math.Max(0, height - 1 - fixedLinesBeforePrompt);
        int desiredEventLines = Math.Min(recentEvents.Count, Math.Max(1, contentBudget - 2));
        int visibleSessions = snapshot.Sessions.Count == 0
            ? 0
            : Math.Min(snapshot.Sessions.Count, Math.Max(1, (contentBudget - desiredEventLines) / 2));
        foreach (RuntimeSessionStatus session in snapshot.Sessions.Take(visibleSessions))
        {
            string version = session.MinecraftVersion is null
                ? "-"
                : $"{session.MinecraftVersion} / {session.ProtocolVersion?.ToString() ?? "-"}";
            string ping = session.PingMilliseconds is long value ? $"{value}ms" : "-";
            string vital = session.Health is float health && session.Food is int food ? $"{health:0.#} / {food}" : "-";
            lines.Add(Fit(
                $"{Fit(session.Name, 21),-21} {Fit(session.Status, 20),-20} {Fit(version, 24),-24} " +
                $"{Fit(ping, 7),-7} {Fit(vital, 11),-11} {session.ReconnectCount,4}", width - 1));
            string position = session.X is double x && session.Y is double y && session.Z is double z
                ? $"XYZ {x:0.0} / {y:0.0} / {z:0.0}"
                : "XYZ -";
            string lastPacket = session.LastPacketAt is DateTimeOffset received
                ? $"last packet {Math.Max(0, (int)(DateTimeOffset.UtcNow - received).TotalSeconds)}s ago"
                : "no packet received";
            lines.Add(Fit(
                $"  {position} | RX {FormatBytes(session.BytesReceived)} / {session.PacketsReceived} packets | " +
                $"TX {FormatBytes(session.BytesSent)} / {session.PacketsSent} packets | {lastPacket}",
                width - 1));
        }
        if (snapshot.Sessions.Count == 0 && contentBudget > 0) lines.Add("No sessions registered.");
        int frameWidth = Math.Max(4, width - 1);
        int chatBodyRows = Math.Max(1, height - 1 - lines.Count - 2);
        string[] visibleEvents = recentEvents.TakeLast(chatBodyRows).ToArray();
        lines.Add(FrameTop("CHAT / EVENTS · newest at bottom · sensitive values redacted", frameWidth));
        int chatPaddingLines = chatBodyRows - visibleEvents.Length;
        for (int index = 0; index < chatPaddingLines; index++) lines.Add(FrameLine(string.Empty, frameWidth));
        foreach (string line in visibleEvents) lines.Add(FrameLine(line, frameWidth));
        lines.Add(FrameBottom(frameWidth));

        if (lines.Count > height - 1) lines.RemoveRange(height - 1, lines.Count - (height - 1));
        while (lines.Count < height - 1) lines.Add(string.Empty);

        string promptLine;
        if (acceptsInput)
        {
            string visible = SensitiveDataRedactor.IsSensitiveCommand(currentInput)
                ? SensitiveDataRedactor.RedactCommand(currentInput)
                : currentInput;
            promptLine = "> " + Fit(visible, width - 3);
        }
        else
        {
            promptLine = "Service input disabled · Ctrl+C/SIGTERM stops cleanly";
        }
        lines.Add(promptLine);
        // Keep one terminal cell unused at the right edge. Writing into the
        // final cell can trigger an implicit line wrap in several terminals.
        // Padding every rendered row lets us overwrite changed rows in one
        // pass instead of clearing them first, which prevents visible flicker.
        string[] frame = lines
            .Select(line => Fit(line, width - 1).PadRight(width - 1))
            .ToArray();
        return frame;
    }

    internal static string BuildTerminalUpdate(
        IReadOnlyList<string> frame,
        IReadOnlyList<string> previousFrame,
        bool fullRedraw)
    {
        StringBuilder output = new();
        if (fullRedraw) output.Append("\x1b[2J");
        for (int index = 0; index < frame.Count; index++)
        {
            if (!fullRedraw && index < previousFrame.Count &&
                string.Equals(previousFrame[index], frame[index], StringComparison.Ordinal)) continue;
            output.Append("\x1b[").Append(index + 1).Append(";1H").Append(frame[index]);
        }
        return output.ToString();
    }

    private static string Fit(string value, int width)
    {
        if (width <= 0) return string.Empty;
        string normalized = TerminalTextSanitizer.Sanitize(value);
        return normalized.Length <= width ? normalized : normalized[..Math.Max(1, width - 1)] + "…";
    }

    private static string FrameTop(string title, int width)
    {
        int interiorWidth = Math.Max(2, width - 2);
        string label = Fit($" {title} ", interiorWidth);
        return "┌" + label + new string('─', interiorWidth - label.Length) + "┐";
    }

    private static string FrameLine(string value, int width)
    {
        int interiorWidth = Math.Max(0, width - 4);
        string content = Fit(value, interiorWidth).PadRight(interiorWidth);
        return "│ " + content + " │";
    }

    private static string FrameBottom(int width) =>
        "└" + new string('─', Math.Max(2, width - 2)) + "┘";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / 1024D / 1024D / 1024D:0.0} GiB",
        >= 1024L * 1024L => $"{bytes / 1024D / 1024D:0.0} MiB",
        >= 1024L => $"{bytes / 1024D:0.0} KiB",
        _ => $"{bytes} B"
    };

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try
        {
            if (renderTask is not null) await renderTask.ConfigureAwait(false);
        }
        finally
        {
            lock (consoleGate)
            {
                if (terminalModeEntered)
                {
                    Console.Write("\x1b[?25h\x1b[?1049l");
                    Console.Out.Flush();
                    terminalModeEntered = false;
                }
            }
            lifetime.Dispose();
        }
    }
}
