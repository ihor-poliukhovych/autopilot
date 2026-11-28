using Autopilot.Core.Config;
using Autopilot.Core.Pool;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class PoolSettingsResolverTests : IDisposable
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

    private static PoolConfig Project() => new()
    {
        Unattended = false,
        Concurrency = 2,
        Master = new PoolMasterConfig { Enabled = true },
        Budget = new PoolBudgetConfig { StopAtUsagePercent = 80 },
        Integration = new IntegrationConfig { Enabled = true },
    };

    [Fact]
    public void A_pool_value_wins_and_its_source_is_pool()
    {
        var result = PoolSettingsResolver.Resolve(Project(), new PoolOverride(true, 4, false, 50, false));

        Assert.Equal(new EffectiveSetting<bool>(true, "pool"), result.Unattended);
        Assert.Equal(new EffectiveSetting<int>(4, "pool"), result.Concurrency);
        Assert.Equal(new EffectiveSetting<bool>(false, "pool"), result.MasterEnabled);
        Assert.Equal(new EffectiveSetting<int>(50, "pool"), result.BudgetStopPercent);
        Assert.Equal(new EffectiveSetting<bool>(false, "pool"), result.IntegrationEnabled);
    }

    [Fact]
    public void A_null_override_or_no_override_row_gives_the_project_value_with_source_project()
    {
        foreach (var pool in new PoolOverride?[] { null, new PoolOverride(null, null, null, null, null) })
        {
            var result = PoolSettingsResolver.Resolve(Project(), pool);

            Assert.Equal(new EffectiveSetting<bool>(false, "project"), result.Unattended);
            Assert.Equal(new EffectiveSetting<int>(2, "project"), result.Concurrency);
            Assert.Equal(new EffectiveSetting<bool>(true, "project"), result.MasterEnabled);
            Assert.Equal(new EffectiveSetting<int>(80, "project"), result.BudgetStopPercent);
            Assert.Equal(new EffectiveSetting<bool>(true, "project"), result.IntegrationEnabled);
        }
    }

    [Fact]
    public void A_mixed_override_resolves_each_field_on_its_own()
    {
        var result = PoolSettingsResolver.Resolve(Project(), new PoolOverride(true, null, null, 60, null));

        Assert.Equal(new EffectiveSetting<bool>(true, "pool"), result.Unattended);
        Assert.Equal(new EffectiveSetting<int>(2, "project"), result.Concurrency);
        Assert.Equal(new EffectiveSetting<bool>(true, "project"), result.MasterEnabled);
        Assert.Equal(new EffectiveSetting<int>(60, "pool"), result.BudgetStopPercent);
        Assert.Equal(new EffectiveSetting<bool>(true, "project"), result.IntegrationEnabled);
    }

    [Fact]
    public void The_store_keeps_a_mixed_override_per_project_and_clears_it()
    {
        var workspace = new Workspace.Workspace(Path.Combine(_temp, "ws"));
        var demo = new PoolStateStore(workspace, "demo");
        var other = new PoolStateStore(workspace, "other");

        Assert.Null(demo.Override());

        demo.SetOverride(new PoolOverride(false, 3, null, null, true));
        Assert.Equal(new PoolOverride(false, 3, null, null, true), demo.Override());
        Assert.Null(other.Override());

        demo.SetOverride(new PoolOverride(null, 1, null, 70, null));
        Assert.Equal(new PoolOverride(null, 1, null, 70, null), demo.Override());

        demo.SetOverride(new PoolOverride(null, null, null, null, null));
        Assert.Null(demo.Override());
    }
}
