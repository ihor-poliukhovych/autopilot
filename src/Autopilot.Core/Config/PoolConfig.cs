namespace Autopilot.Core.Config;

/// <summary>
/// Settings of the pool: whether it may run without a person, who answers its questions, what happens
/// after a failure, how much it may spend, how many agents work at once, and where finished work goes.
/// The pool level wins over <see cref="AutopilotConfig.Trust"/>, which stays the project's default.
/// </summary>
public sealed record PoolConfig
{
    /// <summary>When true the pool never waits for a person: the pool master answers questions and classifies failures.</summary>
    public bool Unattended { get; init; }

    public PoolMasterConfig Master { get; init; } = new();

    public PoolFailureConfig OnFailure { get; init; } = new();

    /// <summary>
    /// Categories of question the master never decides: the task is parked for a person and the pool goes on.
    /// Known names are in <see cref="PoolCategories.All"/>.
    /// </summary>
    public IReadOnlyList<string> AlwaysAsk { get; init; } = [.. PoolCategories.All];

    public PoolBudgetConfig Budget { get; init; } = new();

    /// <summary>How many agents work at once, 1 to <see cref="MaxConcurrency"/>. Read again before each task is started.</summary>
    public int Concurrency { get; init; } = 1;

    public IntegrationConfig Integration { get; init; } = new();

    public PoolRetryConfig Retry { get; init; } = new();

    public const int MaxConcurrency = 4;
}

public sealed record PoolRetryConfig
{
    /// <summary>
    /// When true (the default) a retry continues from the previous attempt's kept branch, merged with the
    /// current base, and its prompt carries the earlier findings. False starts every attempt clean.
    /// </summary>
    public bool Continue { get; init; } = true;
}

public sealed record PoolMasterConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Claude Code model name or alias for the master. Null means Claude Code's default.</summary>
    public string? Model { get; init; }

    /// <summary>The master decides at most this many questions in one pool run; later ones are parked.</summary>
    public int MaxDecisionsPerRun { get; init; } = 10;
}

public sealed record PoolFailureConfig
{
    /// <summary>How many times a task that failed for its own reasons is tried again with the master's notes.</summary>
    public int Retries { get; init; } = 1;

    /// <summary>What happens after the last retry. Only "park" exists.</summary>
    public string Then { get; init; } = PoolFailureActions.Park;

    /// <summary>How long the pool stays paused after an infrastructure failure, in minutes.</summary>
    public int PauseMinutes { get; init; } = 30;
}

public static class PoolFailureActions
{
    public const string Park = "park";
}

public sealed record PoolBudgetConfig
{
    /// <summary>Most agent runs (implement attempts and master calls) in 24 hours. Null means no limit.</summary>
    public int? MaxAgentRuns { get; init; }

    /// <summary>The pool stops starting tasks when the subscription usage reaches this percent, when it is known.</summary>
    public int StopAtUsagePercent { get; init; } = 80;
}

/// <summary>
/// Task PRs go into an integration branch, never into the base branch. The pool merges a task there only
/// after build and tests pass. A person merges the final PR.
/// </summary>
public sealed record IntegrationConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Branch name, for example <c>integration/nightly</c>. <c>{date}</c> becomes yyyyMMdd. Empty means <c>integration/{date}</c>.</summary>
    public string? Branch { get; init; }

    /// <summary>The branch the final PR targets. Null means the project's <c>baseBranch</c>.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>How the final PR is opened. Only "draft" exists: a person marks it ready and merges it.</summary>
    public string FinalPr { get; init; } = "draft";

    public const string BranchPrefix = "integration/";
}

public static class PoolCategories
{
    public const string Secrets = "secrets";
    public const string StoredData = "stored-data";
    public const string Contracts = "contracts";
    public const string Paid = "paid";
    public const string Settings = "settings";
    public const string Rules = "rules";

    /// <summary>The category a question gets when it is none of the above.</summary>
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Secrets, StoredData, Contracts, Paid, Settings, Rules];
}
