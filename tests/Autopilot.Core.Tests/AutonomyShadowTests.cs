using Autopilot.Core.Gate;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class AutonomyShadowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private Workspace.Workspace NewWorkspace() => new(Path.Combine(_temp, "ws"));

    [Fact]
    public void The_ladder_starts_at_observe_only_and_is_ordered()
    {
        Assert.Equal(AutonomyLevel.ObserveOnly, Autonomy.Ladder[0]);
        Assert.Equal(Autonomy.Ladder.OrderBy(l => (int)l), Autonomy.Ladder);
    }

    [Fact]
    public void The_gate_verdict_defaults_to_observe_only()
    {
        Assert.Equal(AutonomyLevel.ObserveOnly, Autonomy.DefaultLevel(AutonomyDecisionType.GateVerdict));
        Assert.Equal([AutonomyDecisionType.GateVerdict], Enum.GetValues<AutonomyDecisionType>());
    }

    [Fact]
    public void A_shadow_record_is_written_and_read_back()
    {
        var store = new AutonomyShadowStore(NewWorkspace());
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var written = store.Record("demo", "task-7", AutonomyDecisionType.GateVerdict, "merge", "reject", at);
        store.Record("other", "task-1", AutonomyDecisionType.GateVerdict, "merge", "merge");

        var records = store.List("demo");
        var record = Assert.Single(records);
        Assert.Equal(written, record);
        Assert.Equal("task-7", record.TaskOrRun);
        Assert.Equal("merge", record.AutopilotVerdict);
        Assert.Equal("reject", record.PersonVerdict);
        Assert.Equal(at, record.At);
    }

    [Fact]
    public void Migration_012_applies_on_a_fresh_workspace_and_on_an_upgrade_from_011()
    {
        Assert.True(Workspace.Workspace.LatestVersion >= 12);
        long latest = Workspace.Workspace.LatestVersion;

        var fresh = NewWorkspace();
        using (var connection = fresh.Open())
        {
            Assert.Equal(latest, Scalar(connection, "SELECT MAX(version) FROM schema_version"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM shadow_records"));
        }

        var upgraded = new Workspace.Workspace(Path.Combine(_temp, "upgrade"));
        using (var connection = upgraded.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS pool_overrides; DROP TABLE shadow_records; DELETE FROM schema_version WHERE version >= 12;";
            command.ExecuteNonQuery();
            Assert.Equal(11L, Scalar(connection, "SELECT MAX(version) FROM schema_version"));
        }

        using var after = upgraded.Open();
        Assert.Equal(latest, Scalar(after, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(0L, Scalar(after, "SELECT COUNT(*) FROM shadow_records"));
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
