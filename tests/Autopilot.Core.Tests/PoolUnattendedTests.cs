using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

/// <summary>A clock a test moves by hand. Local time is UTC so dates are easy to read.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The pool master as a script: the tests say what it answers.</summary>
internal sealed class FakeMaster : IPoolMaster
{
    public Func<MasterQuestion, MasterAnswer> Answer { get; set; } = _ => new MasterAnswer(false, null, string.Empty, "no script", PoolCategories.Other, false);

    public Func<FailureInfo, FailureVerdict> Classify { get; set; } = _ => new FailureVerdict(FailureKind.Task, string.Empty, "task failure", null);

    public List<string> Asked { get; } = [];

    public Task<MasterAnswer> DecideAsync(MasterQuestion question, CancellationToken ct)
    {
        lock (Asked) { Asked.Add(question.WorkItemId); }
        return Task.FromResult(Answer(question));
    }

    public Task<FailureVerdict> ClassifyFailureAsync(FailureInfo failure, CancellationToken ct) => Task.FromResult(Classify(failure));
}

/// <summary>The tasks and the repository as a script.</summary>
internal sealed class FakeTasks : IPoolTasks
{
    public Dictionary<string, PoolTaskInfo> Infos { get; } = [];

    public Dictionary<string, string> Questions { get; } = [];

    /// <summary>Tasks without a stored triage.</summary>
    public HashSet<string> Untriaged { get; } = [];

    /// <summary>What the triage agent finds when the pool triages a task.</summary>
    public Func<string, PoolTriage> Retriage { get; set; } = _ => new PoolTriage(PoolTriageState.AgentReady, "Clear.");

    public List<string> Triaged { get; } = [];

    public Func<ImplementResult, IntegrationResult?> Integrate { get; set; } = _ => null;

    public List<(string Id, string Text)> Answers { get; } = [];

    public List<string> Retried { get; } = [];

    public List<string> Bugs { get; } = [];

    /// <summary>What was left over from a dead run, by task id; the task ids the pool asked about are in <see cref="LeftoversAsked"/>.</summary>
    public Dictionary<string, string> Leftovers { get; } = [];

    public List<string> LeftoversAsked { get; } = [];

    /// <summary>An error the source throws when the pool touches this task (a locked worktree, for example).</summary>
    public Dictionary<string, Exception> Faults { get; } = [];

    public Task<string?> ReleaseLeftoversAsync(string id, CancellationToken ct)
    {
        if (Faults.TryGetValue(id, out var fault))
        {
            throw fault;
        }


        lock (LeftoversAsked) { LeftoversAsked.Add(id); }
        return Task.FromResult(Leftovers.TryGetValue(id, out var said) ? said : null);
    }

    public Task<PoolTaskInfo?> DescribeAsync(string id, CancellationToken ct) =>
        Task.FromResult<PoolTaskInfo?>(Infos.TryGetValue(id, out var info) ? info : new PoolTaskInfo("Task " + id, "Body of " + id, ["src/" + id + "/**"], []));

    public Task<PoolTriage> TriageStateAsync(string id, CancellationToken ct)
    {
        lock (Triaged)
        {
            return Task.FromResult(
                Untriaged.Contains(id) ? new PoolTriage(PoolTriageState.Missing, "The task has not been triaged yet.")
                : Questions.TryGetValue(id, out var q) ? new PoolTriage(PoolTriageState.NeedsHuman, q)
                : new PoolTriage(PoolTriageState.AgentReady, "Clear."));
        }
    }

    public Task AnswerAsync(string id, string text, CancellationToken ct)
    {
        lock (Answers) { Answers.Add((id, text)); }
        return Task.CompletedTask;
    }

    public Task<PoolTriage> RetriageAsync(string id, CancellationToken ct)
    {
        var triage = Retriage(id);
        lock (Triaged)
        {
            Triaged.Add(id);
            if (triage.State != PoolTriageState.Missing)
            {
                Untriaged.Remove(id);
            }

            if (triage.State == PoolTriageState.NeedsHuman)
            {
                Questions[id] = triage.Detail;
            }
            else
            {
                Questions.Remove(id);
            }
        }

        return Task.FromResult(triage);
    }

    public Task PrepareRetryAsync(string id, CancellationToken ct)
    {
        lock (Retried) { Retried.Add(id); }
        return Task.CompletedTask;
    }

    public Task<string> CreateBugAsync(string title, string body, CancellationToken ct)
    {
        lock (Bugs) { Bugs.Add(title); }
        return Task.FromResult("#bug" + Bugs.Count);
    }

    public Task<IntegrationResult?> IntegrateAsync(ImplementResult result, CancellationToken ct) => Task.FromResult(Integrate(result));
}

public sealed class PoolUnattendedTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly Ws _workspace;
    private readonly PoolService _pool;
    private readonly PoolStateStore _state;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 28, 1, 0, 0, TimeSpan.Zero));
    private readonly FakeMaster _master = new();
    private readonly FakeTasks _tasks = new();
    private PoolConfig _config = new() { Unattended = true };

    public PoolUnattendedTests()
    {
        _workspace = new Ws(Path.Combine(_temp, "ws"));
        _pool = new PoolService(_workspace, "demo");
        _state = new PoolStateStore(_workspace, "demo");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static ImplementResult Result(string id, ImplementOutcome outcome, string message = "ok") =>
        new(id, outcome, message, "autopilot/" + id);

    private PoolRunner Runner(Func<string, Task<ImplementResult>> implement, Func<double?>? usage = null, TimeOnly? until = null) =>
        new(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, _ => false), "demo", (id, _) => implement(id),
            new PoolRunOptions
            {
                Config = () => _config,
                Master = _master,
                Tasks = _tasks,
                State = _state,
                TaskLock = new TaskRunLock(_workspace, _ => false),
                Clock = _clock,
                UsagePercent = usage,
                Until = until,
                PollInterval = TimeSpan.FromMilliseconds(20),
            });

    private Task<PoolRunResult> RunAsync(Func<string, Task<ImplementResult>> implement, int? limit = null, Func<double?>? usage = null, TimeOnly? until = null) =>
        Runner(implement, usage, until).RunAsync(limit, Path.Combine(_temp, "report"), CancellationToken.None);

    private void Add(params string[] ids)
    {
        foreach (var id in ids)
        {
            _pool.Add(id);
        }
    }

    // ---------- settings ----------

    [Fact]
    public void Pool_settings_default_to_attended_one_agent_and_an_integration_branch()
    {
        var pool = new AutopilotConfig().Pool;
        Assert.False(pool.Unattended);
        Assert.Equal(1, pool.Concurrency);
        Assert.True(pool.Integration.Enabled);
        Assert.Equal("draft", pool.Integration.FinalPr);
        Assert.Equal(PoolCategories.All, pool.AlwaysAsk);
        Assert.Equal(1, pool.OnFailure.Retries);
        Assert.Empty(ConfigLoader.Validate(new AutopilotConfig()));
    }

    [Fact]
    public void Pool_settings_are_read_from_the_file_and_checked()
    {
        var config = ConfigLoader.Parse("""{ "pool": { "unattended": true, "concurrency": 2, "master": { "model": "sonnet", "maxDecisionsPerRun": 3 }, "budget": { "maxAgentRuns": 6, "stopAtUsagePercent": 80 }, "integration": { "branch": "integration/night" } } }""");
        Assert.True(config.Pool.Unattended);
        Assert.Equal(2, config.Pool.Concurrency);
        Assert.Equal("sonnet", config.Pool.Master.Model);
        Assert.Equal(6, config.Pool.Budget.MaxAgentRuns);
        Assert.Empty(ConfigLoader.Validate(config));

        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { Concurrency = 5 } }), p => p.Contains("pool.concurrency", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { Concurrency = 0 } }), p => p.Contains("pool.concurrency", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { AlwaysAsk = ["secrets", "gossip"] } }), p => p.Contains("gossip", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { Integration = new IntegrationConfig { Branch = "main" } } }), p => p.Contains("integration/<name>", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { BaseBranch = "integration/night" }), p => p.Contains("same as the base branch", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { Integration = new IntegrationConfig { FinalPr = "ready" } } }), p => p.Contains("draft", StringComparison.Ordinal));
        Assert.Contains(ConfigLoader.Validate(config with { Pool = config.Pool with { Budget = new PoolBudgetConfig { StopAtUsagePercent = 0 } } }), p => p.Contains("stopAtUsagePercent", StringComparison.Ordinal));
    }

    // ---------- the pool master ----------

    [Fact]
    public async Task A_question_with_a_recommendation_is_answered_by_the_master_and_the_task_runs_again()
    {
        Add("1");
        _tasks.Questions["1"] = "Which backoff? Option 1: fixed delay. Option 2: exponential (recommended).";
        _master.Answer = _ => new MasterAnswer(true, 2, "Use exponential backoff.", "It is the recommended option and easy to change.", PoolCategories.Other, true);
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); });

        Assert.Equal(1, calls); // the stored needs-human verdict is handled before any implement run
        Assert.Single(result.Done);
        Assert.Empty(result.Parked);
        var written = Assert.Single(_tasks.Answers);
        Assert.Contains("Answer: option 2 (pool master)", written.Text, StringComparison.Ordinal);
        Assert.Contains("Reason: It is the recommended option", written.Text, StringComparison.Ordinal);
        var decision = Assert.Single(_state.Decisions());
        Assert.Equal(DecisionKinds.Answer, decision.Kind);
        Assert.Equal("1", decision.WorkItemId);
        Assert.Equal(3, result.AgentRuns); // master, triage again, implement
        Assert.Contains("Decisions of the pool master (1)", File.ReadAllText(result.ReportPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untriaged_task_is_triaged_before_it_is_implemented_and_counts_one_run()
    {
        Add("1");
        _tasks.Untriaged.Add("1");
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); });

        Assert.Equal(["1"], _tasks.Triaged);
        Assert.Equal(1, calls);
        Assert.Single(result.Done);
        Assert.Empty(_master.Asked);
        Assert.Equal(2, result.AgentRuns); // triage, implement
    }

    [Fact]
    public async Task An_untriaged_task_with_a_question_goes_to_the_master_after_its_triage()
    {
        Add("1");
        _tasks.Untriaged.Add("1");
        var triages = 0;
        _tasks.Retriage = _ => ++triages == 1
            ? new PoolTriage(PoolTriageState.NeedsHuman, "Which backoff? Option 1: fixed. Option 2: exponential (recommended).")
            : new PoolTriage(PoolTriageState.AgentReady, "Clear now.");
        _master.Answer = _ => new MasterAnswer(true, 2, "Use exponential backoff.", "Recommended and easy to change.", PoolCategories.Other, true);

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Single(result.Done);
        Assert.Empty(result.Parked);
        Assert.Equal(["1"], _master.Asked);
        Assert.Contains("Answer: option 2 (pool master)", Assert.Single(_tasks.Answers).Text, StringComparison.Ordinal);
        Assert.Equal(4, result.AgentRuns); // triage, master, triage, implement
    }

    [Fact]
    public async Task Two_untriaged_tasks_at_once_are_both_triaged_answered_and_implemented()
    {
        Add("1", "2");
        _config = _config with { Concurrency = 2, Budget = new PoolBudgetConfig { MaxAgentRuns = 8 } };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/a/**"], []);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/b/**"], []);
        _tasks.Untriaged.Add("1");
        _tasks.Untriaged.Add("2");
        var seen = new HashSet<string>();
        _tasks.Retriage = id =>
        {
            lock (seen) { seen.Add(id); }
            return id == "1" && !_tasks.Answers.Any(a => a.Id == "1")
                ? new PoolTriage(PoolTriageState.NeedsHuman, "Which order? Option 1: by name (recommended).")
                : new PoolTriage(PoolTriageState.AgentReady, "Clear.");
        };
        _master.Answer = _ => new MasterAnswer(true, 1, "By name.", "Recommended.", PoolCategories.Other, true);
        var peak = new Peak { WaitForTogether = 2 };

        var result = await RunAsync(id => peak.RunAsync(id));

        Assert.Equal(["1", "2"], result.Done.Select(d => d.WorkItemId).Order());
        Assert.Empty(result.Parked);
        Assert.Equal(["1"], _master.Asked);
        Assert.Equal(2, peak.Max);
        Assert.Equal(6, result.AgentRuns); // task 1: triage, master, triage, implement; task 2: triage, implement
    }

    [Fact]
    public async Task A_task_that_is_still_needs_human_after_the_answer_is_parked_with_the_triage_verdict()
    {
        Add("1");
        _tasks.Questions["1"] = "Which order? Option 1: by name.";
        _tasks.Retriage = _ => new PoolTriage(PoolTriageState.NeedsHuman, "The answer does not say who owns the sorting.");
        _master.Answer = _ => new MasterAnswer(true, 1, "By name.", "Fine.", PoolCategories.Other, true);
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); });

        Assert.Equal(0, calls);
        var parked = Assert.Single(_state.Parked());
        Assert.Contains("still not ready after the master's answer", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("needs-human", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("who owns the sorting", parked.Reason, StringComparison.Ordinal);
        Assert.Single(result.Parked);
    }

    [Fact]
    public async Task A_triage_that_gives_no_result_is_parked_with_its_error_and_the_pool_goes_on()
    {
        Add("1", "2");
        _tasks.Untriaged.Add("1");
        _tasks.Retriage = _ => new PoolTriage(PoolTriageState.Missing, "The triage agent failed: timeout");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
        var parked = Assert.Single(_state.Parked());
        Assert.Equal("1", parked.WorkItemId);
        Assert.Contains("could not be triaged", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("timeout", parked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_triage_files_under_secrets_is_parked_without_asking_the_master_and_the_pool_goes_on()
    {
        Add("1", "2");
        _tasks.Infos["1"] = new PoolTaskInfo("Send a key", "The client needs a key.", ["src/1/**"], [], Topic: PoolCategories.Secrets);
        _tasks.Questions["1"] = "Which API key should the client send?";
        var result = await RunAsync(id => Task.FromResult(id == "1" ? Result(id, ImplementOutcome.NotReady, "needs-human") : Result(id, ImplementOutcome.Published)));

        Assert.Empty(_master.Asked);
        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
        var parked = Assert.Single(_state.Parked());
        Assert.Equal("1", parked.WorkItemId);
        Assert.Equal(PoolCategories.Secrets, parked.Category);
        Assert.Contains("Triage says the question is about secrets", parked.Reason, StringComparison.Ordinal);
        Assert.Empty(_pool.List());
    }

    [Theory]
    [InlineData("secrets", true)]
    [InlineData("paid", true)]
    [InlineData("other", false)]
    public async Task The_masters_own_category_or_an_irreversible_answer_parks_the_task(string category, bool reversible)
    {
        Add("1");
        _tasks.Questions["1"] = "Should the list be sorted by name or by date?";
        _master.Answer = _ => new MasterAnswer(true, 1, "By name.", "Looks right.", category, reversible);

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.NotReady, "needs-human")));

        Assert.Single(_master.Asked);
        Assert.Empty(_tasks.Answers);
        Assert.Empty(result.Done);
        Assert.Single(_state.Parked());
        Assert.Empty(_state.Decisions());
    }

    [Fact]
    public async Task A_master_that_does_not_decide_parks_the_task_and_the_decision_limit_is_kept()
    {
        Add("1", "2", "3");
        foreach (var id in new[] { "1", "2", "3" })
        {
            _tasks.Questions[id] = "Sort by name or by date?";
        }

        _config = _config with { Master = new PoolMasterConfig { MaxDecisionsPerRun = 1 } };
        _master.Answer = q => q.WorkItemId == "1"
            ? new MasterAnswer(true, 1, "By name.", "Fine.", PoolCategories.Other, true)
            : new MasterAnswer(false, null, string.Empty, "unclear", PoolCategories.Other, true);
        var seen = new HashSet<string>();

        var result = await RunAsync(id => Task.FromResult(seen.Add(id) ? Result(id, ImplementOutcome.NotReady, "needs-human") : Result(id, ImplementOutcome.Published)));

        Assert.Equal(["1"], result.Done.Select(d => d.WorkItemId));
        Assert.Equal(["2", "3"], _state.Parked().Select(p => p.WorkItemId).Order());
        Assert.Single(_state.Decisions());
    }

    [Fact]
    public async Task Without_unattended_mode_a_question_waits_for_a_person_as_before()
    {
        _config = new PoolConfig { Unattended = false };
        Add("1");
        _tasks.Questions["1"] = "Sort by name or by date?";
        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.NotReady, "needs-human")));

        Assert.Single(result.NeedsHuman);
        Assert.Empty(_master.Asked);
        Assert.Empty(_state.Parked());
    }

    // ---------- failures ----------

    [Fact]
    public async Task An_infrastructure_failure_pauses_the_pool_with_a_reason_and_a_retry_time()
    {
        Add("1", "2");
        var calls = 0;
        Task<ImplementResult> Implement(string id)
        {
            calls++;
            return Task.FromResult(calls == 1 ? Result(id, ImplementOutcome.Failed, "You have hit your limit") : Result(id, ImplementOutcome.Published));
        }

        var first = await RunAsync(Implement);
        Assert.NotNull(first.StoppedBecause);
        var pause = _state.CurrentPause();
        Assert.NotNull(pause);
        Assert.Contains("usage limit", pause!.Reason, StringComparison.Ordinal);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(30), pause.RetryAt);
        Assert.Equal(["1", "2"], _pool.List().Select(e => e.WorkItemId));

        // Before the retry time the pool does not start anything.
        var second = await RunAsync(Implement);
        Assert.Equal(1, calls);
        Assert.Contains("paused until", second.StoppedBecause, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromMinutes(31));
        var third = await RunAsync(Implement);
        Assert.Equal(2, third.Done.Count);
        Assert.Null(_state.CurrentPause());
    }

    [Fact]
    public async Task A_failed_task_is_tried_once_more_with_the_masters_notes_and_then_parked()
    {
        Add("1", "2");
        _master.Classify = _ => new FailureVerdict(FailureKind.Task, "Mind the null case.", "The tests failed on a null.", null);
        var attempts = new Dictionary<string, int>();
        Task<ImplementResult> Implement(string id)
        {
            attempts[id] = attempts.GetValueOrDefault(id) + 1;
            return Task.FromResult(id == "1" && attempts[id] == 2 || id == "2" && attempts[id] == 1
                ? Result(id, ImplementOutcome.Published)
                : Result(id, ImplementOutcome.Failed, "tests failed"));
        }

        var result = await RunAsync(Implement);
        Assert.Equal(2, attempts["1"]);
        Assert.Equal(["1", "2"], result.Done.Select(d => d.WorkItemId).Order());
        Assert.Equal(["1"], _tasks.Retried);
        Assert.Contains(_tasks.Answers, a => a.Text.Contains("Notes for the next attempt (pool master): Mind the null case.", StringComparison.Ordinal));
        Assert.Equal(DecisionKinds.Failure, Assert.Single(_state.Decisions()).Kind);

        // A second failure is parked, not tried a third time.
        Add("3");
        var always = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Rejected, "gate said no")));
        Assert.Empty(always.Done);
        Assert.Equal("3", Assert.Single(_state.Parked()).WorkItemId);
    }

    [Fact]
    public async Task A_suspected_defect_in_autopilot_creates_a_bug_task_and_parks_the_original()
    {
        Add("1", "2");
        _master.Classify = _ => new FailureVerdict(FailureKind.AutopilotDefect, string.Empty, "A crash inside the pipeline, not in the task.", "Pipeline crashes on empty triage");

        var result = await RunAsync(id => Task.FromResult(id == "1" ? Result(id, ImplementOutcome.Failed, "NullReferenceException at Autopilot.Core.Pipeline") : Result(id, ImplementOutcome.Published)));

        Assert.Equal(["Pipeline crashes on empty triage"], _tasks.Bugs);
        Assert.Single(result.Bugs);
        Assert.Equal("1", Assert.Single(_state.Parked()).WorkItemId);
        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
        Assert.Contains("Bugs found in Autopilot (1)", File.ReadAllText(result.ReportPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_the_master_calls_infrastructure_pauses_the_pool()
    {
        Add("1", "2");
        _master.Classify = _ => new FailureVerdict(FailureKind.Infrastructure, string.Empty, "The network is down.", null);
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Failed, "could not reach the host")); });

        Assert.Equal(1, calls);
        Assert.NotNull(_state.CurrentPause());
        Assert.Contains("network", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal(2, _pool.List().Count);
    }

    [Fact]
    public async Task A_task_that_does_not_pass_build_and_tests_on_the_integration_branch_is_parked()
    {
        Add("1", "2");
        _tasks.Integrate = r => r.WorkItemId == "1" ? new IntegrationResult(false, "Build failed after merging task 1.") : new IntegrationResult(true, "Merged.");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
        Assert.Contains("Build failed", Assert.Single(_state.Parked()).Reason, StringComparison.Ordinal);
    }

    // ---------- budget and stop time ----------

    [Fact]
    public async Task The_agent_run_budget_stops_the_pool()
    {
        Add("1", "2", "3");
        _config = _config with { Budget = new PoolBudgetConfig { MaxAgentRuns = 2 } };

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(2, result.Done.Count);
        Assert.Contains("budget of 2 agent runs", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal(["3"], _pool.List().Select(e => e.WorkItemId));
        Assert.Contains("not started", Assert.Single(result.NotReached), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_at_the_limit_stops_the_pool_before_the_first_task()
    {
        Add("1");
        var calls = 0;
        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); }, usage: () => 83);

        Assert.Equal(0, calls);
        Assert.Contains("usage is 83%", result.StoppedBecause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_agents_share_one_budget()
    {
        Add("1", "2");
        _config = _config with { Concurrency = 2, Budget = new PoolBudgetConfig { MaxAgentRuns = 1 } };
        var calls = 0;

        var result = await RunAsync(async id => { Interlocked.Increment(ref calls); await Task.Delay(50); return Result(id, ImplementOutcome.Published); });

        Assert.Equal(1, calls);
        Assert.Single(result.Done);
        Assert.Single(_pool.List());
        Assert.Contains("budget", result.StoppedBecause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_task_starts_after_the_stop_time()
    {
        Add("1", "2", "3");
        var calls = 0;
        _clock.Advance(TimeSpan.FromMinutes(-1) + TimeSpan.FromHours(4) + TimeSpan.FromMinutes(-2)); // 04:57 on the fake day

        var result = await RunAsync(id => { calls++; _clock.Advance(TimeSpan.FromMinutes(2)); return Task.FromResult(Result(id, ImplementOutcome.Published)); },
            until: new TimeOnly(5, 0));

        Assert.Equal(2, calls); // 04:57 and 04:59 start, 05:01 does not
        Assert.Contains("stop time 05:00", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal(["3"], _pool.List().Select(e => e.WorkItemId));
    }

    // ---------- concurrency ----------

    private sealed class Peak
    {
        private int _now;
        public int Max { get; private set; }

        public List<string> Log { get; } = [];

        private readonly TaskCompletionSource _together = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, a task waits until this many tasks run at once (or ten seconds pass, so a serial pool fails instead of hanging).</summary>
        public int WaitForTogether { get; init; }

        public async Task<ImplementResult> RunAsync(string id, int millis = 50)
        {
            lock (Log)
            {
                Log.Add("start " + id);
                Max = Math.Max(Max, ++_now);
                if (WaitForTogether > 0 && _now >= WaitForTogether)
                {
                    _together.TrySetResult();
                }
            }

            if (WaitForTogether > 0)
            {
                await Task.WhenAny(_together.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            else
            {
                await Task.Delay(millis);
            }

            lock (Log) { Log.Add("end " + id); _now--; }
            return Result(id, ImplementOutcome.Published);
        }
    }

    [Fact]
    public async Task With_two_agents_tasks_with_different_paths_run_together()
    {
        Add("1", "2");
        _config = new PoolConfig { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/a/**"], []);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/b/**", "tests/b/**"], []);
        var peak = new Peak { WaitForTogether = 2 };

        var result = await RunAsync(id => peak.RunAsync(id));

        Assert.Equal(2, result.Done.Count);
        Assert.Equal(2, peak.Max);
        Assert.Empty(_state.Slots());
    }

    [Fact]
    public async Task With_two_agents_tasks_that_share_a_path_or_a_dependency_run_one_after_the_other()
    {
        Add("1", "2", "3");
        _config = new PoolConfig { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/shared/**"], []);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/shared/Part/*.cs"], []);
        _tasks.Infos["3"] = new PoolTaskInfo("Three", string.Empty, ["docs/**"], ["2"]);
        var peak = new Peak();

        var result = await RunAsync(id => peak.RunAsync(id));

        Assert.Equal(3, result.Done.Count);
        Assert.Equal(1, peak.Max);
        Assert.Equal(["start 1", "end 1", "start 2", "end 2", "start 3", "end 3"], peak.Log);
    }

    [Fact]
    public async Task A_task_waits_for_its_dependency_and_is_parked_when_that_one_fails()
    {
        Add("1", "2");
        _config = new PoolConfig { Concurrency = 2, Unattended = false };
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["docs/**"], ["1"]);
        var calls = new List<string>();

        var result = await RunAsync(id => { lock (calls) { calls.Add(id); } return Task.FromResult(Result(id, ImplementOutcome.Rejected, "gate said no")); });

        Assert.Equal(["1"], calls);
        Assert.Single(result.Rejected);
        Assert.Equal("2", Assert.Single(_state.Parked()).WorkItemId);
    }

    [Fact]
    public async Task The_slider_can_be_raised_while_the_pool_runs()
    {
        Add("1", "2", "3");
        _config = new PoolConfig { Concurrency = 1 };
        var started = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var release = new TaskCompletionSource();
        var peak = 0;
        var now = 0;

        var run = RunAsync(async id =>
        {
            started[id] = true;
            peak = Math.Max(peak, Interlocked.Increment(ref now));
            await release.Task;
            Interlocked.Decrement(ref now);
            return Result(id, ImplementOutcome.Published);
        });

        await WaitAsync(() => started.ContainsKey("1"));
        Assert.False(started.ContainsKey("2")); // one agent: the others wait
        Assert.Contains(_state.Slots(), s => s.WorkItemId == "2" && s.State == "waiting" && s.Detail!.Contains("slot", StringComparison.Ordinal));

        _config = new PoolConfig { Concurrency = 3 };
        await WaitAsync(() => started.ContainsKey("2") && started.ContainsKey("3"));
        release.SetResult();
        var result = await run;

        Assert.Equal(3, peak);
        Assert.Equal(3, result.Done.Count);
    }

    [Fact]
    public async Task The_status_names_the_task_that_holds_a_waiting_task_back()
    {
        Add("1", "2");
        _config = new PoolConfig { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/x/**"], []);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/x/y.cs"], []);
        var release = new TaskCompletionSource();
        var run = RunAsync(async id => { await release.Task; return Result(id, ImplementOutcome.Published); });

        await WaitAsync(() => _state.Slots().Any(s => s.WorkItemId == "2"));
        var slots = _state.Slots();
        Assert.Contains(slots, s => s.WorkItemId == "1" && s.State == "running");
        Assert.Contains(slots, s => s.WorkItemId == "2" && s.State == "waiting" && s.Detail == "shares files with task 1 (src/x/y.cs and src/x/** cover the same files)");
        release.SetResult();
        await run;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The condition did not become true in time.");
    }

    // ---------- locks, hints, decisions ----------

    [Fact]
    public void One_task_cannot_be_locked_twice_but_two_tasks_can()
    {
        var alive = true;
        var first = new TaskRunLock(_workspace, _ => alive, () => 100);
        var second = new TaskRunLock(_workspace, _ => alive, () => 200);

        using var held = first.Acquire("demo", "1", "pool run");
        using var other = second.Acquire("demo", "2", "pool run");
        Assert.Throws<TaskLockHeldException>(() => second.Acquire("demo", "1", "pool run"));
        Assert.Equal(100, second.Holder("demo", "1")?.Pid);

        alive = false;
        using var stale = second.Acquire("demo", "1", "pool run");
    }

    [Fact]
    public async Task A_task_locked_by_another_process_is_skipped()
    {
        Add("1");
        using var held = new TaskRunLock(_workspace, _ => true, () => 777).Acquire("demo", "1", "another pool");
        var calls = 0;
        var runner = new PoolRunner(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, _ => false), "demo",
            (id, _) => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); },
            new PoolRunOptions { Config = () => _config, TaskLock = new TaskRunLock(_workspace, _ => true, () => 1), Clock = _clock });

        var result = await runner.RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Contains("already being worked on", Assert.Single(result.NotReached), StringComparison.Ordinal);
        Assert.Single(_pool.List());
    }

    [Fact]
    public void Writable_paths_overlap_when_one_folder_contains_the_other()
    {
        Assert.True(PathOverlap.Overlaps(["src/a/**"], ["src/a/b/*.cs"]));
        Assert.True(PathOverlap.Overlaps(["**"], ["docs/x.md"]));
        Assert.True(PathOverlap.Overlaps(["src/**"], ["src/a/**"]));
        Assert.False(PathOverlap.Overlaps(["src/a/**"], ["src/b/**"]));
        Assert.False(PathOverlap.Overlaps(["src/a/x.cs"], ["src/a/y.cs"]));
        Assert.True(PathOverlap.Overlaps(["docs/**", "src/a/**"], ["src/a/z.cs"]));
    }

    [Fact]
    public void Task_text_gives_paths_and_labels_give_dependencies()
    {
        Assert.Equal(["src/Billing/**", "tests/Billing.Tests/**"], PoolTaskHints.PathsFrom("Do it.\nPaths: src/Billing/**, `tests/Billing.Tests/**`\nMore."));
        Assert.Empty(PoolTaskHints.PathsFrom("No hints here."));
        Assert.Equal(["12", "7"], PoolTaskHints.DependenciesFrom(["bug", "depends-on:12", "depends-on-7"]));
    }

    [Fact]
    public void A_decision_can_be_undone_once_and_the_answer_leaves_the_task_text()
    {
        var decision = _state.AddDecision("5", DecisionKinds.Answer, "Which?", "option 1", "Easy to change.", "other", _clock.GetUtcNow());
        Assert.Equal(0, _state.RevertedCount());
        Assert.True(_state.Revert(decision.Id, _clock.GetUtcNow()));
        Assert.False(_state.Revert(decision.Id, _clock.GetUtcNow()));
        Assert.Equal(1, _state.RevertedCount());
        Assert.NotNull(_state.Decision(decision.Id)!.RevertedAt);

        var body = "Make it faster.\n\nAnswer: option 1 (pool master) By name.\nReason: Easy to change.\n\nAnswer: use the cache (a person)";
        Assert.Equal("Make it faster.\n\n\nAnswer: use the cache (a person)", MasterGuard.RetractAnswers(body));
    }

    [Fact]
    public void Parked_tasks_and_the_pause_are_kept_per_project()
    {
        _state.Park("1", "paid", "Costs money.", _clock.GetUtcNow());
        _state.Pause("No network.", null, _clock.GetUtcNow());
        var other = new PoolStateStore(_workspace, "other");
        Assert.Empty(other.Parked());
        Assert.Null(other.CurrentPause());
        Assert.Single(_state.Parked());
        Assert.True(_state.Unpark("1"));
        Assert.True(_state.Resume());
    }

    [Fact]
    public void A_person_pause_has_no_retry_time_is_idempotent_and_resume_removes_it()
    {
        Assert.True(_state.PauseByPerson(_clock.GetUtcNow()));
        Assert.False(_state.PauseByPerson(_clock.GetUtcNow().AddHours(1)));

        var pause = _state.CurrentPause()!;
        Assert.Equal("Paused by a person", pause.Reason);
        Assert.Null(pause.RetryAt);
        Assert.Equal(_clock.GetUtcNow(), pause.CreatedAt);
        Assert.True(PoolStateStore.IsPersonPause(pause));

        _state.Pause("Budget used up. x", _clock.GetUtcNow().AddHours(2), _clock.GetUtcNow()); // does not replace a person pause
        Assert.Equal("Paused by a person", _state.CurrentPause()!.Reason);

        Assert.True(_state.Resume());
        Assert.Null(_state.CurrentPause());
    }

    [Fact]
    public void A_person_pause_does_not_overwrite_a_budget_pause_and_keeps_its_retry_time()
    {
        var retry = _clock.GetUtcNow().AddHours(3);
        _state.Pause("Budget used up, resets at x.", retry, _clock.GetUtcNow());

        Assert.False(_state.PauseByPerson(_clock.GetUtcNow()));

        var pause = _state.CurrentPause()!;
        Assert.StartsWith("Budget used up", pause.Reason, StringComparison.Ordinal);
        Assert.Equal(retry, pause.RetryAt);
    }

    [Fact]
    public async Task A_run_with_a_person_pause_starts_no_task_and_keeps_the_pause_and_the_queue()
    {
        Add("1", "2");
        _state.PauseByPerson(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromDays(3)); // no expiry by time
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Published)); });

        Assert.Equal(0, calls);
        Assert.Contains("paused by a person", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal("Paused by a person", _state.CurrentPause()!.Reason);
        Assert.Equal(2, _pool.List().Count);
    }

    [Fact]
    public async Task A_pause_set_while_the_run_goes_lets_the_started_task_finish_and_starts_no_other()
    {
        Add("1", "2");
        var started = new List<string>();

        var result = await RunAsync(id =>
        {
            started.Add(id);
            _state.PauseByPerson(_clock.GetUtcNow()); // a person pauses while task 1 works
            return Task.FromResult(Result(id, ImplementOutcome.Published));
        });

        Assert.Equal(["1"], started);
        Assert.Single(result.Done); // the started task finished normally
        Assert.Contains("paused by a person", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal(["2"], _pool.List().Select(e => e.WorkItemId));
        Assert.NotNull(_state.CurrentPause());
    }

    [Fact]
    public async Task Resume_lifts_a_person_pause_and_the_next_run_starts_tasks()
    {
        Add("1");
        _state.PauseByPerson(_clock.GetUtcNow());
        Assert.True(_state.Resume());

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Single(result.Done);
        Assert.Null(result.StoppedBecause);
    }

    [Fact]
    public void Pruning_deletes_parked_entries_of_tasks_that_are_not_open_any_more()
    {
        _state.Park("1", "paid", "Closed since.", _clock.GetUtcNow());
        _state.Park("2", "paid", "Still open.", _clock.GetUtcNow());
        var other = new PoolStateStore(_workspace, "other");
        other.Park("1", null, "Other project.", _clock.GetUtcNow());

        var removed = _state.PruneParked(new HashSet<string>(["2"], StringComparer.Ordinal));

        Assert.Equal(["1"], removed);
        Assert.Equal(["2"], _state.Parked().Select(p => p.WorkItemId));
        Assert.Single(other.Parked());
        Assert.Empty(_state.PruneParked(new HashSet<string>(["2"], StringComparer.Ordinal)));
    }

    [Fact]
    public void The_masters_answer_is_read_from_the_model_output_and_checked_in_code()
    {
        var answer = new MasterAnswer(true, 1, "x", "y", "secrets", true);
        Assert.NotNull(MasterGuard.ParkReason(answer, PoolCategories.All));
        Assert.Null(MasterGuard.ParkReason(answer with { Category = "other" }, PoolCategories.All));
        Assert.Null(MasterGuard.ParkReason(answer, []));
        Assert.Null(JsonSerializer.Deserialize<string?>("null"));
    }

    // ---------- schedule ----------

    private ScheduleService NewSchedule(FakeSchedulerForTests scheduler) => new(_workspace, "demo", scheduler, _clock);

    [Fact]
    public async Task A_dated_one_time_schedule_is_planned_without_installing_and_rejects_the_past()
    {
        var scheduler = new FakeSchedulerForTests();
        var service = NewSchedule(scheduler);

        var plan = service.Plan(new ScheduleRequest("2026-11-28 02:00", Until: "06:00", Limit: 5, Unattended: true), "autopilot --project demo");
        Assert.Equal("2026-11-28", plan.Date);
        Assert.Equal(ScheduleRepeats.None, plan.Repeat);
        Assert.Contains("--limit 5", plan.Command, StringComparison.Ordinal);
        Assert.Contains("--unattended", plan.Command, StringComparison.Ordinal);
        Assert.Contains("--until 06:00", plan.Command, StringComparison.Ordinal);
        Assert.Contains("--only-on 2026-11-28", plan.Command, StringComparison.Ordinal);
        Assert.Contains("on 2026-11-28 at 02:00", plan.ToString(), StringComparison.Ordinal);
        Assert.Empty(scheduler.Jobs);

        Assert.Throws<WorkspaceException>(() => service.Plan(new ScheduleRequest("2026-11-27 02:00"), "x"));
        Assert.Throws<WorkspaceException>(() => service.Plan(new ScheduleRequest("2026-13-01 02:00"), "x"));
        Assert.Throws<WorkspaceException>(() => service.Plan(new ScheduleRequest("02:00", "none"), "x"));
        Assert.Throws<WorkspaceException>(() => service.Plan(new ScheduleRequest("02:00", "fortnightly"), "x"));
        Assert.Throws<WorkspaceException>(() => service.Plan(new ScheduleRequest("02:00", Until: "late"), "x"));

        var note = await service.SetAsync(plan, CancellationToken.None);
        Assert.Null(note);
        Assert.Equal(["autopilot-demo", "autopilot-demo-catchup"], scheduler.Jobs.Keys.Order());
        Assert.True(scheduler.Jobs["autopilot-demo-catchup"].OnLogon);
        Assert.EndsWith("schedule catch-up", scheduler.Jobs["autopilot-demo-catchup"].Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missed_run_is_caught_up_until_its_stop_time_and_not_started_twice()
    {
        var scheduler = new FakeSchedulerForTests();
        var service = NewSchedule(scheduler);
        var plan = service.Plan(new ScheduleRequest("2026-11-28 02:00", Until: "06:00"), "autopilot");
        await service.SetAsync(plan, CancellationToken.None);

        Assert.Null(await service.MissedAsync(CancellationToken.None)); // 01:00, not due yet
        _clock.Advance(TimeSpan.FromHours(2)); // 03:00, the computer was off at 02:00
        Assert.NotNull(await service.MissedAsync(CancellationToken.None));

        service.MarkStarted();
        Assert.Null(await service.MissedAsync(CancellationToken.None));
        Assert.NotNull((await service.ShowAsync(CancellationToken.None))!.LastStartedAt);
    }

    [Fact]
    public async Task A_missed_run_after_the_stop_time_is_not_caught_up()
    {
        var service = NewSchedule(new FakeSchedulerForTests());
        await service.SetAsync(service.Plan(new ScheduleRequest("2026-11-28 02:00", Until: "06:00"), "autopilot"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(6)); // 07:00
        Assert.Null(await service.MissedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_repeating_schedule_finds_the_next_start_and_the_last_missed_one()
    {
        var scheduler = new FakeSchedulerForTests();
        var service = NewSchedule(scheduler);
        var plan = service.Plan(new ScheduleRequest("02:00", "mon,wed,fri"), "autopilot");
        Assert.Equal("mon,wed,fri", plan.Repeat);
        await service.SetAsync(plan, CancellationToken.None);

        // 2026-11-28 is a Saturday; the last start was Friday the 27th, the next is Monday the 30th.
        var next = ScheduleRules.NextRun(plan, _clock.GetUtcNow(), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 2, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.Zero), ScheduleRules.LastStart(plan, _clock.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Equal(next, (await service.ShowAsync(CancellationToken.None))!.Next);

        var weekdays = service.Plan(new ScheduleRequest("02:00", "weekdays"), "autopilot");
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 2, 0, 0, TimeSpan.Zero), ScheduleRules.NextRun(weekdays, _clock.GetUtcNow(), TimeZoneInfo.Utc));
        var daily = service.Plan(new ScheduleRequest("02:00"), "autopilot");
        Assert.Equal(ScheduleRepeats.Daily, daily.Repeat);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 2, 0, 0, TimeSpan.Zero), ScheduleRules.NextRun(daily, _clock.GetUtcNow(), TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task The_catch_up_job_failing_to_install_does_not_fail_the_schedule()
    {
        var scheduler = new FakeSchedulerForTests { RefuseLogon = true };
        var service = NewSchedule(scheduler);
        var note = await service.SetAsync(service.Plan(new ScheduleRequest("2026-11-28 02:00"), "autopilot"), CancellationToken.None);
        Assert.Contains("catch-up", note, StringComparison.Ordinal);
        Assert.True((await service.ShowAsync(CancellationToken.None))!.InstalledInOs);
        Assert.True(await service.RemoveAsync(CancellationToken.None));
        Assert.Empty(scheduler.Jobs);
    }

    [Fact]
    public async Task Windows_gets_a_weekly_trigger_for_chosen_days_and_a_logon_trigger_for_catch_up()
    {
        var calls = new List<string[]>();
        var scheduler = new WindowsTaskScheduler((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(new Autopilot.Core.Process.ProcessResult(0, string.Empty, string.Empty, string.Empty, false, TimeSpan.Zero));
        });

        await scheduler.InstallAsync(new ScheduledJob("autopilot-demo", "02:30", "run") { Repeat = "mon,fri" }, CancellationToken.None);
        await scheduler.InstallAsync(new ScheduledJob("autopilot-demo-catchup", "02:30", "catch") { OnLogon = true }, CancellationToken.None);

        Assert.Equal(["/Create", "/TN", "autopilot-demo", "/TR", "run", "/SC", "WEEKLY", "/D", "MON,FRI", "/ST", "02:30", "/F"], calls[0]);
        Assert.Equal(["/Create", "/TN", "autopilot-demo-catchup", "/TR", "catch", "/SC", "ONLOGON", "/F"], calls[1]);
    }

    private sealed class FakeSchedulerForTests : IScheduler
    {
        public Dictionary<string, ScheduledJob> Jobs { get; } = [];

        public bool RefuseLogon { get; init; }

        public string Description => "a fake scheduler";

        public Task InstallAsync(ScheduledJob job, CancellationToken ct)
        {
            if (RefuseLogon && job.OnLogon)
            {
                throw new WorkspaceException("Access is denied.");
            }

            Jobs[job.Name] = job;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.Remove(name));

        public Task<bool> ExistsAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.ContainsKey(name));
    }
}
