using System.Globalization;
using System.Text;
using Autopilot.Core.Config;
using Autopilot.Core.Evals;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Runs;

namespace Autopilot.Core.Pool;

/// <summary>What happened to the pool items in one pool run, in the order they were tried.</summary>
public sealed record PoolRunResult(
    IReadOnlyList<ImplementResult> Done,
    IReadOnlyList<ImplementResult> Rejected,
    IReadOnlyList<ImplementResult> NeedsHuman,
    IReadOnlyList<string> NotReached,
    string? StoppedBecause,
    string? ReportPath)
{
    /// <summary>Tasks left for a person in this run: the reason is the pool master's or the pool's own.</summary>
    public IReadOnlyList<ParkedTask> Parked { get; init; } = [];

    /// <summary>What the pool master decided in this run.</summary>
    public IReadOnlyList<PoolDecision> Decisions { get; init; } = [];

    /// <summary>Bug tasks the pool created because it suspected a defect in Autopilot itself.</summary>
    public IReadOnlyList<string> Bugs { get; init; } = [];

    /// <summary>Agent runs started by this pool run (implement attempts and master calls).</summary>
    public int AgentRuns { get; init; }

    public bool Unattended { get; init; }

    /// <summary>Tasks whose run in an earlier pool died; their slots were released and they are tried again.</summary>
    public IReadOnlyList<string> Recovered { get; init; } = [];

    /// <summary>What was cleaned up for tasks that an earlier, dead run left behind (branches, worktrees).</summary>
    public IReadOnlyList<string> CleanedUp { get; init; } = [];

    public bool Succeeded => StoppedBecause is null && Rejected.Count == 0;
}

/// <summary>Everything the unattended pool needs besides the queue. All parts are optional: without them the pool is the plain, attended one.</summary>
public sealed class PoolRunOptions
{
    /// <summary>The pool settings, read again before each task starts so a changed concurrency applies to the next one.</summary>
    public Func<PoolConfig> Config { get; init; } = () => new PoolConfig();

    /// <summary>Forces unattended mode on (the <c>--unattended</c> flag). Null follows <c>pool.unattended</c>.</summary>
    public bool? Unattended { get; init; }

    public IPoolMaster? Master { get; init; }

    public IPoolTasks? Tasks { get; init; }

    public PoolStateStore? State { get; init; }

    public TaskRunLock? TaskLock { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The subscription usage in percent when it is known, otherwise null (then only <c>maxAgentRuns</c> limits).</summary>
    public Func<double?>? UsagePercent { get; init; }

    /// <summary>No task is started after this local time of day (a night run that stops at six).</summary>
    public TimeOnly? Until { get; init; }

    /// <summary>How often a pool with waiting tasks looks at the settings again while agents work.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a running pool records that it is alive.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A run that has not beaten for this long is dead: its slots and locks are released.</summary>
    public TimeSpan StaleAfter { get; init; } = PoolStateStore.DefaultStaleAfter;

    /// <summary>Tells whether a process still runs. Null uses the operating system.</summary>
    public Func<int, bool>? IsProcessAlive { get; init; }

    /// <summary>The id of this process (a test gives its own). Null uses the real one.</summary>
    public Func<int>? CurrentPid { get; init; }

    /// <summary>Where the run's events are appended (one run id per <c>RunAsync</c> call). Null records nothing.</summary>
    public PoolRunEventStore? Events { get; init; }
}

/// <summary>
/// Runs the pool. Up to <c>pool.concurrency</c> agents work at once; tasks whose write paths overlap, or that
/// wait for another task, are started one after the other. It stops on an infrastructure error (Claude Code
/// signed out, a usage limit, a program that cannot start): every later item would fail the same way. Items
/// that were tried leave the pool; items not reached and the item that hit the error stay. Takes the project's
/// run lock first and one lock per task.
/// In unattended mode the pool master answers needs-human questions and classifies failures; what it must
/// not decide is parked for a person and the pool goes on.
/// </summary>
public sealed class PoolRunner(
    PoolService pool,
    RunStore runs,
    ProjectRunLock runLock,
    string projectKey,
    Func<string, CancellationToken, Task<ImplementResult>> implement,
    PoolRunOptions? options = null)
{
    public const string ReportFileName = "POOL-REPORT.md";

    /// <summary>A pool pause that only says the budget is used up. It starts the pause reason the Pool page shows.</summary>
    public const string BudgetPausePrefix = "Budget used up";

    private const string PersonPauseStop = "The pool is paused by a person. No new task starts; tasks already started finish.";

    private readonly PoolRunOptions _options = options ?? new PoolRunOptions();

    private sealed record Processed(string Bucket, ImplementResult? Result, string? StopBecause, bool Keep, string? Message = null);

    private const string BucketDone = "done";
    private const string BucketRejected = "rejected";
    private const string BucketNeedsHuman = "needs-human";
    private const string BucketParked = "parked";
    private const string BucketStopped = "stopped";
    private const string BucketSkipped = "skipped";

    /// <param name="limit">At most this many items are tried; the rest are listed as not reached.</param>
    /// <param name="reportFolder">Where <c>POOL-REPORT.md</c> is written (the project's workspace folder).</param>
    public async Task<PoolRunResult> RunAsync(int? limit, string reportFolder, CancellationToken ct)
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be greater than 0.");
        }

        var clock = _options.Clock;
        var state = _options.State;
        var unattended = _options.Unattended ?? _options.Config().Unattended;
        using var held = runLock.Acquire(projectKey, "pool run");
        var runId = Guid.NewGuid().ToString("N");
        var eventLock = new object();
        void Record(PoolRunEvent runEvent, string? taskId = null, string? detail = null)
        {
            if (_options.Events is { } events)
            {
                lock (eventLock)
                {
                    events.Append(runId, runEvent, clock.GetUtcNow(), taskId, detail);
                }
            }
        }

        Record(PoolRunEvent.Enqueue);
        Record(PoolRunEvent.Start);
        var queue = pool.List().Select(e => e.WorkItemId).ToList();
        var done = new List<ImplementResult>();
        var rejected = new List<ImplementResult>();
        var needsHuman = new List<ImplementResult>();
        var notReached = new List<string>();
        var parked = new List<ParkedTask>();
        var decisions = new List<PoolDecision>();
        var bugs = new List<string>();
        var agentRuns = 0;
        var decisionsMade = 0;
        string? stopped = null;
        var budgetLock = new object();
        var cleanedUp = new List<string>();
        var pid = (_options.CurrentPid ?? (() => Environment.ProcessId))();

        // An earlier run that died (killed, crashed, rebooted) left slots and locks behind. We hold the project
        // now, so whatever it owned is released and its tasks, still in the pool, are tried again.
        var recovered = state?.ReleaseDead(clock.GetUtcNow(), _options.StaleAfter, _options.IsProcessAlive ?? ProjectRunLock.IsAlive, pid) ?? [];
        state?.ClearSlots();
        using var beat = new Heartbeat(state, pid, clock);

        // A pause from an earlier run (infrastructure) holds until its retry time. A budget pause is only a
        // message: the budget itself is checked again below, so a raised limit takes effect at once.
        if (state?.CurrentPause() is { } pause)
        {
            if (PoolStateStore.IsPersonPause(pause))
            {
                stopped = PersonPauseStop;
            }
            else if (pause.Reason.StartsWith(BudgetPausePrefix, StringComparison.Ordinal))
            {
                state.Resume();
                Record(PoolRunEvent.PauseBudget, null, pause.Reason);
                Record(PoolRunEvent.Resume);
            }
            else if (pause.RetryAt is { } retry && retry > clock.GetUtcNow())
            {
                stopped = $"The pool is paused until {retry.ToLocalTime():yyyy-MM-dd HH:mm}: {pause.Reason}";
            }
            else
            {
                state.Resume();
                Record(PoolRunEvent.PauseInfrastructure, null, pause.Reason);
                Record(PoolRunEvent.Resume);
            }
        }

        var startedAt = clock.GetUtcNow();
        DateTimeOffset? deadline = null;
        if (_options.Until is { } until)
        {
            var local = TimeZoneInfo.ConvertTime(startedAt, clock.LocalTimeZone);
            var day = local.Date + until.ToTimeSpan();
            if (day <= local.DateTime)
            {
                day = day.AddDays(1);
            }

            deadline = new DateTimeOffset(day, clock.LocalTimeZone.GetUtcOffset(day));
        }

        string? Reserve(string? workItemId, string kind)
        {
            lock (budgetLock)
            {
                var config = _options.Config();
                var now = clock.GetUtcNow();
                if (config.Budget.MaxAgentRuns is { } max)
                {
                    var used = state?.AgentRunsInLastDay(now) ?? agentRuns;
                    if (used >= max)
                    {
                        return BudgetStop($"The budget of {max} agent runs in 24 hours is used up.", state?.BudgetResetsAt(now, max));
                    }
                }

                if (_options.UsagePercent?.Invoke() is { } percent && percent >= config.Budget.StopAtUsagePercent)
                {
                    return BudgetStop($"Subscription usage is {percent:0}%, at or above the limit of {config.Budget.StopAtUsagePercent}%.", null);
                }

                agentRuns++;
                state?.CountAgentRun(workItemId, kind, now);
                return null;
            }
        }

        string? BudgetProblem()
        {
            lock (budgetLock)
            {
                var config = _options.Config();
                var now = clock.GetUtcNow();
                if (config.Budget.MaxAgentRuns is { } max && (state?.AgentRunsInLastDay(now) ?? agentRuns) >= max)
                {
                    return BudgetStop($"The budget of {max} agent runs in 24 hours is used up.", state?.BudgetResetsAt(now, max));
                }

                return _options.UsagePercent?.Invoke() is { } percent && percent >= config.Budget.StopAtUsagePercent
                    ? BudgetStop($"Subscription usage is {percent:0}%, at or above the limit of {config.Budget.StopAtUsagePercent}%.", null)
                    : null;
            }
        }

        // A used-up budget is a pool state, not a task failure: the pool says so (with the time the budget has room
        // again, when known) and stops; the task that was about to run stays queued.
        string BudgetStop(string reason, DateTimeOffset? resetsAt)
        {
            if (unattended)
            {
                var when = resetsAt is { } at ? $", resets at {TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone):yyyy-MM-dd HH:mm}" : string.Empty;
                state?.Pause($"{BudgetPausePrefix}{when}. {reason}", resetsAt, clock.GetUtcNow());
                Record(PoolRunEvent.PauseBudget, null, reason);
            }

            return reason;
        }

        var infos = new Dictionary<string, PoolTaskInfo>(StringComparer.Ordinal);
        var pending = new List<string>(queue);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var running = new Dictionary<Task<Processed>, (string Id, DateTimeOffset Started)>();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var failedIds = new HashSet<string>(StringComparer.Ordinal);
        var tried = 0;
        var lastWait = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
        while (!ct.IsCancellationRequested)
        {
            // Read the queue again: a task added, unparked or moved by a person takes effect at once, and a task
            // removed while it waits is never started. Tasks this run already started or parked are not added again.
            var current = pool.List().Select(e => e.WorkItemId).Where(id => !handled.Contains(id) && !running.Values.Any(r => r.Id == id)).ToList();
            foreach (var gone in pending.Except(current, StringComparer.Ordinal).ToList())
            {
                state?.ClearSlot(gone);
                lastWait.Remove(gone);
            }

            pending = current;

            // Fill the free slots, in queue order, with tasks that may start now.
            if (stopped is null)
            {
                var slots = Math.Clamp(_options.Config().Concurrency, 1, PoolConfig.MaxConcurrency);

                // A task whose paths are only the project's writable paths may have been triaged since (by its own
                // run, which holds a slot while it triages): look again, so tasks with disjoint paths start together.
                foreach (var known in infos.Where(p => p.Value.PathSource == PathSource.Project).Select(p => p.Key).ToList())
                {
                    infos.Remove(known);
                    await InfoAsync(known).ConfigureAwait(false);
                }

                for (var i = 0; i < pending.Count && stopped is null;)
                {
                    var id = pending[i];
                    if (limit is { } max && tried >= max)
                    {
                        break;
                    }

                    // A person may pause the pool while it runs: no new task starts, the started ones finish.
                    if (state?.CurrentPause() is { } personPause && PoolStateStore.IsPersonPause(personPause))
                    {
                        stopped = PersonPauseStop;
                        break;
                    }

                    if (deadline is { } end && clock.GetUtcNow() >= end)
                    {
                        stopped = $"The stop time {_options.Until:HH\\:mm} was reached.";
                        break;
                    }

                    if (BudgetProblem() is { } problem)
                    {
                        stopped = problem;
                        break;
                    }

                    var info = await InfoAsync(id).ConfigureAwait(false);
                    var wait = WaitReason(id, info);
                    if (wait is not null)
                    {
                        if (wait.StartsWith("dependency failed:", StringComparison.Ordinal))
                        {
                            var why = wait["dependency failed:".Length..].Trim();
                            parked.Add(Park(id, null, $"It depends on a task that did not finish ({why})."));
                            Record(PoolRunEvent.TaskParked, id, "dependency failed");
                            pending.RemoveAt(i);
                            handled.Add(id);
                            state?.ClearSlot(id);
                            pool.Remove(id);
                            continue;
                        }

                        SetWait(id, wait);
                        i++;
                        continue;
                    }

                    if (running.Count >= slots)
                    {
                        SetWait(id, $"all {slots} slot(s) are busy");
                        i++;
                        continue;
                    }

                    pending.RemoveAt(i);
                    handled.Add(id);
                    tried++;
                    lastWait.Remove(id);
                    var started = clock.GetUtcNow();
                    state?.SetSlot(id, "running", $"slot {running.Count + 1}", started);
                    Record(PoolRunEvent.SlotGranted, id, $"slot {running.Count + 1}");
                    running[Work(id, unattended, Reserve, decisions, bugs, cleanedUp, () => Interlocked.Increment(ref decisionsMade), Record, ct)] = (id, started);
                }
            }

            if (running.Count == 0)
            {
                if (pending.Count > 0 && stopped is null && !(limit is { } m && tried >= m))
                {
                    // Nothing runs and nothing can start: tasks that wait on each other.
                    foreach (var id in pending.ToList())
                    {
                        parked.Add(Park(id, null, "It waits for tasks that can never finish first (a dependency cycle)."));
                        Record(PoolRunEvent.TaskParked, id, "dependency cycle");
                        handled.Add(id);
                        state?.ClearSlot(id);
                        pool.Remove(id);
                    }

                    pending.Clear();
                }

                break;
            }

            // While tasks wait for a slot, look again now and then: a person may have raised the concurrency.
            // The pool also beats now and then, so a run that dies is noticed.
            beat.Touch();
            var waiting = new List<Task>(running.Keys);
            waiting.Add(Task.Delay(Min(_options.PollInterval, _options.HeartbeatInterval), ct));

            var woken = await Task.WhenAny(waiting).ConfigureAwait(false);
            if (woken is not Task<Processed> completed)
            {
                continue;
            }

            var (doneId, doneStarted) = running[completed];
            running.Remove(completed);
            state?.ClearSlot(doneId);
            var outcome = await completed.ConfigureAwait(false);
            finished.Add(doneId);
            switch (outcome.Bucket)
            {
                case BucketStopped:
                    stopped ??= outcome.StopBecause;
                    notReached.Add($"{doneId}: stopped here. {outcome.Message ?? outcome.StopBecause}");
                    failedIds.Add(doneId);
                    break;
                case BucketSkipped:
                    notReached.Add($"{doneId}: {outcome.Message}");
                    break;
                default:
                    runs.Record(outcome.Result!, doneStarted);
                    pool.Remove(doneId);
                    switch (outcome.Bucket)
                    {
                        case BucketDone:
                            done.Add(outcome.Result!);
                            Record(PoolRunEvent.TaskMerged, doneId);
                            break;
                        case BucketNeedsHuman:
                            needsHuman.Add(outcome.Result!);
                            failedIds.Add(doneId);
                            break;
                        case BucketParked:
                            parked.Add(new ParkedTask(doneId, null, outcome.Message ?? outcome.Result!.Message, clock.GetUtcNow()));
                            Record(PoolRunEvent.TaskParked, doneId, outcome.Message ?? outcome.Result!.Message);
                            failedIds.Add(doneId);
                            break;
                        default:
                            rejected.Add(outcome.Result!);
                            failedIds.Add(doneId);
                            break;
                    }

                    break;
            }
        }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever went wrong, the report is still written and the exit code follows the result.
            stopped ??= $"The pool stopped on an unexpected error: {ex.GetType().Name}: {ex.Message}";
        }

        ct.ThrowIfCancellationRequested();
        state?.ClearSlots();
        Record(PoolRunEvent.WorkFinished);
        Record(PoolRunEvent.RunFinished, null, stopped);
        foreach (var id in pending)
        {
            notReached.Add(stopped is not null
                ? $"{id}: not started. {stopped}"
                : $"{id}: not started, the limit of {limit} was reached.");
        }

        var result = new PoolRunResult(done, rejected, needsHuman, notReached, stopped, Path.Combine(reportFolder, ReportFileName))
        {
            Parked = parked,
            Decisions = decisions,
            Bugs = bugs,
            AgentRuns = agentRuns,
            Unattended = unattended,
            Recovered = recovered,
            CleanedUp = cleanedUp,
        };
        Directory.CreateDirectory(reportFolder);
        await File.WriteAllTextAsync(result.ReportPath!, Report(result, clock.GetUtcNow()), ct).ConfigureAwait(false);
        return result;

        async Task<PoolTaskInfo> InfoAsync(string id)
        {
            if (!infos.TryGetValue(id, out var info))
            {
                PoolTaskInfo? described = null;
                try
                {
                    described = _options.Tasks is { } tasks ? await tasks.DescribeAsync(id, ct).ConfigureAwait(false) : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A task that cannot be read is tried alone; its own run records what is wrong.
                }

                info = described ?? new PoolTaskInfo(id, string.Empty, ["**"], []);
                infos[id] = info;
            }

            return info;
        }

        string? WaitReason(string id, PoolTaskInfo info)
        {
            foreach (var dependency in info.DependsOn)
            {
                if (failedIds.Contains(dependency))
                {
                    return $"dependency failed: task {dependency} was not completed";
                }

                if (pending.Contains(dependency) || running.Values.Any(r => r.Id == dependency))
                {
                    return $"waits for task {dependency} (depends-on)";
                }
            }

            foreach (var other in running.Values.Select(r => r.Id))
            {
                if (infos.TryGetValue(other, out var otherInfo) && PathOverlap.Find(info.Paths, otherInfo.Paths) is { } overlap)
                {
                    var why = PathOverlap.Describe(overlap);
                    var unknown = info.PathSource == PathSource.Project ? id : otherInfo.PathSource == PathSource.Project ? other : null;
                    return unknown is null
                        ? $"shares files with task {other} ({why})"
                        : $"shares files with task {other} ({why}); the expected paths of task {unknown} are not known yet, so the project's writable paths stand in";
                }
            }

            return null;
        }

        void SetWait(string id, string reason)
        {
            if (!lastWait.TryGetValue(id, out var last) || last != reason)
            {
                lastWait[id] = reason;
                state?.SetSlot(id, "waiting", reason, clock.GetUtcNow());
            }
        }

        ParkedTask Park(string id, string? category, string reason)
        {
            var at = clock.GetUtcNow();
            state?.Park(id, category, reason, at);
            return new ParkedTask(id, category, reason, at);
        }
    }

    private Task<Processed> Work(string id, bool unattended, Func<string?, string, string?> reserve, List<PoolDecision> decisions, List<string> bugs, List<string> cleanedUp, Func<int> nextDecision, Action<PoolRunEvent, string?, string?> record, CancellationToken ct) =>
        Task.Run(() => WorkAsync(id, unattended, reserve, decisions, bugs, cleanedUp, nextDecision, record, ct), CancellationToken.None);

    private async Task<Processed> WorkAsync(string id, bool unattended, Func<string?, string, string?> reserve, List<PoolDecision> decisions, List<string> bugs, List<string> cleanedUp, Func<int> nextDecision, Action<PoolRunEvent, string?, string?> record, CancellationToken ct)
    {
        IDisposable? taskLock = null;
        try
        {
            if (_options.TaskLock is { } locks)
            {
                try
                {
                    taskLock = locks.Acquire(projectKey, id, "pool run");
                }
                catch (TaskLockHeldException ex)
                {
                    return new Processed(BucketSkipped, null, null, true, ex.Message);
                }
            }

            var config = _options.Config();
            var master = unattended && config.Master.Enabled ? _options.Master : null;
            var clock = _options.Clock;

            // We hold the task's lock, so no live run owns it: what a dead run left (its branch, its worktree) goes.
            if (_options.Tasks is { } cleaning && await cleaning.ReleaseLeftoversAsync(id, ct).ConfigureAwait(false) is { } cleaned)
            {
                lock (cleanedUp) { cleanedUp.Add($"{id}: {cleaned}"); }
            }

            var attempts = 0;
            var retriages = 0;
            var firstAttempt = true;

            // Triage comes first: implementing a task that has no triage only wastes an agent run.
            if (master is not null && _options.Tasks is { } preparing && await PrepareAsync(master, config, preparing).ConfigureAwait(false) is { } unready)
            {
                return unready;
            }

            while (true)
            {
                if (reserve(id, firstAttempt ? "implement" : "retry") is { } budget)
                {
                    return new Processed(BucketStopped, null, budget, true);
                }

                firstAttempt = false;
                ImplementResult result;
                try
                {
                    result = await implement(id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new ImplementResult(id, ImplementOutcome.Failed, ex.Message);
                }

                var infrastructure = result.Outcome == ImplementOutcome.Failed ? InfrastructureErrors.Classify(result.Message) : null;
                if (infrastructure is not null)
                {
                    // A usage limit is not the task's fault: the task stays, no retry is used, and the pause
                    // lasts until the reset time in the message (or the configured minutes).
                    var resetAt = UsageLimit.IsUsageLimit(result.Message) ? UsageLimit.ParseResetTime(result.Message, clock) : null;
                    return Pause(id, infrastructure, config, resetAt);
                }

                switch (result.Outcome)
                {
                    case ImplementOutcome.Published:
                        _options.State?.ClearNotes(id);
                        return await IntegrateAsync(result, ct).ConfigureAwait(false);

                    case ImplementOutcome.NotReady when master is not null && _options.Tasks is { } tasks:
                    {
                        // The stored triage changed under us (an edit, a person): look at it again once.
                        if (retriages++ > 0)
                        {
                            return Parked(result, null, $"The task could not be made ready: {result.Message}");
                        }

                        if (await PrepareAsync(master, config, tasks).ConfigureAwait(false) is { } notReady)
                        {
                            return notReady;
                        }

                        continue;
                    }

                    case ImplementOutcome.Rejected or ImplementOutcome.Failed when unattended && _options.Tasks is { } tasks:
                    {
                        var verdict = master is null
                            ? new FailureVerdict(FailureKind.Task, string.Empty, "No pool master, so the failure is treated as the task's own.", null)
                            : await ClassifyAsync(id, result, master, reserve, ct).ConfigureAwait(false);
                        if (verdict.Kind == FailureKind.Budget)
                        {
                            // Not the task's fault and not a decision: no failure is recorded, no retry is used up.
                            // The failed attempt's branch is set aside so the task starts clean when the budget has room.
                            await tasks.PrepareRetryAsync(id, ct).ConfigureAwait(false);
                            return new Processed(BucketStopped, null, verdict.Reason, true, "The budget ran out before the failed run could be looked at, so the task stays queued.");
                        }

                        if (master is not null)
                        {
                            lock (decisions) { decisions.Add(Store(id, DecisionKinds.Failure, result.Message, verdict.Kind.ToString(), verdict.Reason, null)); }
                        }

                        switch (verdict.Kind)
                        {
                            case FailureKind.Infrastructure:
                                return Pause(id, verdict.Reason, config);
                            case FailureKind.AutopilotDefect:
                                var title = string.IsNullOrWhiteSpace(verdict.BugTitle) ? $"Autopilot defect while working on task {id}" : verdict.BugTitle!;
                                var bugId = await tasks.CreateBugAsync(title, $"Found by the pool master while task {id} failed.\n\n{verdict.Reason}\n\nLog excerpt:\n{Excerpt(result.Message)}", ct).ConfigureAwait(false);
                                lock (bugs) { bugs.Add($"{bugId}: {title}"); }
                                return Parked(result, null, $"The pool master suspects a defect in Autopilot (bug task {bugId}): {verdict.Reason}");
                            default:
                                if (attempts < config.OnFailure.Retries)
                                {
                                    attempts++;
                                    await tasks.PrepareRetryAsync(id, ct).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(verdict.Notes))
                                    {
                                        await tasks.AnswerAsync(id, $"Notes for the next attempt (pool master): {verdict.Notes.Trim()}", ct).ConfigureAwait(false);
                                    }

                                    continue;
                                }

                                return Parked(result, null, $"It failed {attempts + 1} time(s) and the retries are used up: {Excerpt(result.Message)}");
                        }
                    }

                    case ImplementOutcome.NotReady or ImplementOutcome.NotFound:
                        return new Processed(BucketNeedsHuman, result, null, false);

                    default:
                        return new Processed(BucketRejected, result, null, false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A locked worktree ("being used by another process") or any other error of one task never ends the pool:
            // the task is parked as interrupted and the pool goes on with the next one.
            var reason = ex is IOException
                ? $"Interrupted: a file or folder of the task is in use by another process ({ex.Message}). It can be tried again."
                : $"Interrupted by an unexpected error ({ex.GetType().Name}): {ex.Message}";
            return Parked(new ImplementResult(id, ImplementOutcome.Failed, reason), null, reason);
        }
        finally
        {
            taskLock?.Dispose();
        }

        Processed Parked(ImplementResult result, string? category, string reason)
        {
            _options.State?.Park(id, category, reason, _options.Clock.GetUtcNow());
            return new Processed(BucketParked, result, null, false, reason + ((category is not null) ? $" ({category})" : string.Empty));
        }

        // Makes the task ready to implement: reads the stored triage, triages when there is none, lets the pool
        // master answer a needs-human question and triages again. Null when the task is agent-ready.
        async Task<Processed?> PrepareAsync(IPoolMaster master, PoolConfig config, IPoolTasks tasks)
        {
            var triage = await tasks.TriageStateAsync(id, ct).ConfigureAwait(false);
            if (triage.State == PoolTriageState.Missing)
            {
                if (TriageHolderPark() is { } busy)
                {
                    return busy;
                }

                if (reserve(id, "triage") is { } problem)
                {
                    return new Processed(BucketStopped, null, problem, true);
                }

                triage = await tasks.RetriageAsync(id, ct).ConfigureAwait(false);
                if (triage.State == PoolTriageState.Missing)
                {
                    return NotReadyPark(null, $"The task could not be triaged: {Excerpt(triage.Detail)}");
                }
            }

            if (triage.State == PoolTriageState.AgentReady)
            {
                return null;
            }

            var info = await tasks.DescribeAsync(id, ct).ConfigureAwait(false);
            AnswerVerdict verdict;
            try
            {
                verdict = await AnswerAsync(id, info, triage.Detail, master, config, reserve, decisions, nextDecision, tasks, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return NotReadyPark(null, $"Triage marked the task needs-human, and it cannot be answered: {ex.Message}");
            }

            if (verdict.Stop is { } stop)
            {
                return new Processed(BucketStopped, null, stop, true);
            }

            if (verdict.Park is { } park)
            {
                return NotReadyPark(verdict.Category, park);
            }

            if (TriageHolderPark() is { } held)
            {
                // Someone else triages it now: use what is stored, if it is good.
                triage = await tasks.TriageStateAsync(id, ct).ConfigureAwait(false);
                return triage.State == PoolTriageState.AgentReady ? null : held;
            }

            if (reserve(id, "triage") is { } budget)
            {
                return new Processed(BucketStopped, null, budget, true);
            }

            triage = await tasks.RetriageAsync(id, ct).ConfigureAwait(false);
            return triage.State == PoolTriageState.AgentReady
                ? null
                : NotReadyPark(verdict.Category, $"The task is still not ready after the master's answer. Triage says {triage.Label}: {Excerpt(triage.Detail)}");
        }

        // When a live process (the web UI) holds the triage lock of this task, the pool does not triage it: it parks
        // the task as waiting and names the holder. Null when nobody triages it.
        Processed? TriageHolderPark() =>
            _options.TaskLock?.TriageHolder(projectKey, id) is { } holder
                ? NotReadyPark(null, $"Waiting: the triage of this task is running in {holder.Owner} (process {holder.Pid}) and no stored result exists yet. Run the pool again when it has finished.")
                : null;

        Processed NotReadyPark(string? category, string reason) =>
            Parked(new ImplementResult(id, ImplementOutcome.NotReady, reason), category, reason);

        Processed Pause(string taskId, string reason, PoolConfig config, DateTimeOffset? resetAt = null)
        {
            var now = _options.Clock.GetUtcNow();
            var retryAt = resetAt ?? now.AddMinutes(config.OnFailure.PauseMinutes);
            if (unattended)
            {
                _options.State?.Pause(reason, retryAt, now);
                record(reason.StartsWith(BudgetPausePrefix, StringComparison.Ordinal) ? PoolRunEvent.PauseBudget : PoolRunEvent.PauseInfrastructure, null, reason);
            }

            var message = unattended ? $"Stopped at {taskId}: {reason} The pool is paused; it can start again at {retryAt.ToLocalTime():HH:mm}." : $"Stopped at {taskId}: {reason}";
            return new Processed(BucketStopped, null, message, true, reason);
        }
    }

    private async Task<Processed> IntegrateAsync(ImplementResult result, CancellationToken ct)
    {
        if (_options.Tasks is not { } tasks)
        {
            return new Processed(BucketDone, result, null, false);
        }

        var merged = await tasks.IntegrateAsync(result, ct).ConfigureAwait(false);
        if (merged is null || merged.Merged)
        {
            return new Processed(BucketDone, result, null, false);
        }

        _options.State?.Park(result.WorkItemId, null, merged.Message, _options.Clock.GetUtcNow());
        return new Processed(BucketParked, result, null, false, merged.Message);
    }

    /// <summary><c>Park</c>: leave the task for a person. <c>Stop</c>: the budget is used up, the task stays queued and the pool stops.</summary>
    private sealed record AnswerVerdict(string? Park, string? Category, string? Stop = null);

    private async Task<AnswerVerdict> AnswerAsync(
        string id, PoolTaskInfo? info, string question, IPoolMaster master, PoolConfig config,
        Func<string?, string, string?> reserve, List<PoolDecision> decisions, Func<int> nextDecision, IPoolTasks tasks, CancellationToken ct)
    {
        // What the question is about comes from triage (and, below, from the master) as a structured field.
        if (info?.Topic is { } topic && config.AlwaysAsk.Contains(topic, StringComparer.OrdinalIgnoreCase))
        {
            return new AnswerVerdict($"Triage says the question is about {topic}, which is always left to a person.", topic);
        }

        // Only a real signal stops the task without asking: a secret value in the text, or a sensitive file to change.
        var text = $"{info?.Title}\n{info?.Body}\n{question}";
        var writePaths = info is { PathSource: not PathSource.Project } ? info.Paths : [];
        if (MasterGuard.SafetyNet(text, writePaths, config.AlwaysAsk) is { } hit)
        {
            return new AnswerVerdict(hit.Reason, hit.Category);
        }

        if (nextDecision() > config.Master.MaxDecisionsPerRun)
        {
            return new AnswerVerdict($"The master already made {config.Master.MaxDecisionsPerRun} decisions in this run.", null);
        }

        if (reserve(id, "master") is { } problem)
        {
            return new AnswerVerdict(null, null, problem);
        }

        var answer = await master.DecideAsync(new MasterQuestion(id, info?.Title ?? id, info?.Body ?? string.Empty, question), ct).ConfigureAwait(false);
        if (MasterGuard.ParkReason(answer, config.AlwaysAsk) is { } park)
        {
            return new AnswerVerdict(park, answer.Category);
        }

        var line = MasterGuard.AnswerLine(answer, answer.Reason);
        await tasks.AnswerAsync(id, line, ct).ConfigureAwait(false);
        lock (decisions) { decisions.Add(Store(id, DecisionKinds.Answer, question, string.IsNullOrWhiteSpace(answer.Answer) ? $"option {answer.Option}" : answer.Answer, answer.Reason, answer.Category)); }
        return new AnswerVerdict(null, answer.Category);
    }

    private async Task<FailureVerdict> ClassifyAsync(string id, ImplementResult result, IPoolMaster master, Func<string?, string, string?> reserve, CancellationToken ct)
    {
        if (reserve(id, "master") is { } problem)
        {
            return new FailureVerdict(FailureKind.Budget, string.Empty, problem, null);
        }

        var info = _options.Tasks is { } tasks ? await tasks.DescribeAsync(id, ct).ConfigureAwait(false) : null;
        return await master.ClassifyFailureAsync(new FailureInfo(id, info?.Title ?? id, result.Outcome.ToString(), result.Message, Excerpt(result.Message)), ct).ConfigureAwait(false);
    }

    private PoolDecision Store(string id, string kind, string question, string answer, string reason, string? category) =>
        _options.State?.AddDecision(id, kind, question, answer, reason, category, _options.Clock.GetUtcNow())
        ?? new PoolDecision(0, id, kind, question, answer, reason, category, _options.Clock.GetUtcNow(), null);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Beats while the run lives and removes its beat when it ends, so only a run that died leaves a stale one.</summary>
    private sealed class Heartbeat : IDisposable
    {
        private readonly PoolStateStore? _state;
        private readonly int _pid;
        private readonly TimeProvider _clock;

        public Heartbeat(PoolStateStore? state, int pid, TimeProvider clock)
        {
            _state = state;
            _pid = pid;
            _clock = clock;
            Touch();
        }

        public void Touch() => _state?.Beat(_pid, _clock.GetUtcNow());

        public void Dispose() => _state?.EndBeat(_pid);
    }

    private static string Excerpt(string text) => text.Length <= 2000 ? text : text[..2000] + "...";

    public static string Report(PoolRunResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        text.AppendLine("# Pool report");
        text.AppendLine();
        text.AppendLine($"Finished {at:yyyy-MM-dd HH:mm} UTC." + (result.Unattended ? " Ran without a person (unattended)." : string.Empty));
        if (result.StoppedBecause is not null)
        {
            text.AppendLine();
            text.AppendLine($"**{result.StoppedBecause}**");
        }

        Section(text, "Done (drafts waiting for a person)", result.Done.Select(r => $"{r.WorkItemId}: {r.Message}{Where(r)}"));
        Section(text, "Rejected", result.Rejected.Select(r => $"{r.WorkItemId}: {r.Message}{Where(r)}"));
        Section(text, "Needs a person", result.NeedsHuman.Select(r => $"{r.WorkItemId}: {r.Message}"));
        Section(text, "Parked for a person", result.Parked.Select(p => $"{p.WorkItemId}: {p.Reason}" + (p.Category is not null ? $" [{p.Category}]" : string.Empty)));
        Section(text, "Decisions of the pool master", result.Decisions.Select(d => $"{d.WorkItemId} ({d.Kind}): {d.Answer}. Why: {d.Reason}"));
        Section(text, "Bugs found in Autopilot", result.Bugs);
        Section(text, "Interrupted earlier (the run died, the task was tried again)", result.Recovered.Select(id => $"{id}: no sign of life from the earlier run."));
        Section(text, "Cleaned up after a run that died", result.CleanedUp);
        Section(text, "Not reached", result.NotReached);
        text.AppendLine();
        text.AppendLine($"Agent runs used: {result.AgentRuns.ToString(CultureInfo.InvariantCulture)}.");
        return text.ToString();
    }

    private static string Where(ImplementResult r) =>
        r.Location is not null ? $" (files: {r.Location})" : r.BranchName is not null ? $" (branch: {r.BranchName})" : string.Empty;

    private static void Section(StringBuilder text, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        text.AppendLine();
        text.AppendLine($"## {title} ({list.Count})");
        text.AppendLine();
        if (list.Count == 0)
        {
            text.AppendLine("None.");
            return;
        }

        foreach (var line in list)
        {
            text.AppendLine($"- {line.ReplaceLineEndings(" ")}");
        }
    }
}
