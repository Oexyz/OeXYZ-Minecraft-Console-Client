using System.Text.RegularExpressions;
using OeXYZ.Core;

namespace OeXYZ.Session;

internal sealed class SessionAutomationEngine
{
    private const int MaximumInputCharacters = 4096;
    private const int MaximumGlobalActionsPerHour = 240;
    private readonly IReadOnlyList<AutomationRuleProfile> rules;
    private readonly Func<string, CancellationToken, Task> send;
    private readonly Func<CancellationToken, Task> respawn;
    private readonly Action stop;
    private readonly Action reconnect;
    private readonly Action<string> notify;
    private readonly Action<string> log;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<Guid, Regex> regexes = [];
    private readonly Dictionary<Guid, DateTimeOffset> lastRun = [];
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> runHistory = [];
    private readonly Queue<DateTimeOffset> globalActions = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public SessionAutomationEngine(
        IReadOnlyList<AutomationRuleProfile> rules,
        Func<string, CancellationToken, Task> send,
        Func<CancellationToken, Task> respawn,
        Action stop,
        Action reconnect,
        Action<string> notify,
        Action<string> log,
        TimeProvider? timeProvider = null)
    {
        this.rules = rules.Where(rule => rule.Enabled).Take(32).ToArray();
        this.send = send;
        this.respawn = respawn;
        this.stop = stop;
        this.reconnect = reconnect;
        this.notify = notify;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        foreach (AutomationRuleProfile rule in this.rules.Where(rule => rule.UseRegex))
            regexes[rule.Id] = new Regex(rule.Pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
    }

    public async Task TriggerAsync(
        AutomationTriggerKind trigger,
        string input,
        CancellationToken cancellationToken)
    {
        string bounded = input.Length <= MaximumInputCharacters ? input : input[..MaximumInputCharacters];
        foreach (AutomationRuleProfile rule in rules.Where(rule => rule.Trigger == trigger))
        {
            if (!Matches(rule, bounded)) continue;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (lastRun.TryGetValue(rule.Id, out DateTimeOffset previous) &&
                    now - previous < TimeSpan.FromSeconds(rule.CooldownSeconds)) continue;
                Queue<DateTimeOffset> history = runHistory.GetValueOrDefault(rule.Id) ?? new Queue<DateTimeOffset>();
                runHistory[rule.Id] = history;
                Trim(history, now);
                Trim(globalActions, now);
                if (history.Count >= rule.MaximumRunsPerHour ||
                    globalActions.Count + rule.Actions.Count > MaximumGlobalActionsPerHour) continue;
                lastRun[rule.Id] = now;
                history.Enqueue(now);
                foreach (AutomationActionProfile action in rule.Actions.Take(4))
                {
                    globalActions.Enqueue(now);
                    await ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
                }
                log($"Automation rule '{rule.Name}' completed {rule.Actions.Count} bounded action(s).");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log($"Automation rule '{rule.Name}' failed: {exception.Message}");
            }
            finally { gate.Release(); }
        }
    }

    public async Task RunIntervalsAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await TriggerAsync(AutomationTriggerKind.Interval, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private bool Matches(AutomationRuleProfile rule, string input)
    {
        if (rule.Trigger is not (AutomationTriggerKind.ChatContains or AutomationTriggerKind.Mention or
            AutomationTriggerKind.PrivateMessage or AutomationTriggerKind.PlayerJoined or AutomationTriggerKind.PlayerLeft))
            return true;
        if (string.IsNullOrEmpty(rule.Pattern)) return false;
        return rule.UseRegex
            ? regexes[rule.Id].IsMatch(input)
            : input.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExecuteAsync(AutomationActionProfile action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case AutomationActionKind.SendChat:
                await send(action.Value, cancellationToken).ConfigureAwait(false);
                break;
            case AutomationActionKind.SendCommand:
                await send(action.Value.StartsWith('/') ? action.Value : "/" + action.Value, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case AutomationActionKind.Respawn:
                await respawn(cancellationToken).ConfigureAwait(false);
                break;
            case AutomationActionKind.Notify:
                notify(action.Value);
                break;
            case AutomationActionKind.Stop:
                stop();
                break;
            case AutomationActionKind.Reconnect:
                reconnect();
                break;
            default:
                throw new InvalidDataException("The automation action is unsupported.");
        }
    }

    private static void Trim(Queue<DateTimeOffset> history, DateTimeOffset now)
    {
        while (history.Count > 0 && now - history.Peek() >= TimeSpan.FromHours(1)) history.Dequeue();
    }
}
