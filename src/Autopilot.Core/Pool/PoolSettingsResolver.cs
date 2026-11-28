using Autopilot.Core.Config;

namespace Autopilot.Core.Pool;

/// <summary>Pool-level values stored in the workspace. A null field means "not overridden".</summary>
public sealed record PoolOverride(bool? Unattended, int? Concurrency, bool? MasterEnabled, int? BudgetStopPercent, bool? IntegrationEnabled)
{
    public bool IsEmpty => Unattended is null && Concurrency is null && MasterEnabled is null && BudgetStopPercent is null && IntegrationEnabled is null;
}

public static class PoolSettingSources
{
    public const string Pool = "pool";
    public const string Project = "project";
}

/// <summary>A setting's effective value and where it comes from: "pool" or "project".</summary>
public sealed record EffectiveSetting<T>(T Value, string Source);

public sealed record EffectivePoolSettings(
    EffectiveSetting<bool> Unattended,
    EffectiveSetting<int> Concurrency,
    EffectiveSetting<bool> MasterEnabled,
    EffectiveSetting<int> BudgetStopPercent,
    EffectiveSetting<bool> IntegrationEnabled);

public static class PoolSettingsResolver
{
    /// <summary>Each setting on its own: the pool value when it is set, else the project's autopilot.json value.</summary>
    public static EffectivePoolSettings Resolve(PoolConfig project, PoolOverride? pool)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new EffectivePoolSettings(
            Pick(pool?.Unattended, project.Unattended),
            Pick(pool?.Concurrency, project.Concurrency),
            Pick(pool?.MasterEnabled, project.Master.Enabled),
            Pick(pool?.BudgetStopPercent, project.Budget.StopAtUsagePercent),
            Pick(pool?.IntegrationEnabled, project.Integration.Enabled));
    }

    private static EffectiveSetting<T> Pick<T>(T? pool, T project) where T : struct =>
        pool is { } value ? new(value, PoolSettingSources.Pool) : new(project, PoolSettingSources.Project);
}
