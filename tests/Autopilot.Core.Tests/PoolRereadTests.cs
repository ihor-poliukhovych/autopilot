using Autopilot.Core.Config;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

/// <summary>A running pool reads its queue from the store each cycle, so add, move and remove take effect at once.</summary>
public sealed class PoolRereadTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly Ws _workspace;
    private readonly PoolService _pool;

    public PoolRereadTests()
    {
        _workspace = new Ws(Path.Combine(_temp, "ws"));
        _pool = new PoolService(_workspace, "demo");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private Task<PoolRunResult> RunAsync(Func<string, Task<ImplementResult>> implement) =>
        new PoolRunner(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, _ => false, () => 1, TimeProvider.System), "demo", (id, _) => implement(id),
            new PoolRunOptions
            {
                Config = () => new PoolConfig { Concurrency = 1 },
                PollInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            }).RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

    private static ImplementResult Published(string id) => new(id, ImplementOutcome.Published, "ok", "autopilot/" + id);

    [Fact]
    public async Task A_task_added_while_the_pool_runs_is_started_in_the_same_run()
    {
        _pool.Add("1");
        var started = new List<string>();
        var release = new TaskCompletionSource();
        var result = await RunAsync(async id =>
        {
            lock (started) { started.Add(id); }
            if (id == "1")
            {
                _pool.Add("2");
                await Task.Delay(100);
            }

            return Published(id);
        });

        Assert.Equal(["1", "2"], started);
        Assert.Equal(["1", "2"], result.Done.Select(d => d.WorkItemId));
        Assert.Empty(result.NotReached);
        release.TrySetResult();
    }

    [Fact]
    public async Task A_waiting_task_moved_with_Move_changes_which_task_is_picked_next()
    {
        _pool.Add("1");
        _pool.Add("2");
        _pool.Add("3");
        var started = new List<string>();
        await RunAsync(async id =>
        {
            lock (started) { started.Add(id); }
            if (id == "1")
            {
                _pool.Move("3", 1);
                await Task.Delay(100);
            }

            return Published(id);
        });

        Assert.Equal(["1", "3", "2"], started);
    }

    [Fact]
    public async Task A_waiting_task_removed_with_Remove_is_never_started_and_is_not_listed_as_not_reached()
    {
        _pool.Add("1");
        _pool.Add("2");
        var started = new List<string>();
        var result = await RunAsync(async id =>
        {
            lock (started) { started.Add(id); }
            _pool.Remove("2");
            await Task.Delay(100);
            return Published(id);
        });

        Assert.Equal(["1"], started);
        Assert.Equal(["1"], result.Done.Select(d => d.WorkItemId));
        Assert.Empty(result.NotReached);
    }
}
