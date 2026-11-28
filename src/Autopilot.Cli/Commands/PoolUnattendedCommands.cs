using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autopilot.Core.Config;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Settings;
using Autopilot.Core.Tasks;

namespace Autopilot.Cli.Commands;

/// <summary>Everything of <c>pool</c> that belongs to unattended runs: starting one, its settings, decisions, parked tasks and the integration branch.</summary>
internal static class PoolUnattendedCommands
{
    /// <summary>The options of <c>pool run</c> that this feature adds.</summary>
    public sealed record RunOptions(Option<bool> Unattended, Option<string?> Until, Option<string?> OnlyOn, Option<bool> Scheduled)
    {
        public static RunOptions Create() => new(
            new Option<bool>("--unattended") { Description = "Run without a person: the pool master answers questions and classifies failures; what it must not decide is parked. Overrides pool.unattended." },
            new Option<string?>("--until") { Description = "Start no new task after this time of day, HH:mm (local time)." },
            new Option<string?>("--only-on") { Description = "Run only on this date, yyyy-MM-dd (used by one-time schedules).", Hidden = true },
            new Option<bool>("--scheduled") { Description = "Marks a run started by the schedule, so a missed run is not started twice.", Hidden = true });
    }

    public static async Task<int> RunPoolAsync(ProjectTarget target, CliContext context, RunOptions run, int? limit, bool unattended, string? until, string? onlyOn, bool scheduled, CancellationToken ct)
    {
        var (project, workspace) = target.RequireRegistered();
        var clock = TimeProvider.System;
        if (onlyOn is not null && onlyOn != clock.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
        {
            await context.Out.WriteLineAsync($"Not {onlyOn}, so this one-time run does nothing today.").ConfigureAwait(false);
            return ExitCodes.Ok;
        }

        TimeOnly? stopAt = null;
        if (until is not null)
        {
            stopAt = ScheduleService.NormalizeTime(until) is { } text
                ? TimeOnly.ParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : throw new UsageException($"--until \"{until}\" is not a time of day. Use HH:mm, for example 06:00.");
        }

        var schedule = new ScheduleService(workspace, project.Key, context.Scheduler ?? (OperatingSystem.IsWindows() ? new WindowsTaskScheduler() : new CronScheduler()));
        if (scheduled)
        {
            schedule.MarkStarted();
        }

        AutopilotConfig config;
        try
        {
            config = ConfigLoader.Load(target.RepoRoot, target.DataFolder);
        }
        catch (ConfigException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var repoRoot = await GitClient.FindRepositoryRootAsync(target.RepoRoot, ct).ConfigureAwait(false);
        if (repoRoot is null)
        {
            await context.Error.WriteLineAsync($"{target.RepoRoot} is not in a git repository.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var git = new GitClient(repoRoot, config.Identity is { } id ? new GitIdentity(id.Name, id.Email) : null);
        var worktreeRoot = context.WorktreeRoot ?? GitClient.DefaultWorktreeRoot(repoRoot);
        var integration = new IntegrationService(workspace, project.Key, git, config, worktreeRoot);
        string? branch;
        try
        {
            branch = await integration.EnsureBranchAsync(clock.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var state = new PoolStateStore(workspace, project.Key);
        Autopilot.Core.Pipeline.AutopilotPipeline pipeline;
        try
        {
            pipeline = await ProjectServices.CreatePipelineAsync(target.RepoRoot, context, ct, null, target.DataFolder, target.LocalTasks,
                c => branch is null ? c : c with { BaseBranch = branch },
                PipelineCommands.TriageLockFor(target, context),
                w => w is LocalTaskWorkItemSource ? w : new PoolNotesWorkItemSource(w, state)).ConfigureAwait(false);
        }
        catch (ProjectSetupException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var triage = new TriageStore(target.RepoRoot, target.DataFolder);
        var items = target.LocalTasks is { } local
            ? new LocalTaskWorkItemSource(local, triage)
            : ProjectServices.CreateModules(target.RepoRoot, config, target.DataFolder, target.LocalTasks).WorkItems;
        var settings = new ProjectSettingsService(target.RepoRoot, target.DataFolder);
        var current = config;
        PoolConfig ReadPool()
        {
            // Read again each time: a person may move the concurrency slider while the pool runs.
            try
            {
                current = ConfigLoader.Load(target.RepoRoot, target.DataFolder);
            }
            catch (ConfigException)
            {
                // Keep the last good settings.
            }

            return current.Pool;
        }

        var master = new ClaudePoolMaster(ProjectServices.CreateRunner(context, config), config.Pool.Master, repoRoot,
            Path.Combine(workspace.ProjectFolder(project.Key), "pool-master"), ReadRules(settings),
            git, () => branch, worktreeRoot);
        var runner = new PoolRunner(new PoolService(workspace, project.Key), new Autopilot.Core.Runs.RunStore(workspace, project.Key),
            new ProjectRunLock(workspace, context.IsProcessAlive), project.Key, pipeline.ImplementAsync,
            new PoolRunOptions
            {
                Config = ReadPool,
                Unattended = unattended ? true : null,
                Master = master,
                Tasks = new PipelinePoolTasks(pipeline, items, triage, target.LocalTasks, git, config, integration, state, clock),
                State = state,
                TaskLock = new TaskRunLock(workspace, context.IsProcessAlive),
                IsProcessAlive = context.IsProcessAlive,
                Clock = clock,
                Until = stopAt,
                Events = new PoolRunEventStore(workspace),
            });

        if (branch is not null)
        {
            await context.Out.WriteLineAsync($"Integration branch: {branch} (task branches start from it; nothing is merged into {integration.BaseBranch}).").ConfigureAwait(false);
        }

        var result = await runner.RunAsync(limit, workspace.ProjectFolder(project.Key), ct).ConfigureAwait(false);
        await context.Out.WriteLineAsync(
            $"Done {result.Done.Count}, rejected {result.Rejected.Count}, needs a person {result.NeedsHuman.Count}, parked {result.Parked.Count}, not reached {result.NotReached.Count}.").ConfigureAwait(false);
        if (result.StoppedBecause is not null)
        {
            await context.Error.WriteLineAsync(result.StoppedBecause).ConfigureAwait(false);
        }

        await context.Out.WriteLineAsync($"Report: {result.ReportPath}").ConfigureAwait(false);
        if (onlyOn is not null && (await schedule.ShowAsync(ct).ConfigureAwait(false))?.Plan.Repeat == ScheduleRepeats.None)
        {
            await schedule.RemoveAsync(ct).ConfigureAwait(false);
        }

        return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static string ReadRules(ProjectSettingsService settings)
    {
        try
        {
            var text = new System.Text.StringBuilder();
            foreach (var file in settings.ListContextFiles())
            {
                if (settings.ReadContext(file) is { } body && text.Length < 20_000)
                {
                    text.AppendLine($"--- {file}").AppendLine(body);
                }
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is ConfigException or SettingsInvalidException or IOException)
        {
            return string.Empty;
        }
    }

    public static void AddTo(Command pool, ProjectOptions options, CliContext context)
    {
        pool.Subcommands.Add(Settings(options, context));
        pool.Subcommands.Add(Status(options, context));
        pool.Subcommands.Add(Pause(options, context));
        pool.Subcommands.Add(Resume(options, context));
        pool.Subcommands.Add(Decisions(options, context));
        pool.Subcommands.Add(Parked(options, context));
        pool.Subcommands.Add(Unpark(options, context));
        pool.Subcommands.Add(IntegrationCommand(options, context));
    }

    private static Option<bool> Json() => new("--json") { Description = "Print JSON." };

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ---------- settings ----------

    private static Command Settings(ProjectOptions options, CliContext context)
    {
        var unattended = new Option<bool?>("--unattended") { Description = "true or false: run without a person watching." };
        var concurrency = new Option<int?>("--concurrency") { Description = $"How many agents work at once, 1 to {PoolConfig.MaxConcurrency}. Takes effect for the next task a running pool starts." };
        var master = new Option<bool?>("--master") { Description = "true or false: the pool master answers questions and classifies failures." };
        var maxRuns = new Option<int?>("--max-agent-runs") { Description = "Most agent runs in 24 hours." };
        var stopAt = new Option<int?>("--stop-at-usage") { Description = "Stop starting tasks at this percent of the subscription limit, when it is known." };
        var integration = new Option<bool?>("--integration") { Description = "true or false: task PRs go into an integration branch. Turning it off sends each task PR straight to the base branch." };
        var command = new Command("settings", "Show the pool settings, or change them with the options.") { unattended, concurrency, master, maxRuns, stopAt, integration };
        command.SetAction(async (parse, _) =>
        {
            var target = options.Resolve(parse);
            var service = new ProjectSettingsService(target.RepoRoot, target.DataFolder);
            var change = new JsonObject();
            var poolChange = new JsonObject();
            if (parse.GetValue(unattended) is { } u) { poolChange["unattended"] = u; }
            if (parse.GetValue(concurrency) is { } c) { poolChange["concurrency"] = c; }
            if (parse.GetValue(master) is { } m) { poolChange["master"] = new JsonObject { ["enabled"] = m }; }
            var budget = new JsonObject();
            if (parse.GetValue(maxRuns) is { } runs) { budget["maxAgentRuns"] = runs; }
            if (parse.GetValue(stopAt) is { } s) { budget["stopAtUsagePercent"] = s; }
            if (budget.Count > 0) { poolChange["budget"] = budget; }

            if (parse.GetValue(integration) is { } i) { poolChange["integration"] = new JsonObject { ["enabled"] = i }; }

            try
            {
                var view = poolChange.Count > 0 ? service.Save(change.AddPool(poolChange)) : service.Read();
                var pool = view.Config.Pool;
                await context.Out.WriteLineAsync(poolChange.Count > 0 ? "Saved." : "Pool settings:").ConfigureAwait(false);
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(pool, ConfigLoader.JsonOptions)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }
            catch (SettingsInvalidException ex)
            {
                await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Failure;
            }
        });
        return command;
    }

    private static JsonObject AddPool(this JsonObject root, JsonObject pool)
    {
        root["pool"] = pool;
        return root;
    }

    // ---------- status, resume ----------

    private static Command Status(ProjectOptions options, CliContext context)
    {
        var json = Json();
        var command = new Command("status", "Who is working on what, what waits and why, the pause, parked tasks and the master's decisions.") { json };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var state = new PoolStateStore(workspace, project.Key);
            state.ReleaseDead(DateTimeOffset.UtcNow, PoolStateStore.DefaultStaleAfter, context.IsProcessAlive ?? ProjectRunLock.IsAlive, Environment.ProcessId);
            var slots = state.Slots();
            var pause = state.CurrentPause();
            var parked = state.Parked();
            var decisions = state.Decisions(10);
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(new { slots, pause, parked, decisions }, JsonOut)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync(pause is null ? "Not paused." : $"Paused: {pause.Reason}" + (pause.RetryAt is { } at ? $" (retry after {at.ToLocalTime():yyyy-MM-dd HH:mm})" : string.Empty)).ConfigureAwait(false);
            foreach (var slot in slots)
            {
                await context.Out.WriteLineAsync($"{slot.WorkItemId,-8} {slot.State,-8} {slot.Detail}").ConfigureAwait(false);
            }

            await context.Out.WriteLineAsync($"Parked: {parked.Count}. Master decisions: {state.Decisions().Count} (undone by a person: {state.RevertedCount()}).").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Pause(ProjectOptions options, CliContext context)
    {
        var command = new Command("pause", "Pause the pool: tasks already started finish, no new task starts. 'pool resume' lifts it.");
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var state = new PoolStateStore(workspace, project.Key);
            if (state.PauseByPerson(DateTimeOffset.UtcNow))
            {
                await context.Out.WriteLineAsync("The pool is paused. Tasks already started finish; no new task starts. Run 'autopilot pool resume' to continue.").ConfigureAwait(false);
            }
            else
            {
                var pause = state.CurrentPause();
                await context.Out.WriteLineAsync($"The pool is already paused: {pause?.Reason}. Run 'autopilot pool resume' to lift it.").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Resume(ProjectOptions options, CliContext context)
    {
        var command = new Command("resume", "Lift the pause of the pool (after an infrastructure failure or a used-up budget).");
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            await context.Out.WriteLineAsync(new PoolStateStore(workspace, project.Key).Resume() ? "The pool is no longer paused." : "The pool was not paused.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---------- decisions ----------

    private static Command Decisions(ProjectOptions options, CliContext context)
    {
        var json = Json();
        var command = new Command("decisions", "What the pool master decided, newest first.") { json };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var decisions = new PoolStateStore(workspace, project.Key).Decisions();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(decisions, JsonOut)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (decisions.Count == 0)
            {
                await context.Out.WriteLineAsync("The pool master has not decided anything yet.").ConfigureAwait(false);
            }

            foreach (var d in decisions)
            {
                await context.Out.WriteLineAsync($"{d.Id,4}  task {d.WorkItemId,-6} {d.Kind,-8} {(d.RevertedAt is null ? string.Empty : "[undone] ")}{d.Answer}  (why: {d.Reason})").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });

        var id = new Argument<long>("decision-id") { Description = "Id from 'pool decisions'." };
        var revert = new Command("revert", "Undo a decision of the master: its answer is taken out of the task and the task goes back into the pool.") { id };
        revert.SetAction(async (parse, ct) =>
        {
            var target = options.Resolve(parse);
            var (project, workspace) = target.RequireRegistered();
            var state = new PoolStateStore(workspace, project.Key);
            var decision = state.Decision(parse.GetRequiredValue(id));
            if (decision is null || decision.RevertedAt is not null)
            {
                await context.Error.WriteLineAsync("There is no such decision, or it was already undone.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            if (decision.Kind == DecisionKinds.Answer && target.LocalTasks is { } tasks && LocalTaskService.TryParseNumber(decision.WorkItemId, out var number) && tasks.Get(number) is { } task)
            {
                tasks.Update(number, null, MasterGuard.RetractAnswers(task.Body), "person");
            }

            state.Revert(decision.Id, DateTimeOffset.UtcNow);
            state.Unpark(decision.WorkItemId);
            new PoolService(workspace, project.Key).Add(decision.WorkItemId);
            await context.Out.WriteLineAsync($"Undone. Task {decision.WorkItemId} is back in the pool; triage it again before the next run.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(revert);
        return command;
    }

    // ---------- parked ----------

    private static Command Parked(ProjectOptions options, CliContext context)
    {
        var json = Json();
        var command = new Command("parked", "Tasks the pool left for a person, with the reason.") { json };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var parked = new PoolStateStore(workspace, project.Key).Parked();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(parked, JsonOut)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (parked.Count == 0)
            {
                await context.Out.WriteLineAsync("Nothing is parked.").ConfigureAwait(false);
            }

            foreach (var p in parked)
            {
                await context.Out.WriteLineAsync($"{p.WorkItemId,-8} {p.Category ?? "-",-12} {p.Reason}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Unpark(ProjectOptions options, CliContext context)
    {
        var id = new Argument<string>("id") { Description = "Work item id." };
        var command = new Command("unpark", "Take a task off the parked list and put it back at the end of the pool.") { id };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var taskId = parse.GetRequiredValue(id);
            var removed = new PoolStateStore(workspace, project.Key).Unpark(taskId);
            if (removed)
            {
                new PoolService(workspace, project.Key).Add(taskId);
            }

            await context.Out.WriteLineAsync(removed ? $"{taskId} is back in the pool." : $"{taskId} was not parked.").ConfigureAwait(false);
            return removed ? ExitCodes.Ok : ExitCodes.Failure;
        });
        return command;
    }

    // ---------- integration ----------

    private static Command IntegrationCommand(ProjectOptions options, CliContext context)
    {
        var json = Json();
        var command = new Command("integration", "The integration branch: where task PRs go. Shows the branch and what the pool merged into it.") { json };
        command.SetAction(async (parse, ct) =>
        {
            var service = await ServiceAsync(options, parse, ct).ConfigureAwait(false);
            var info = service.Info();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(info, JsonOut)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (!info.Enabled)
            {
                await context.Out.WriteLineAsync("Integration is switched off: task PRs go straight to the base branch.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync(info.Branch is null
                ? $"No integration branch yet. The next pool run creates one from {info.BaseBranch}."
                : $"Branch {info.Branch} (final PR into {info.BaseBranch}), {info.Merges.Count} task(s) merged." + (info.FinalPrUrl is null ? string.Empty : $" Final PR: {info.FinalPrUrl}")).ConfigureAwait(false);
            foreach (var m in info.Merges)
            {
                await context.Out.WriteLineAsync($"  task {m.WorkItemId} ({m.TaskBranch}) at {m.MergedAt.ToLocalTime():yyyy-MM-dd HH:mm}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });

        var pr = new Command("pr", "Push the integration branch and open the final PR into the base branch as a draft (needs the gh program). A person marks it ready and merges it.");
        pr.SetAction(async (parse, ct) =>
        {
            var service = await ServiceAsync(options, parse, ct).ConfigureAwait(false);
            try
            {
                var url = await service.OpenFinalPrAsync(null, ct).ConfigureAwait(false);
                await context.Out.WriteLineAsync($"Draft PR: {url}").ConfigureAwait(false);
                return ExitCodes.Ok;
            }
            catch (GitException ex)
            {
                await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Failure;
            }
        });
        command.Subcommands.Add(pr);

        var fresh = new Command("new", "Forget the current integration branch (it stays in git); the next pool run starts a new one from the base branch.");
        fresh.SetAction(async (parse, ct) =>
        {
            var service = await ServiceAsync(options, parse, ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync(service.StartNew() ? "The next pool run starts a new integration branch." : "There was no integration branch.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(fresh);
        return command;
    }

    private static async Task<IntegrationService> ServiceAsync(ProjectOptions options, ParseResult parse, CancellationToken ct)
    {
        var target = options.Resolve(parse);
        var (project, workspace) = target.RequireRegistered();
        var config = ConfigLoader.Load(target.RepoRoot, target.DataFolder);
        var repoRoot = await GitClient.FindRepositoryRootAsync(target.RepoRoot, ct).ConfigureAwait(false) ?? target.RepoRoot;
        return new IntegrationService(workspace, project.Key, new GitClient(repoRoot), config, GitClient.DefaultWorktreeRoot(repoRoot));
    }
}
