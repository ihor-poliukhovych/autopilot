using System.Text.Json;
using Autopilot.Core.Jobs;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

public sealed record TaskListView(
    IReadOnlyList<WorkItemListEntry> Items,
    bool LocalTasks,
    bool LegacyTaskFiles,
    IReadOnlyDictionary<string, string> ActiveJobs,
    RunStatus Status,
    int Total,
    int Page,
    int PageSize,
    TaskCounts Counts,
    IReadOnlyDictionary<string, JobRecord> TriageJobs,
    IReadOnlyDictionary<string, PipelineCard>? Cards = null);

/// <summary>How many open tasks each state filter would show.</summary>
public sealed record TaskCounts(int All, int NeedsHuman, int AgentReady, int NotTriaged, int Stale);

/// <summary>The list filters: a state, a search text and a page. No page size means everything.</summary>
public sealed record TaskQuery(string? State = null, string? Search = null, int Page = 1, int? PageSize = null)
{
    public const int MaxPageSize = 200;
    public static readonly string[] States = ["needs-human", "agent-ready", "not-triaged", "stale"];

    public bool Matches(WorkItemListEntry item)
    {
        var stateOk = State switch
        {
            "needs-human" => item.Triage is { Label: "needs-human" },
            "agent-ready" => item.Triage is { Label: "agent-ready" },
            "not-triaged" => item.Triage is null,
            "stale" => item.Triage is { Stale: true },
            _ => true,
        };
        return stateOk && (string.IsNullOrWhiteSpace(Search)
            || item.Title.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
            || item.Id.Equals(Search.Trim().TrimStart('#'), StringComparison.Ordinal));
    }
}

public sealed record TaskTreeCounts(int Done, int InProgress, int NeedsYou, int NotStarted);

/// <summary>One node of the task tree. <see cref="Counts"/> is set on groups (tasks with children) only.</summary>
public sealed record TaskTreeNode(int Id, string Title, int? ParentId, bool Closed, string? Kind, TaskTreeCounts? Counts, IReadOnlyList<TaskTreeNode> Children)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal int Bucket { get; init; }

    /// <summary>How many leaf tasks of this subtree fall in a bucket (0 done, 1 in progress, 2 needs you, 3 not started).</summary>
    internal int Leaf(int bucket) => Counts is null
        ? (Bucket == bucket ? 1 : 0)
        : bucket switch { 0 => Counts.Done, 1 => Counts.InProgress, 2 => Counts.NeedsYou, _ => Counts.NotStarted };
}

/// <summary>A task with its text, comments, history, the stored triage and the last triage job.</summary>
public sealed record TaskDetailView(
    LocalTask Task,
    IReadOnlyList<TaskComment> Comments,
    IReadOnlyList<TaskHistoryEntry> History,
    TriageSummary? Triage,
    JobRecord? TriageJob);

public sealed record RunStatus(bool Busy, int? Pid, string? Owner);

/// <summary>One question on Needs a person. <see cref="Question"/> is the text without the options; <see cref="Reason"/> is the whole triage reason.</summary>
public sealed record NeedsHumanItem(
    string Id, string Title, string Question, bool Stale, bool CanAnswer, string? Answer = null,
    IReadOnlyList<string>? Options = null, int? Recommended = null, string? Reason = null, bool InPool = false);

public static class InboxKinds
{
    public const string Question = "question";
    public const string Parked = "parked";
}

/// <summary>One entry of the Inbox. A question carries <see cref="Question"/>; a parked task carries <see cref="ParkReason"/>.</summary>
public sealed record InboxItem(string Kind, string Id, string Title, DateTimeOffset CreatedAt, string? ParkReason, NeedsHumanItem? Question);

/// <summary>What "Save and continue" did: the task with its answer, whether it is in the pool, and the triage job if one started.</summary>
public sealed record AnswerContinueView(TaskDetailView Task, bool InPool, JobRecord? TriageJob, string? Note);

/// <summary>Tasks: lists, local task changes, starting a run. Every method is one call into the core.</summary>
public sealed class TaskOperations(WebUiOptions options)
{
    public const string AnswerHeading = "## Answer from a person";

    public async Task<TaskListView> ListAsync(ProjectScope scope, CancellationToken ct, TaskQuery? query = null)
    {
        query ??= new TaskQuery();
        if (query.State is { Length: > 0 } state && !TaskQuery.States.Contains(state))
        {
            throw new ArgumentException($"The state must be one of: {string.Join(", ", TaskQuery.States)}.");
        }

        var all = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        var jobs = scope.Jobs.List();
        var active = jobs.Where(j => j.IsActive && j.Kind == JobKinds.Implement).GroupBy(j => j.WorkItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var triageJobs = jobs.Where(j => j.Kind == JobKinds.Triage).GroupBy(j => j.WorkItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var counts = new TaskCounts(
            all.Count,
            all.Count(i => i.Triage is { Label: "needs-human" }),
            all.Count(i => i.Triage is { Label: "agent-ready" }),
            all.Count(i => i.Triage is null),
            all.Count(i => i.Triage is { Stale: true }));

        var matching = all.Where(query.Matches).ToList();
        var pageSize = query.PageSize is { } size ? Math.Clamp(size, 1, TaskQuery.MaxPageSize) : Math.Max(matching.Count, 1);
        var pages = Math.Max(1, (matching.Count + pageSize - 1) / pageSize);
        var page = Math.Clamp(query.Page, 1, pages);
        var items = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var eventCounts = scope.Runs.List().GroupBy(r => r.WorkItemId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new TaskListView(items, scope.LocalTasks is not null, scope.LegacyTaskFiles, active, Status(scope), matching.Count, page, pageSize, counts, triageJobs,
            items.ToDictionary(i => i.Id, i => PipelineSteps.CardOf(i, active.ContainsKey(i.Id), eventCounts.GetValueOrDefault(i.Id)), StringComparer.Ordinal));
    }

    public static RunStatus Status(ProjectScope scope) =>
        scope.Lock.Holder(scope.Project.Key) is { } held ? new RunStatus(true, held.Pid, held.Owner) : new RunStatus(false, null, null);

    public async Task<TaskDetailView> DetailAsync(ProjectScope scope, int number, CancellationToken ct)
    {
        var detail = Local(scope).Show(number) ?? throw new NotFoundException($"There is no task {number}.");
        var triage = await WorkItemListService.SummarizeAsync(scope.Triage, LocalTaskWorkItemSource.ToItem(detail.Task), ct).ConfigureAwait(false);
        var job = scope.Jobs.List().FirstOrDefault(j => j.Kind == JobKinds.Triage && j.WorkItemId == detail.Task.Id);
        return new TaskDetailView(detail.Task, detail.Comments, detail.History, triage, job);
    }

    public LocalTask Add(ProjectScope scope, string title, string? body, IEnumerable<string>? labels) =>
        Local(scope).Add(title, body, labels, Author());

    public void Update(ProjectScope scope, int number, string? title, string? body, IEnumerable<string>? labels)
    {
        var tasks = Local(scope);
        tasks.Update(number, title, body, Author());
        if (labels is not null)
        {
            var current = (tasks.Get(number) ?? throw new NotFoundException($"There is no task {number}.")).Labels;
            var wanted = labels.SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            tasks.ChangeLabels(number, wanted, current.Where(c => !wanted.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public TaskComment Comment(ProjectScope scope, int number, string text) => Local(scope).Comment(number, text, Author());

    public bool SetClosed(ProjectScope scope, int number, bool closed) =>
        closed ? Local(scope).Close(number, Author()) : Local(scope).Reopen(number, Author());

    /// <summary>Moves a task or group under another task, or to the root (null). Cycles and unknown ids are rejected.</summary>
    public LocalTask SetParent(ProjectScope scope, int number, int? parentId) => Local(scope).SetParent(number, parentId);

    /// <summary>The task tree. A group's counts are taken over all its descendant leaf tasks.</summary>
    public async Task<IReadOnlyList<TaskTreeNode>> TreeAsync(ProjectScope scope, CancellationToken ct)
    {
        var roots = Local(scope).Tree();
        var entries = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        var needsYou = entries.Where(i => i.Triage is { Label: "needs-human" }).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var active = scope.Jobs.List().Where(j => j.IsActive && j.Kind == JobKinds.Implement).Select(j => j.WorkItemId).ToHashSet(StringComparer.Ordinal);

        TaskTreeNode Build(TaskNode node)
        {
            var children = node.Children.Select(Build).ToList();
            var t = node.Task;
            if (children.Count == 0)
            {
                var bucket = t.Closed ? 0 : active.Contains(t.Id) ? 1 : needsYou.Contains(t.Id) ? 2 : 3;
                return new TaskTreeNode(t.Number, t.Title, t.ParentNumber, t.Closed, node.Kind, null, []) { Bucket = bucket };
            }

            var counts = new TaskTreeCounts(
                children.Sum(c => c.Leaf(0)), children.Sum(c => c.Leaf(1)), children.Sum(c => c.Leaf(2)), children.Sum(c => c.Leaf(3)));
            return new TaskTreeNode(t.Number, t.Title, t.ParentNumber, t.Closed, node.Kind, counts, children);
        }

        return roots.Select(Build).ToList();
    }

    /// <summary>Starts one implement job in the background. Refused while another run holds the project.</summary>
    public JobRecord Start(ProjectScope scope, string id)
    {
        if (!ProjectPaths.IsValidId(id))
        {
            throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));
        }

        RequireIdle(scope);
        return scope.Jobs.Start(
            id,
            options.Launcher,
            options.Program.File,
            jobId => [.. options.Program.Arguments, "implement", id, "--job", jobId, "--project", scope.Project.Key, "--workspace", scope.Workspace.Root],
            scope.Project.RepositoryFolder);
    }

    /// <summary>
    /// Starts the triage of one item as a background job, so the page can follow it and show the error when it
    /// fails. One triage runs at a time: it uses one worktree. Asking again for the item that is being triaged
    /// returns that job.
    /// </summary>
    public JobRecord StartTriage(ProjectScope scope, string id)
    {
        if (!ProjectPaths.IsValidId(id))
        {
            throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));
        }

        if (scope.Jobs.List().FirstOrDefault(j => j.IsActive && j.Kind == JobKinds.Triage) is { } running)
        {
            return running.WorkItemId == id
                ? running
                : throw new ConflictException($"Task {running.WorkItemId} is being triaged. Wait for it to end.");
        }

        return scope.Jobs.Start(
            id,
            options.Launcher,
            options.Program.File,
            jobId => [.. options.Program.Arguments, "triage", id, "--job", jobId, "--project", scope.Project.Key, "--workspace", scope.Workspace.Root],
            scope.Project.RepositoryFolder,
            JobKinds.Triage);
    }

    public async Task<IReadOnlyList<NeedsHumanItem>> NeedsHumanAsync(ProjectScope scope, CancellationToken ct)
    {
        var items = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        return [.. items
            .Where(i => i.Triage is { Label: "needs-human" })
            .Select(i => Needs(scope, i))];
    }

    /// <summary>Everything that waits for a person: needs-human questions and parked tasks, oldest first.</summary>
    public async Task<IReadOnlyList<InboxItem>> InboxAsync(ProjectScope scope, CancellationToken ct)
    {
        var items = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        var titles = items.ToDictionary(i => i.Id, i => i.Title, StringComparer.Ordinal);
        var store = new PoolStateStore(scope.Workspace, scope.Project.Key);
        store.PruneParked(titles.Keys.ToHashSet(StringComparer.Ordinal));
        var parkedEntries = store.Parked();
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parkedEntries) reasons[p.WorkItemId] = p.Reason;
        var questions = items
            .Where(i => i.Triage is { Label: "needs-human" })
            .Select(i => new InboxItem(InboxKinds.Question, i.Id, i.Title, i.Triage!.TriagedAt, reasons.GetValueOrDefault(i.Id), Needs(scope, i)))
            .ToList();
        var questionIds = questions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var parked = parkedEntries
            .Where(p => !questionIds.Contains(p.WorkItemId))
            .Select(p => new InboxItem(InboxKinds.Parked, p.WorkItemId, titles.GetValueOrDefault(p.WorkItemId, p.WorkItemId), p.ParkedAt, p.Reason, null));
        return [.. questions.Concat(parked).OrderBy(i => i.CreatedAt)];
    }

    private static NeedsHumanItem Needs(ProjectScope scope, WorkItemListEntry i)
    {
        var triage = i.Triage!;
        var q = TriageQuestion.Resolve(triage.Reason, triage.Options, triage.Recommended);
        return new NeedsHumanItem(i.Id, i.Title, q.Text, triage.Stale, scope.LocalTasks is not null, AnswerOf(scope, i.Id), q.Options, q.Recommended, triage.Reason, i.InPool);
    }

    /// <summary>
    /// Saves the answer, puts the task in the pool and starts its triage again. When another triage is running the
    /// answer and the pool entry are still kept and <see cref="AnswerContinueView.Note"/> says to triage it next.
    /// </summary>
    public async Task<AnswerContinueView> AnswerAndContinueAsync(ProjectScope scope, int number, string answer, CancellationToken ct)
    {
        Answer(scope, number, answer);
        var id = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        scope.Pool.Add(id);
        JobRecord? job = null;
        string? note = null;
        try { job = StartTriage(scope, id); }
        catch (ConflictException e) { note = e.Message; }
        return new AnswerContinueView(await DetailAsync(scope, number, ct).ConfigureAwait(false), true, job, note);
    }

    /// <summary>The last answer a person gave on the task (the section <see cref="Answer"/> appends), or null.</summary>
    private static string? AnswerOf(ProjectScope scope, string id)
    {
        if (scope.LocalTasks is not { } tasks || !LocalTaskService.TryParseNumber(id, out var number) || tasks.Get(number) is not { } task)
        {
            return null;
        }

        var at = task.Body.LastIndexOf(AnswerHeading, StringComparison.Ordinal);
        return at < 0 ? null : task.Body[(at + AnswerHeading.Length)..].Trim();
    }

    /// <summary>
    /// Stores a person's answer on the task: as a comment, and as a section of the text so that the next
    /// triage reads it (the changed text also marks the old triage stale).
    /// </summary>
    public void Answer(ProjectScope scope, int number, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Write the answer first.");
        }

        var tasks = Local(scope);
        var task = tasks.Get(number) ?? throw new NotFoundException($"There is no task {number}.");
        var text = answer.Trim();
        tasks.Comment(number, "Answer to the triage question:\n\n" + text, Author());
        tasks.Update(number, null, task.Body.TrimEnd() + "\n\n## Answer from a person\n\n" + text + "\n", Author());
    }

    internal static void RequireIdle(ProjectScope scope)
    {
        if (scope.Lock.Holder(scope.Project.Key) is { } held)
        {
            throw new ConflictException($"A run is already active in this project ({held.Owner}, process {held.Pid}). Wait for it to end.");
        }
    }

    private static LocalTaskService Local(ProjectScope scope) =>
        scope.LocalTasks ?? throw new ConflictException(scope.LegacyTaskFiles
            ? "This project still has task files. Run 'autopilot task import-files' to move them into the workspace."
            : "The tasks of this project live in its tracker; change them there.");

    private static string Author() => LocalTaskService.DefaultAuthor();
}

public sealed record PoolItemView(string Id, string Title, int Position, string? Triage, string? Risk, bool Stale);

public sealed record SchedulePlanView(string ProjectKey, string JobName, string AtTime, int? Limit, string Command, string Scheduler, string Summary, IReadOnlyList<PoolItemView> Items)
{
    public string? Date { get; init; }

    public string Repeat { get; init; } = "daily";

    public string? Until { get; init; }

    public bool Unattended { get; init; }

    public string When { get; init; } = string.Empty;

    /// <summary>The time zone the times are in, shown so nobody guesses.</summary>
    public string TimeZone { get; init; } = string.Empty;

    public int Concurrency { get; init; } = 1;

    public double? StopAtUsagePercent { get; init; }
}

public sealed record ScheduleView(bool Set, bool InstalledInOs, SchedulePlanView? Plan)
{
    public DateTimeOffset? Next { get; init; }

    public DateTimeOffset? LastStartedAt { get; init; }

    public string? Note { get; init; }
}

/// <summary>The pool settings the Pool page edits. Null means "leave as it is".</summary>
public sealed record PoolSettingsBody(bool? Unattended, int? Concurrency, bool? MasterEnabled, bool? IntegrationEnabled, int? MaxAgentRuns, int? StopAtUsagePercent);

public sealed record PoolSettingsView(
    bool Unattended, int Concurrency, int MaxConcurrency, bool MasterEnabled, bool IntegrationEnabled,
    int? MaxAgentRuns, int StopAtUsagePercent, IReadOnlyList<string> AlwaysAsk, int Retries);

public sealed record PoolSlotView(string Id, string Title, string State, string? Detail, DateTimeOffset Since);

public sealed record PoolStatusView(
    PoolSettingsView Settings,
    IReadOnlyList<PoolSlotView> Slots,
    PoolPause? Pause,
    IReadOnlyList<ParkedTask> Parked,
    IReadOnlyList<PoolDecision> Decisions,
    int RevertedDecisions,
    IntegrationInfo Integration,
    bool Busy)
{
    /// <summary>The status of the latest pool run (Planned when there is none).</summary>
    public string Status { get; init; } = nameof(PoolRunStatus.Planned);

    /// <summary>The sub-status of the latest pool run ("None" when it has none).</summary>
    public string SubStatus { get; init; } = nameof(PoolRunSubStatus.None);

    public PoolRunProgressView Progress { get; init; } = new(0, 0, 0);

    /// <summary>The events of the latest pool run, in the order they were appended.</summary>
    public IReadOnlyList<PoolTimelineEntryView> Timeline { get; init; } = [];

    /// <summary>The tasks waiting in the pool, each with one readiness word.</summary>
    public IReadOnlyList<PoolQueueTaskView> Queue { get; init; } = [];

    /// <summary>Agent runs the pool started in the last 24 hours, the number the daily limit counts.</summary>
    public int AgentRunsToday { get; init; }
}

public sealed record PoolRunProgressView(int Done, int Total, int Parked);

public sealed record PoolTimelineEntryView(DateTimeOffset Time, string Event, string? Detail)
{
    /// <summary>The task the event is about, when it is about one.</summary>
    public string? TaskId { get; init; }
}

/// <summary>Readiness is one of: ready, needs an answer, not triaged, waits for &lt;id&gt;, parked.</summary>
public sealed record PoolQueueTaskView(string Id, string Title, string Readiness);

/// <summary>The pool and its runs: order, remove, run now, run at night.</summary>
public sealed class PoolOperations(WebUiOptions options)
{
    /// <param name="fresh">Drops the cached tracker list first, so the queue shows what the tracker holds now.</param>
    public async Task<IReadOnlyList<PoolItemView>> ListAsync(ProjectScope scope, CancellationToken ct, bool fresh = false)
    {
        if (fresh)
        {
            options.ListCache.Invalidate(scope.Project.Key);
        }

        var entries = scope.Pool.List();
        if (entries.Count == 0)
        {
            return [];
        }

        var titles = (await scope.CreateList().ListAsync(ct).ConfigureAwait(false)).ToDictionary(e => e.Id, StringComparer.Ordinal);
        return [.. entries.Select(e => titles.TryGetValue(e.WorkItemId, out var item)
            ? new PoolItemView(e.WorkItemId, item.Title, e.Position, item.Triage?.Label, item.Triage?.Risk, item.Triage?.Stale ?? false)
            : new PoolItemView(e.WorkItemId, "(not open any more)", e.Position, null, null, false))];
    }

    public bool Add(ProjectScope scope, string id) =>
        ProjectPaths.IsValidId(id) ? scope.Pool.Add(id) : throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));

    public bool Remove(ProjectScope scope, string id) => scope.Pool.Remove(id);

    public bool Move(ProjectScope scope, string id, int position) => scope.Pool.Move(id, position);

    /// <summary>Starts <c>pool run</c> in the background. The run takes the project lock itself; this only checks it is free.</summary>
    public int RunNow(ProjectScope scope, int? limit)
    {
        if (limit is <= 0)
        {
            throw new ArgumentException("The limit must be greater than 0.");
        }

        if (scope.Pool.List().Count == 0)
        {
            throw new ConflictException("The pool is empty. Add tasks to it first.");
        }

        TaskOperations.RequireIdle(scope);
        List<string> args = [.. options.Program.Arguments, "pool", "run", "--project", scope.Project.Key, "--workspace", scope.Workspace.Root];
        if (limit is { } n)
        {
            args.AddRange(["--limit", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return options.Launcher.Launch(options.Program.File, args, scope.Project.RepositoryFolder);
    }

    public async Task<SchedulePlanView> PlanAsync(ProjectScope scope, ScheduleBody body, CancellationToken ct)
    {
        var plan = PlanOf(scope, body);
        var pool = ReadPool(scope);
        var summary = plan.ToString() + Environment.NewLine
            + $"  At the start the pool takes up to {plan.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"} task(s), {pool.Concurrency} agent(s) at a time"
            + (plan.Unattended ? ", without a person" : string.Empty)
            + $", and stops at {pool.Budget.StopAtUsagePercent}% of the usage limit when that is known.";
        return View(plan, summary, await ListAsync(scope, ct).ConfigureAwait(false), pool);
    }

    /// <summary>Installs the run. The caller must say it confirmed the plan it was shown. Returns a note when the catch-up job could not be installed.</summary>
    public async Task<string?> SetAsync(ProjectScope scope, ScheduleBody body, CancellationToken ct)
    {
        if (!body.Confirmed)
        {
            throw new ArgumentException("Confirm the plan first.");
        }

        return await scope.Schedule.Create().SetAsync(PlanOf(scope, body), ct).ConfigureAwait(false);
    }

    public async Task<ScheduleView> ShowAsync(ProjectScope scope, CancellationToken ct, string? note = null)
    {
        var state = await scope.Schedule.Create().ShowAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return new ScheduleView(false, false, null);
        }

        var p = state.Plan;
        return new ScheduleView(true, state.InstalledInOs, View(p, p.ToString(), [], ReadPool(scope))) { Next = state.Next, LastStartedAt = state.LastStartedAt, Note = note };
    }

    public Task<bool> RemoveScheduleAsync(ProjectScope scope, CancellationToken ct) => scope.Schedule.Create().RemoveAsync(ct);

    private static SchedulePlanView View(SchedulePlan p, string summary, IReadOnlyList<PoolItemView> items, Autopilot.Core.Config.PoolConfig pool) =>
        new(p.ProjectKey, p.JobName, p.AtTime, p.Limit, p.Command, p.SchedulerDescription, summary, items)
        {
            Date = p.Date,
            Repeat = p.Repeat,
            Until = p.Until,
            Unattended = p.Unattended,
            When = p.When,
            TimeZone = TimeZoneInfo.Local.Id + " (UTC" + (TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow) is var o && o >= TimeSpan.Zero ? "+" : "-") + o.ToString(@"hh\:mm") + ")",
            Concurrency = pool.Concurrency,
            StopAtUsagePercent = pool.Budget.StopAtUsagePercent,
        };

    private static SchedulePlan PlanOf(ProjectScope scope, ScheduleBody body)
    {
        var service = scope.Schedule.Create();
        var unattended = body.Unattended ?? ReadPool(scope).Unattended;
        var at = body.At ?? string.Empty;
        return at.Contains(' ', StringComparison.Ordinal) || body.Repeat is not null || body.Until is not null || unattended
            ? service.Plan(new ScheduleRequest(at, body.Repeat, body.Until, body.Limit, unattended), scope.ProgramCommandLine())
            : service.Plan(at, body.Limit, scope.ProgramCommandLine());
    }

    // ---------- unattended pool: settings, decisions, parked tasks, integration ----------

    private static Autopilot.Core.Config.PoolConfig ReadPool(ProjectScope scope) => scope.Settings.Read().Config.Pool;

    public async Task<PoolStatusView> StatusAsync(ProjectScope scope, CancellationToken ct)
    {
        var view = scope.Settings.Read();
        var pool = view.Config.Pool;
        var state = new PoolStateStore(scope.Workspace, scope.Project.Key);

        // A run that was killed leaves its slots 'working': a run with no sign of life for a few minutes is dead.
        state.ReleaseDead(DateTimeOffset.UtcNow, PoolStateStore.DefaultStaleAfter, scope.Options.IsProcessAlive ?? ProjectRunLock.IsAlive, Environment.ProcessId);
        var entries = (await scope.CreateList().ListAsync(ct).ConfigureAwait(false)).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var titles = entries.ToDictionary(e => e.Key, e => e.Value.Title, StringComparer.Ordinal);
        var integration = Integration(scope, view.Config).Info();

        var events = new PoolRunEventStore(scope.Workspace);
        var runId = events.LatestRunId();
        var runEvents = runId is null ? [] : events.Read(runId);
        var snapshot = PoolRunStatusFolder.Fold(runEvents);

        var parked = state.Parked();
        var parkedIds = parked.Select(p => p.WorkItemId).ToHashSet(StringComparer.Ordinal);
        var inPool = scope.Pool.List().Select(e => e.WorkItemId).ToList();
        var inPoolSet = inPool.ToHashSet(StringComparer.Ordinal);
        var queue = inPool.Concat(parkedIds.Where(id => !inPoolSet.Contains(id))).Select(id =>
        {
            entries.TryGetValue(id, out var entry);
            return new PoolQueueTaskView(id, entry?.Title ?? id, Readiness(id, entry, parkedIds, inPoolSet));
        }).ToList();

        var slotList = state.Slots();
        var currentPause = state.CurrentPause();
        var personPause = PersonPauseStatus(currentPause, slotList);
        return new PoolStatusView(
            new PoolSettingsView(pool.Unattended, pool.Concurrency, Autopilot.Core.Config.PoolConfig.MaxConcurrency, pool.Master.Enabled, pool.Integration.Enabled,
                pool.Budget.MaxAgentRuns, pool.Budget.StopAtUsagePercent, pool.AlwaysAsk, pool.OnFailure.Retries),
            [.. slotList.Select(s => new PoolSlotView(s.WorkItemId, titles.GetValueOrDefault(s.WorkItemId, s.WorkItemId), s.State, s.Detail, s.Since))],
            currentPause,
            parked,
            state.Decisions(50),
            state.RevertedCount(),
            integration,
            scope.Lock.Holder(scope.Project.Key) is not null)
        {
            Status = personPause?.Status ?? snapshot.State.Status.ToString(),
            SubStatus = personPause?.SubStatus ?? snapshot.State.Sub.ToString(),
            Progress = new PoolRunProgressView(snapshot.Progress.Done, snapshot.Progress.Total, snapshot.Progress.Parked),
            Timeline = [.. runEvents.Select(e => new PoolTimelineEntryView(e.At, e.Event.ToString(), e.Detail) { TaskId = e.TaskId })],
            Queue = queue,
            AgentRunsToday = state.AgentRunsInLastDay(DateTimeOffset.UtcNow),
        };
    }

    private static string Readiness(string id, WorkItemListEntry? entry, IReadOnlySet<string> parked, IReadOnlySet<string> inPool)
    {
        if (parked.Contains(id))
        {
            return "parked";
        }

        if (entry?.Triage is not { } triage)
        {
            return "not triaged";
        }

        if (string.Equals(triage.Label, "needs-human", StringComparison.Ordinal))
        {
            return "needs an answer";
        }

        var waitsFor = PoolTaskHints.DependenciesFrom(entry.Labels ?? []).FirstOrDefault(d => d != id && inPool.Contains(d));
        return waitsFor is null ? "ready" : "waits for " + waitsFor;
    }

    public PoolSettingsView SaveSettings(ProjectScope scope, PoolSettingsBody body)
    {
        var pool = new System.Text.Json.Nodes.JsonObject();
        if (body.Unattended is { } u) { pool["unattended"] = u; }
        if (body.Concurrency is { } c) { pool["concurrency"] = c; }
        if (body.MasterEnabled is { } m) { pool["master"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = m }; }
        if (body.IntegrationEnabled is { } i) { pool["integration"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = i }; }
        var budget = new System.Text.Json.Nodes.JsonObject();
        // 0 means "no limit": the key is stored as null, which is how the file says it.
        if (body.MaxAgentRuns is { } r) { budget["maxAgentRuns"] = r == 0 ? null : r; }
        if (body.StopAtUsagePercent is { } s) { budget["stopAtUsagePercent"] = s; }
        if (budget.Count > 0) { pool["budget"] = budget; }
        var saved = pool.Count == 0 ? scope.Settings.Read() : scope.Settings.Save(new System.Text.Json.Nodes.JsonObject { ["pool"] = pool });
        var p = saved.Config.Pool;
        return new PoolSettingsView(p.Unattended, p.Concurrency, Autopilot.Core.Config.PoolConfig.MaxConcurrency, p.Master.Enabled, p.Integration.Enabled,
            p.Budget.MaxAgentRuns, p.Budget.StopAtUsagePercent, p.AlwaysAsk, p.OnFailure.Retries);
    }

    /// <summary>Undoes a decision of the pool master: its answer leaves the task, the task goes back in the pool.</summary>
    public bool RevertDecision(ProjectScope scope, long id)
    {
        var state = new PoolStateStore(scope.Workspace, scope.Project.Key);
        var decision = state.Decision(id);
        if (decision is null || decision.RevertedAt is not null)
        {
            return false;
        }

        if (decision.Kind == DecisionKinds.Answer && scope.LocalTasks is { } tasks && LocalTaskService.TryParseNumber(decision.WorkItemId, out var number) && tasks.Get(number) is { } task)
        {
            tasks.Update(number, null, MasterGuard.RetractAnswers(task.Body), LocalTaskService.DefaultAuthor());
        }

        state.Revert(id, DateTimeOffset.UtcNow);
        state.Unpark(decision.WorkItemId);
        scope.Pool.Add(decision.WorkItemId);
        return true;
    }

    public bool Unpark(ProjectScope scope, string id)
    {
        var removed = new PoolStateStore(scope.Workspace, scope.Project.Key).Unpark(id);
        if (removed)
        {
            scope.Pool.Add(id);
        }

        return removed;
    }

    /// <summary>Pauses the pool at a person's request. Idempotent; returns the same status as the other pool endpoints.</summary>
    public Task<PoolStatusView> PauseAsync(ProjectScope scope, CancellationToken ct)
    {
        new PoolStateStore(scope.Workspace, scope.Project.Key).PauseByPerson(DateTimeOffset.UtcNow);
        return StatusAsync(scope, ct);
    }

    /// <summary>
    /// The status and sub-status a person pause shows: "Paused" with "finishing current tasks" while a task still
    /// runs, and no sub-status once none does. Null when the pause is not a person pause.
    /// </summary>
    public static (string Status, string SubStatus)? PersonPauseStatus(PoolPause? pause, IEnumerable<PoolSlot> slots) =>
        pause is not null && PoolStateStore.IsPersonPause(pause)
            ? ("Paused", slots.Any(s => s.State == "running") ? PoolStateStore.FinishingCurrentTasks : nameof(PoolRunSubStatus.None))
            : null;

    public bool Resume(ProjectScope scope) => new PoolStateStore(scope.Workspace, scope.Project.Key).Resume();

    /// <summary>Pushes the integration branch and opens the final PR as a draft. A person marks it ready and merges it.</summary>
    public async Task<string> OpenFinalPrAsync(ProjectScope scope, CancellationToken ct)
    {
        try
        {
            return await Integration(scope, scope.Settings.Read().Config).OpenFinalPrAsync(null, ct).ConfigureAwait(false);
        }
        catch (Autopilot.Core.Git.GitException ex)
        {
            throw new ConflictException(ex.Message);
        }
    }

    private static IntegrationService Integration(ProjectScope scope, Autopilot.Core.Config.AutopilotConfig config)
    {
        var repo = scope.Project.RepositoryFolder;
        return new IntegrationService(scope.Workspace, scope.Project.Key, new Autopilot.Core.Git.GitClient(repo, config.Identity is { } id ? new Autopilot.Core.Git.GitIdentity(id.Name, id.Email) : null),
            config, Autopilot.Core.Git.GitClient.DefaultWorktreeRoot(repo));
    }
}

public sealed record RunOverview(
    IReadOnlyList<RunRecord> Runs,
    RunStatsView Stats,
    IReadOnlyList<JobView> Jobs,
    RunStatus Status);

public sealed record RunStatsView(int TotalRuns, int GatePassed, int GateRejected, int Decided, int Agreed, int GateTooLenient, int GateTooStrict, double? AgreementRate);

public sealed record JobView(JobRecord Job, string? LogTail);

public sealed record RunDetail(RunRecord Run, JsonElement? Gate, string? Report, string? Diff, bool DiffTruncated, string? GateLog, string? Base = null);

/// <summary>Run history, one run's gate report and diff, jobs and the Stop button.</summary>
public sealed class RunOperations(WebUiOptions options)
{
    public const int MaxDiffChars = 400_000;
    public const int LogLines = 60;

    public async Task<RunOverview> OverviewAsync(ProjectScope scope, CancellationToken ct)
    {
        var s = scope.Runs.Stats();
        var jobs = new List<JobView>();
        foreach (var job in scope.Jobs.List().Take(15))
        {
            jobs.Add(new JobView(job, job.IsActive ? await TailAsync(scope, job.Id, ct).ConfigureAwait(false) : null));
        }

        return new RunOverview(
            scope.Runs.List(null, 100),
            new RunStatsView(s.TotalRuns, s.GatePassed, s.GateRejected, s.Decided, s.Agreed, s.GateTooLenient, s.GateTooStrict, s.AgreementRate),
            jobs,
            TaskOperations.Status(scope));
    }

    public async Task<JobView> JobAsync(ProjectScope scope, string id, CancellationToken ct)
    {
        var job = scope.Jobs.Get(id) ?? throw new NotFoundException($"There is no job \"{id}\".");
        return new JobView(job, await TailAsync(scope, id, ct).ConfigureAwait(false));
    }

    /// <summary>Stops this one job and the processes it started. Other jobs and the pool run are not touched.</summary>
    public bool Stop(ProjectScope scope, string id)
    {
        _ = scope.Jobs.Get(id) ?? throw new NotFoundException($"There is no job \"{id}\".");
        return scope.Jobs.Cancel(id, options.Killer, scope.Lock);
    }

    public async Task<RunDetail> DetailAsync(ProjectScope scope, long id, CancellationToken ct)
    {
        var run = scope.Runs.Get(id) ?? throw new NotFoundException($"There is no run {id}.");
        if (!ProjectPaths.IsValidId(run.WorkItemId))
        {
            return new RunDetail(run, null, null, null, false, null);
        }

        var folder = ProjectPaths.RunFolder(scope.Project.RepositoryFolder, run.WorkItemId, scope.DataFolder);
        JsonElement? gate = null;
        if (await ReadAsync(Path.Combine(folder, GateReport.FileName), ct).ConfigureAwait(false) is { } json)
        {
            try
            {
                gate = JsonDocument.Parse(json).RootElement.Clone();
            }
            catch (JsonException)
            {
                gate = null;
            }
        }

        var report = await ReadAsync(Path.Combine(folder, "PULL_REQUEST.md"), ct).ConfigureAwait(false)
            ?? await ReadAsync(Path.Combine(folder, "REJECTED.md"), ct).ConfigureAwait(false);
        var diff = await ReadAsync(Path.Combine(folder, "change.patch"), ct).ConfigureAwait(false);
        var truncated = diff is not null && diff.Length > MaxDiffChars;
        var gateLog = await ReadAsync(Path.Combine(folder, "gate-log.txt"), ct).ConfigureAwait(false);
        var baseText = (await ReadAsync(Path.Combine(folder, Autopilot.Core.Pipeline.AutopilotPipeline.BaseFileName), ct).ConfigureAwait(false))?.Trim();
        return new RunDetail(run, gate, report, truncated ? diff![..MaxDiffChars] : diff, truncated, gateLog is { Length: > 20_000 } ? gateLog[^20_000..] : gateLog, baseText is { Length: > 0 } ? baseText : null);
    }

    public bool Decide(ProjectScope scope, long id, string decision) =>
        Enum.TryParse<HumanDecision>(decision, ignoreCase: true, out var parsed)
            ? scope.Runs.RecordDecision(id, parsed)
            : throw new ArgumentException("The decision must be \"merged\" or \"rejected\".");

    private static async Task<string?> TailAsync(ProjectScope scope, string id, CancellationToken ct)
    {
        try
        {
            return await scope.Jobs.ReadLogAsync(id, LogLines, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
}
