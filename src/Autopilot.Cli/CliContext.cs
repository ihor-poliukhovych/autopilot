using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Jobs;
using Autopilot.Core.Scheduling;

namespace Autopilot.Cli;

/// <summary>Streams and replaceable parts for one CLI invocation. Tests swap the agent runner and the gate.</summary>
public sealed record CliContext(TextReader In, TextWriter Out, TextWriter Error)
{
    /// <summary>Builds the agent runner. Null means Claude Code headless.</summary>
    public Func<IAgentRunner>? RunnerFactory { get; init; }

    /// <summary>The gate. Null means <see cref="MergeGate.Evaluate"/>.</summary>
    public Func<GateInput, AutopilotConfig, GateDecision>? EvaluateGate { get; init; }

    /// <summary>Gate facts collector. Null means <see cref="GateFactsCollector"/>.</summary>
    public Func<AutopilotConfig, IGateFactsCollector>? FactsCollectorFactory { get; init; }

    /// <summary>Folder for worktrees. Null means the per-user default outside the project.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>The operating system's scheduler for <c>schedule</c>. Null means Task Scheduler on Windows, crontab elsewhere.</summary>
    public IScheduler? Scheduler { get; init; }

    /// <summary>Starts detached jobs. Null means a real detached process.</summary>
    public IJobLauncher? JobLauncher { get; init; }

    /// <summary>Stops a job's process. Null means a real process-tree kill.</summary>
    public IProcessKiller? ProcessKiller { get; init; }

    /// <summary>Tells whether a process id is alive (stale locks, dead jobs). Null means ask the operating system.</summary>
    public Func<int, bool>? IsProcessAlive { get; init; }

    /// <summary>The program that scheduled and detached runs start, as file and leading arguments. Null means this program.</summary>
    public (string File, IReadOnlyList<string> Arguments)? Program { get; init; }

    /// <summary>Where <c>ui install</c> writes: the user's home and roaming application data folders. Null means the real ones.</summary>
    public (string Home, string AppData)? UserFolders { get; init; }

    /// <summary>Which autostart system <c>ui install</c> uses. Null means the one of this operating system.</summary>
    public Commands.AutostartSystem? AutostartSystem { get; init; }

    /// <summary>Runs the system commands of <c>ui install</c> (systemctl, launchctl). Null means start the process.</summary>
    public Func<string[], Task<int>>? RunSystemCommand { get; init; }

    /// <summary>Tells whether a UI answers on a port of this machine (<c>ui status</c>). Null means try to connect.</summary>
    public Func<int, bool>? IsPortInUse { get; init; }

    /// <summary>How long <c>ui restart</c> waits for the old UI to exit. Null means 30 seconds.</summary>
    public TimeSpan? UiStopTimeout { get; init; }

    /// <summary>False skips the dotnet, git and claude version checks in doctor (tests never start claude).</summary>
    public bool DoctorChecksTools { get; init; } = true;

    /// <summary>What <c>project add --detect</c> uses to look at a folder. Null means the real analyzer and the git history reader.</summary>
    public Func<string, CancellationToken, Task<Commands.ProjectDetection>>? Detector { get; init; }

    public static CliContext FromConsole() => new(Console.In, Console.Out, Console.Error);
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
}
