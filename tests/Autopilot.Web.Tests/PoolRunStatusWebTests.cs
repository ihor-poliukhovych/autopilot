using Autopilot.Core;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

public sealed class PoolRunStatusWebTests
{
    private const string StatusUrl = "/api/projects/demo/pool/status";

    private static async Task<UiFixture> WithProjectAsync()
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        return ui;
    }

    [Fact]
    public async Task A_project_with_no_run_events_is_planned_with_empty_progress_and_timeline()
    {
        await using var ui = await WithProjectAsync();

        var (_, body) = await ui.GetAsync(StatusUrl);

        Assert.Equal("Planned", body.GetProperty("status").GetString());
        Assert.Equal("None", body.GetProperty("subStatus").GetString());
        var progress = body.GetProperty("progress");
        Assert.Equal(0, progress.GetProperty("done").GetInt32());
        Assert.Equal(0, progress.GetProperty("total").GetInt32());
        Assert.Equal(0, progress.GetProperty("parked").GetInt32());
        Assert.Empty(body.GetProperty("timeline").EnumerateArray());
        Assert.Empty(body.GetProperty("queue").EnumerateArray());
        Assert.True(body.TryGetProperty("settings", out _));
    }

    [Fact]
    public async Task The_latest_run_gives_status_sub_status_progress_and_timeline()
    {
        await using var ui = await WithProjectAsync();
        var events = new PoolRunEventStore(ui.Workspace);
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);
        events.Append("old", PoolRunEvent.Enqueue, t);
        events.Append("old", PoolRunEvent.Cancel, t.AddSeconds(1));
        events.Append("new", PoolRunEvent.Enqueue, t.AddMinutes(1));
        events.Append("new", PoolRunEvent.Start, t.AddMinutes(2));
        events.Append("new", PoolRunEvent.SlotGranted, t.AddMinutes(3), "1");
        events.Append("new", PoolRunEvent.TaskMerged, t.AddMinutes(4), "1", "merged");
        events.Append("new", PoolRunEvent.SlotGranted, t.AddMinutes(5), "2");
        events.Append("new", PoolRunEvent.TaskParked, t.AddMinutes(6), "2", "paid");

        var (_, body) = await ui.GetAsync(StatusUrl);

        Assert.Equal("Running", body.GetProperty("status").GetString());
        Assert.Equal("Working", body.GetProperty("subStatus").GetString());
        var progress = body.GetProperty("progress");
        Assert.Equal(1, progress.GetProperty("done").GetInt32());
        Assert.Equal(2, progress.GetProperty("total").GetInt32());
        Assert.Equal(1, progress.GetProperty("parked").GetInt32());
        var timeline = body.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(["Enqueue", "Start", "SlotGranted", "TaskMerged", "SlotGranted", "TaskParked"], timeline.Select(e => e.GetProperty("event").GetString()!));
        Assert.Equal("merged", timeline[3].GetProperty("detail").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, timeline[0].GetProperty("detail").ValueKind);
        Assert.Equal(t.AddMinutes(1), timeline[0].GetProperty("time").GetDateTimeOffset());
    }

    [Fact]
    public async Task Each_queue_task_gets_one_readiness_word()
    {
        await using var ui = await WithProjectAsync();
        var tasks = new LocalTaskService(ui.Workspace, "demo");
        tasks.Add("ready one");                                        // 1
        tasks.Add("asks", labels: ["depends-on:1"]);                    // 2
        tasks.Add("untriaged");                                         // 3
        tasks.Add("waiter", labels: ["depends-on:1"]);                  // 4
        tasks.Add("parked one");                                        // 5
        tasks.Add("depends on a task outside the pool");                // 6 (task 7 is not in the pool)
        tasks.Add("outside");                                           // 7
        var store = new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        foreach (var number in new[] { 1, 2, 4, 5 })
        {
            var label = number == 2 ? TriageLabel.NeedsHuman : TriageLabel.AgentReady;
            await store.SaveAsync(LocalTaskWorkItemSource.ToItem(tasks.Get(number)!), new TriageResult(label, "why", [], RiskLevel.Low), CancellationToken.None);
        }

        await store.SaveAsync(LocalTaskWorkItemSource.ToItem(tasks.Get(6)!), new TriageResult(TriageLabel.AgentReady, "why", [], RiskLevel.Low), CancellationToken.None);
        tasks.ChangeLabels(6, ["depends-on:7"], null);
        await store.SaveAsync(LocalTaskWorkItemSource.ToItem(tasks.Get(6)!), new TriageResult(TriageLabel.AgentReady, "why", [], RiskLevel.Low), CancellationToken.None);

        foreach (var id in new[] { "1", "2", "3", "4", "5", "6" })
        {
            await ui.PostAsync("/api/projects/demo/pool", new { id });
        }

        new PoolStateStore(ui.Workspace, "demo").Park("5", "paid", "It costs money.", DateTimeOffset.UtcNow);

        var (_, body) = await ui.GetAsync(StatusUrl);

        var words = body.GetProperty("queue").EnumerateArray().ToDictionary(q => q.GetProperty("id").GetString()!, q => q.GetProperty("readiness").GetString()!);
        Assert.Equal("ready", words["1"]);
        Assert.Equal("needs an answer", words["2"]);
        Assert.Equal("not triaged", words["3"]);
        Assert.Equal("waits for 1", words["4"]);
        Assert.Equal("parked", words["5"]);
        Assert.Equal("ready", words["6"]);
    }

    [Fact]
    public async Task A_run_started_outside_the_web_app_with_events_on_the_workspace_shows_as_Running_with_a_timeline()
    {
        await using var ui = await WithProjectAsync();
        var pool = new PoolService(ui.Workspace, "demo");
        pool.Add("1");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Built like the CLI builds it: events go to a PoolRunEventStore on the workspace the web page reads.
        var runner = new PoolRunner(pool, new Autopilot.Core.Runs.RunStore(ui.Workspace, "demo"), new ProjectRunLock(ui.Workspace, _ => false), "demo",
            async (id, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return new ImplementResult(id, ImplementOutcome.Published, "ok", "autopilot/" + id);
            },
            new PoolRunOptions { Config = () => new Autopilot.Core.Config.PoolConfig(), Events = new PoolRunEventStore(ui.Workspace), PollInterval = TimeSpan.FromMilliseconds(20) });
        var run = runner.RunAsync(null, Path.Combine(ui.Workspace.ProjectFolder("demo"), "report"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var (_, body) = await ui.GetAsync(StatusUrl);

        release.SetResult();
        await run;
        Assert.Equal("Running", body.GetProperty("status").GetString());
        Assert.NotEmpty(body.GetProperty("timeline").EnumerateArray());
    }

    [Fact]
    public async Task The_queue_counts_pool_entries_and_parked_tasks_together()
    {
        await using var ui = await WithProjectAsync();
        var pool = new PoolService(ui.Workspace, "demo");
        var state = new PoolStateStore(ui.Workspace, "demo");
        for (var i = 1; i <= 12; i++)
        {
            pool.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        for (var i = 13; i <= 20; i++)
        {
            state.Park(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "paid", "It costs money.", DateTimeOffset.UtcNow);
        }

        var (_, body) = await ui.GetAsync(StatusUrl);

        Assert.Equal(20, body.GetProperty("queue").GetArrayLength());
    }

    [Fact]
    public async Task Timeline_entries_name_their_task_and_the_status_counts_agent_runs_of_the_day()
    {
        await using var ui = await WithProjectAsync();
        var events = new PoolRunEventStore(ui.Workspace);
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);
        events.Append("new", PoolRunEvent.Enqueue, t);
        events.Append("new", PoolRunEvent.TaskMerged, t.AddMinutes(1), "7", "merged");
        var state = new PoolStateStore(ui.Workspace, "demo");
        state.CountAgentRun("7", "implement", DateTimeOffset.UtcNow.AddMinutes(-5));
        state.CountAgentRun("7", "master", DateTimeOffset.UtcNow.AddHours(-30));

        var (_, body) = await ui.GetAsync(StatusUrl);

        var timeline = body.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, timeline[0].GetProperty("taskId").ValueKind);
        Assert.Equal("7", timeline[1].GetProperty("taskId").GetString());
        Assert.Equal(1, body.GetProperty("agentRunsToday").GetInt32());
    }
}
