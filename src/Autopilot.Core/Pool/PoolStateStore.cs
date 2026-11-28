using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Pool;

public static class DecisionKinds
{
    public const string Answer = "answer";
    public const string Failure = "failure";
}

/// <summary>One thing the pool master decided: the task, the question, the answer and why.</summary>
public sealed record PoolDecision(
    long Id,
    string WorkItemId,
    string Kind,
    string Question,
    string Answer,
    string Reason,
    string? Category,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevertedAt);

/// <summary>A task the pool left for a person.</summary>
public sealed record ParkedTask(string WorkItemId, string? Category, string Reason, DateTimeOffset ParkedAt);

/// <summary>A task the running pool works on ("running") or holds back ("waiting", with the reason).</summary>
public sealed record PoolSlot(string WorkItemId, string State, string? Detail, DateTimeOffset Since);

public sealed record PoolPause(string Reason, DateTimeOffset? RetryAt, DateTimeOffset CreatedAt);

/// <summary>
/// What the unattended pool remembers in the workspace database: the master's decisions, parked tasks,
/// who is running or waiting, the pause, and the agent runs counted against the budget.
/// </summary>
public sealed class PoolStateStore(Workspace.Workspace workspace, string projectKey)
{
    // ---------- decisions ----------

    public PoolDecision AddDecision(string workItemId, string kind, string question, string answer, string reason, string? category, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO pool_decisions (project_key, work_item_id, kind, question, answer, reason, category, created_at) VALUES ($k, $i, $kind, $q, $a, $r, $c, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$kind", kind), ("$q", question), ("$a", answer), ("$r", reason), ("$c", category), ("$t", Db.Stamp(at)));
        using var id = Db.Command(connection, "SELECT last_insert_rowid()");
        return new PoolDecision(Convert.ToInt64(id.ExecuteScalar()), workItemId, kind, question, answer, reason, category, at, null);
    }

    public IReadOnlyList<PoolDecision> Decisions(int? limit = null)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, ReadDecision,
            "SELECT id, work_item_id, kind, question, answer, reason, category, created_at, reverted_at FROM pool_decisions WHERE project_key = $k ORDER BY id DESC" + (limit is { } n ? $" LIMIT {n}" : string.Empty),
            ("$k", projectKey));
    }

    public PoolDecision? Decision(long id)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, ReadDecision,
            "SELECT id, work_item_id, kind, question, answer, reason, category, created_at, reverted_at FROM pool_decisions WHERE project_key = $k AND id = $id",
            ("$k", projectKey), ("$id", id)).FirstOrDefault();
    }

    /// <summary>Marks a decision as undone by a person. False when it does not exist or was already undone.</summary>
    public bool Revert(long id, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "UPDATE pool_decisions SET reverted_at = $t WHERE project_key = $k AND id = $id AND reverted_at IS NULL",
            ("$t", Db.Stamp(at)), ("$k", projectKey), ("$id", id)) > 0;
    }

    /// <summary>How many times a person undid a decision of the master. It feeds the trust levels.</summary>
    public int RevertedCount()
    {
        using var connection = workspace.Open();
        using var command = Db.Command(connection, "SELECT COUNT(*) FROM pool_decisions WHERE project_key = $k AND reverted_at IS NOT NULL", ("$k", projectKey));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static PoolDecision ReadDecision(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Db.StringOrNull(r, 6), Db.ParseStamp(r.GetString(7)), Db.ParseStampOrNull(r, 8));

    // ---------- pool-level overrides of the project's settings ----------

    /// <summary>The pool-level override of this project, or null when it has no row.</summary>
    public PoolOverride? Override()
    {
        using var connection = workspace.Open();
        return Db.Query(connection,
            r => new PoolOverride(Bool(r, 0), Int(r, 1), Bool(r, 2), Int(r, 3), Bool(r, 4)),
            "SELECT unattended, concurrency, master_enabled, budget_stop_percent, integration_enabled FROM pool_overrides WHERE project_key = $k",
            ("$k", projectKey)).FirstOrDefault();
    }

    /// <summary>Stores the override. An override with every field null removes the row.</summary>
    public void SetOverride(PoolOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsEmpty)
        {
            ClearOverride();
            return;
        }

        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO pool_overrides (project_key, unattended, concurrency, master_enabled, budget_stop_percent, integration_enabled) VALUES ($k, $u, $c, $m, $b, $i) "
            + "ON CONFLICT(project_key) DO UPDATE SET unattended = $u, concurrency = $c, master_enabled = $m, budget_stop_percent = $b, integration_enabled = $i",
            ("$k", projectKey), ("$u", value.Unattended), ("$c", value.Concurrency), ("$m", value.MasterEnabled), ("$b", value.BudgetStopPercent), ("$i", value.IntegrationEnabled));
    }

    public void ClearOverride()
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_overrides WHERE project_key = $k", ("$k", projectKey));
    }

    private static bool? Bool(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i) != 0;

    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    // ---------- notes for the next attempt ----------

    /// <summary>Keeps a note for the next attempt at a task whose text lives in a tracker.</summary>
    public void AddNote(string workItemId, string note, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT INTO pool_task_notes (project_key, work_item_id, note, created_at) VALUES ($k, $i, $n, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$n", note), ("$t", Db.Stamp(at)));
    }

    /// <summary>The notes kept for a task, oldest first.</summary>
    public IReadOnlyList<string> Notes(string workItemId)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => r.GetString(0),
            "SELECT note FROM pool_task_notes WHERE project_key = $k AND work_item_id = $i ORDER BY id", ("$k", projectKey), ("$i", workItemId));
    }

    /// <summary>Forgets the notes of a task, for example once its draft is published.</summary>
    public void ClearNotes(string workItemId)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_task_notes WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
    }

    // ---------- parked ----------

    public void Park(string workItemId, string? category, string reason, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_parked WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
        Db.Execute(connection, "INSERT INTO pool_parked (project_key, work_item_id, category, reason, parked_at) VALUES ($k, $i, $c, $r, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$c", category), ("$r", reason), ("$t", Db.Stamp(at)));
    }

    public bool Unpark(string workItemId)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_parked WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId)) > 0;
    }

    public IReadOnlyList<ParkedTask> Parked()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new ParkedTask(r.GetString(0), Db.StringOrNull(r, 1), r.GetString(2), Db.ParseStamp(r.GetString(3))),
            "SELECT work_item_id, category, reason, parked_at FROM pool_parked WHERE project_key = $k ORDER BY parked_at", ("$k", projectKey));
    }

    /// <summary>
    /// Deletes parked entries of tasks that are not open any more (closed, done, removed, or from a pool that is
    /// gone). Returns the ids that were removed.
    /// </summary>
    public IReadOnlyList<string> PruneParked(IReadOnlySet<string> openIds)
    {
        var removed = new List<string>();
        foreach (var parked in Parked().Where(p => !openIds.Contains(p.WorkItemId)))
        {
            if (Unpark(parked.WorkItemId))
            {
                removed.Add(parked.WorkItemId);
            }
        }

        return removed;
    }

    // ---------- slots ----------

    public void SetSlot(string workItemId, string state, string? detail, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR REPLACE INTO pool_status (project_key, work_item_id, state, detail, since) VALUES ($k, $i, $s, $d, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$s", state), ("$d", detail), ("$t", Db.Stamp(at)));
    }

    public void ClearSlot(string workItemId)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_status WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
    }

    public void ClearSlots()
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_status WHERE project_key = $k", ("$k", projectKey));
    }

    public IReadOnlyList<PoolSlot> Slots()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new PoolSlot(r.GetString(0), r.GetString(1), Db.StringOrNull(r, 2), Db.ParseStamp(r.GetString(3))),
            "SELECT work_item_id, state, detail, since FROM pool_status WHERE project_key = $k ORDER BY since", ("$k", projectKey));
    }

    // ---------- heartbeat ----------

    /// <summary>How long a run may stay silent before its slots and locks count as dead.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>Records that the pool run of process <paramref name="pid"/> is alive.</summary>
    public void Beat(int pid, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR REPLACE INTO pool_heartbeats (project_key, pid, beat_at) VALUES ($k, $p, $t)",
            ("$k", projectKey), ("$p", pid), ("$t", Db.Stamp(at)));
    }

    /// <summary>The runs of this project that beat, with the time of their last beat.</summary>
    public IReadOnlyList<(int Pid, DateTimeOffset At)> Beats()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => (Pid: r.GetInt32(0), At: Db.ParseStamp(r.GetString(1))),
            "SELECT pid, beat_at FROM pool_heartbeats WHERE project_key = $k", ("$k", projectKey));
    }

    /// <summary>Removes the heartbeat of a run that ended on its own.</summary>
    public void EndBeat(int pid)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_heartbeats WHERE project_key = $k AND pid = $p", ("$k", projectKey), ("$p", pid));
    }

    /// <summary>True when the owner <paramref name="pid"/> beat before but not within <paramref name="staleAfter"/>. False when it never beat (an older build, or not a pool run).</summary>
    public static bool IsSilent(Microsoft.Data.Sqlite.SqliteConnection connection, string projectKey, int pid, DateTimeOffset now, TimeSpan staleAfter)
    {
        var beats = Db.Query(connection, r => r.GetString(0), "SELECT beat_at FROM pool_heartbeats WHERE project_key = $k AND pid = $p", ("$k", projectKey), ("$p", pid));
        return beats.Count > 0 && now - Db.ParseStamp(beats[0]) > staleAfter;
    }

    /// <summary>
    /// Finds out whether the pool run that owned this project's slots is dead: no run beat within
    /// <paramref name="staleAfter"/> (or its process is gone). If so, running slots become "interrupted" (the
    /// task stays in the pool), waiting slots go away, and the dead run's task locks and project lock are
    /// released. Returns the ids of the tasks whose run was interrupted. Does nothing while a live run beats.
    /// </summary>
    /// <param name="ownPid">The calling process: it never counts as the dead owner.</param>
    public IReadOnlyList<string> ReleaseDead(DateTimeOffset now, TimeSpan staleAfter, Func<int, bool> isAlive, int ownPid)
    {
        using var connection = workspace.Open();
        var beats = Db.Query(connection, r => (Pid: r.GetInt32(0), At: Db.ParseStamp(r.GetString(1))),
            "SELECT pid, beat_at FROM pool_heartbeats WHERE project_key = $k", ("$k", projectKey));
        bool Dead((int Pid, DateTimeOffset At) b) => b.Pid != ownPid && (now - b.At > staleAfter || !isAlive(b.Pid));
        if (beats.Any(b => b.Pid == ownPid || !Dead(b)))
        {
            return [];
        }

        var holder = Db.Query(connection, r => r.GetInt32(0), "SELECT pid FROM project_locks WHERE project_key = $k", ("$k", projectKey));
        if (beats.Count == 0 && holder.Count > 0 && holder[0] != ownPid && isAlive(holder[0]))
        {
            return []; // someone holds the project without beating (an older build, a job): leave it alone
        }

        var deadPids = beats.Select(b => b.Pid).ToHashSet();
        var lastBeat = beats.Count > 0 ? beats.Max(b => b.At) : (DateTimeOffset?)null;
        var slots = Db.Query(connection, r => (Id: r.GetString(0), State: r.GetString(1), Since: Db.ParseStamp(r.GetString(2))),
            "SELECT work_item_id, state, since FROM pool_status WHERE project_key = $k", ("$k", projectKey));
        var interrupted = new List<string>();
        foreach (var slot in slots.Where(s => s.State == "running"))
        {
            var seen = lastBeat ?? slot.Since;
            Db.Execute(connection, "UPDATE pool_status SET state = 'interrupted', detail = $d WHERE project_key = $k AND work_item_id = $i",
                ("$d", $"The run was interrupted: no sign of life since {seen.ToLocalTime():HH:mm}. The task is still in the pool and starts again with the next run."),
                ("$k", projectKey), ("$i", slot.Id));
            interrupted.Add(slot.Id);
        }

        Db.Execute(connection, "DELETE FROM pool_status WHERE project_key = $k AND state = 'waiting'", ("$k", projectKey));
        Db.Execute(connection, "DELETE FROM pool_heartbeats WHERE project_key = $k AND pid <> $p", ("$k", projectKey), ("$p", ownPid));
        var locks = Db.Query(connection, r => (Id: r.GetString(0), Pid: r.GetInt32(1)),
            "SELECT work_item_id, pid FROM task_locks WHERE project_key = $k", ("$k", projectKey));
        foreach (var held in locks.Where(l => l.Pid != ownPid && (deadPids.Contains(l.Pid) || !isAlive(l.Pid))))
        {
            Db.Execute(connection, "DELETE FROM task_locks WHERE project_key = $k AND work_item_id = $i AND pid = $p", ("$k", projectKey), ("$i", held.Id), ("$p", held.Pid));
        }

        if (holder.Count > 0 && holder[0] != ownPid && (deadPids.Contains(holder[0]) || !isAlive(holder[0])))
        {
            Db.Execute(connection, "DELETE FROM project_locks WHERE project_key = $k AND pid = $p", ("$k", projectKey), ("$p", holder[0]));
        }

        return interrupted;
    }

    // ---------- pause ----------

    /// <summary>The reason of a pause a person asked for. Such a pause has no retry time and never expires.</summary>
    public const string PersonPauseReason = "Paused by a person";

    /// <summary>The sub-status shown while a person pause waits for the tasks that already started.</summary>
    public const string FinishingCurrentTasks = "finishing current tasks";

    /// <summary>
    /// Records a pause a person asked for. Does nothing when the pool is already paused: a budget or infrastructure
    /// pause keeps its retry time, a person pause stays as it is. Returns true when the pause was newly set.
    /// </summary>
    public bool PauseByPerson(DateTimeOffset at)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "INSERT OR IGNORE INTO pool_pause (project_key, reason, retry_at, created_at) VALUES ($k, $r, NULL, $t)",
            ("$k", projectKey), ("$r", PersonPauseReason), ("$t", Db.Stamp(at))) > 0;
    }

    public void Pause(string reason, DateTimeOffset? retryAt, DateTimeOffset at)
    {
        // A pause a person asked for is only lifted by a person.
        if (CurrentPause() is { } existing && IsPersonPause(existing))
        {
            return;
        }

        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR REPLACE INTO pool_pause (project_key, reason, retry_at, created_at) VALUES ($k, $r, $a, $t)",
            ("$k", projectKey), ("$r", reason), ("$a", retryAt is { } r ? Db.Stamp(r) : null), ("$t", Db.Stamp(at)));
    }

    public static bool IsPersonPause(PoolPause pause) =>
        pause.RetryAt is null && string.Equals(pause.Reason, PersonPauseReason, StringComparison.Ordinal);

    public PoolPause? CurrentPause()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new PoolPause(r.GetString(0), Db.ParseStampOrNull(r, 1), Db.ParseStamp(r.GetString(2))),
            "SELECT reason, retry_at, created_at FROM pool_pause WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
    }

    public bool Resume()
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_pause WHERE project_key = $k", ("$k", projectKey)) > 0;
    }

    // ---------- agent runs (budget) ----------

    public void CountAgentRun(string? workItemId, string kind, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT INTO pool_agent_runs (project_key, work_item_id, kind, started_at) VALUES ($k, $i, $kind, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$kind", kind), ("$t", Db.Stamp(at)));
    }

    /// <summary>
    /// When the budget of <paramref name="max"/> agent runs in 24 hours has room again: the moment the oldest run
    /// that still counts leaves the window. Null while there is room now.
    /// </summary>
    public DateTimeOffset? BudgetResetsAt(DateTimeOffset now, int max)
    {
        using var connection = workspace.Open();
        var starts = Db.Query(connection, r => Db.ParseStamp(r.GetString(0)), "SELECT started_at FROM pool_agent_runs WHERE project_key = $k", ("$k", projectKey))
            .Where(s => s > now.AddHours(-24)).Order().ToList();
        return starts.Count >= max && max > 0 ? starts[starts.Count - max].AddHours(24) : null;
    }

    /// <summary>Agent runs started in the 24 hours before <paramref name="now"/>.</summary>
    public int AgentRunsInLastDay(DateTimeOffset now)
    {
        using var connection = workspace.Open();
        var rows = Db.Query(connection, r => r.GetString(0), "SELECT started_at FROM pool_agent_runs WHERE project_key = $k", ("$k", projectKey));
        return rows.Count(s => Db.ParseStamp(s) > now.AddHours(-24));
    }
}
