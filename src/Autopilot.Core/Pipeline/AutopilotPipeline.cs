using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autopilot.Core.Config;
using Autopilot.Core.Diagnostics;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Prompts;

namespace Autopilot.Core.Pipeline;

/// <summary>Everything the pipeline needs. The CLI builds the real set; tests pass fakes.</summary>
public sealed record AutopilotPipelineOptions
{
    /// <summary>Project root; must be the git repository's top-level folder.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>Folder with the project's tasks and out/. Null means <c>.autopilot/</c> in the project root.</summary>
    public string? DataFolder { get; init; }

    public required AutopilotConfig Config { get; init; }
    public required IWorkItemSource WorkItems { get; init; }
    public required IAgentRunner Runner { get; init; }
    public required IPublisher Publisher { get; init; }
    public required GitClient Git { get; init; }
    public required IGateFactsCollector Facts { get; init; }

    /// <summary>Folder for agent worktrees, outside the project.</summary>
    public required string WorktreeRoot { get; init; }

    /// <summary>The gate. Defaults to <see cref="MergeGate.Evaluate"/>.</summary>
    public Func<GateInput, AutopilotConfig, GateDecision> EvaluateGate { get; init; } = MergeGate.Evaluate;

    /// <summary>Progress lines for a person. Null for silence.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>
    /// The read-only diagnostics MCP server, or null when diagnostics is off. When set, triage and
    /// implementer runs get it as the <c>diagnostics</c> server plus its four tools; the reviewer never does.
    /// </summary>
    public McpServerSpec? DiagnosticsServer { get; init; }

    /// <summary>
    /// Takes the per-task triage lock (the id of the work item is the argument) and returns the handle that releases it.
    /// Throws <see cref="Pool.TaskLockHeldException"/> when a live process already triages that task. Null means no lock.
    /// </summary>
    public Func<string, IDisposable>? AcquireTriageLock { get; init; }
}

/// <summary>
/// Triage, implement, review, gate, publish. The agent never commits, merges, or decides the outcome:
/// the harness commits, the gate decides, and a person merges.
/// </summary>
public sealed partial class AutopilotPipeline(AutopilotPipelineOptions options)
{
    private readonly AutopilotConfig _config = options.Config;
    private readonly GitClient _git = options.Git;
    private readonly TriageStore _triageStore = new(options.ProjectRoot, options.DataFolder);
    private readonly string _projectRoot = Path.GetFullPath(options.ProjectRoot);

    /// <summary>Worktree for triage. '@' cannot appear in an id, so no agent worktree can have this name.</summary>
    public const string TriageWorktreeName = "@triage";

    public async Task<IReadOnlyList<TriageItemResult>> TriageAsync(string? workItemId, CancellationToken ct)
    {
        if (workItemId is not null && !ProjectPaths.IsValidId(workItemId))
        {
            return [new TriageItemResult(workItemId, string.Empty, null, ProjectPaths.InvalidIdMessage(workItemId))];
        }

        IReadOnlyList<WorkItem> items;
        if (workItemId is null)
        {
            items = await options.WorkItems.ListOpenAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var item = await options.WorkItems.GetAsync(workItemId, ct).ConfigureAwait(false);
            if (item is null)
            {
                return [new TriageItemResult(workItemId, string.Empty, null, $"Work item {workItemId} was not found.")];
            }

            items = [item];
        }

        // Every id is checked before any work starts; a bad one is reported and skipped, not fatal.
        var results = items
            .Where(i => !ProjectPaths.IsValidId(i.Id))
            .Select(i => new TriageItemResult(i.Id, i.Title, null, ProjectPaths.InvalidIdMessage(i.Id)))
            .ToList();
        items = items.Where(i => ProjectPaths.IsValidId(i.Id)).ToList();
        if (items.Count == 0)
        {
            return results;
        }

        // One triage per task at a time, across processes (the web UI and the pool): the second one is refused.
        var taskLocks = new List<IDisposable>();
        try
        {
            if (options.AcquireTriageLock is { } acquire)
            {
                var free = new List<WorkItem>();
                foreach (var item in items)
                {
                    try
                    {
                        taskLocks.Add(acquire(item.Id));
                        free.Add(item);
                    }
                    catch (Pool.TaskLockHeldException ex)
                    {
                        results.Add(new TriageItemResult(item.Id, item.Title, null, $"Not triaged: {ex.Message}"));
                    }
                }

                items = free;
                if (items.Count == 0)
                {
                    return results;
                }
            }

            await TriageLockedAsync(workItemId, items, results, ct).ConfigureAwait(false);
            return results;
        }
        finally
        {
            foreach (var taskLock in taskLocks)
            {
                taskLock.Dispose();
            }
        }
    }

    private async Task TriageLockedAsync(string? workItemId, IReadOnlyList<WorkItem> items, List<TriageItemResult> results, CancellationToken ct)
    {
        var baseCommit = await ResolveBaseAsync(ct).ConfigureAwait(false);
        if (baseCommit is null)
        {
            results.AddRange(items.Select(i => new TriageItemResult(i.Id, i.Title, null, BaseMissingMessage)));
            return;
        }

        var context = await LoadContextAsync(baseCommit, ct).ConfigureAwait(false);
        if (context.Missing.Count > 0)
        {
            results.AddRange(items.Select(i => new TriageItemResult(i.Id, i.Title, null, ContextMissingMessage(context))));
            return;
        }

        // Triage reads the code at the base branch, in a worktree outside the project.
        // One item gets its own folder, so two triages of different items (a pool with several agents, a person
        // in the web UI) do not remove each other's worktree.
        var worktree = Path.Combine(options.WorktreeRoot, workItemId is null ? TriageWorktreeName : $"{TriageWorktreeName}-{ProjectPaths.SafeId(workItemId)}");
        await RemoveWorktreeIfPresentAsync(worktree).ConfigureAwait(false);
        await _git.AddDetachedWorktreeAsync(worktree, baseCommit, ct).ConfigureAwait(false);
        try
        {
            foreach (var item in items)
            {
                results.Add(await TriageOneAsync(item, worktree, context, ct).ConfigureAwait(false));
            }
        }
        finally
        {
            await RemoveWorktreeIfPresentAsync(worktree).ConfigureAwait(false);
        }
    }

    public async Task<ImplementResult> ImplementAsync(string workItemId, CancellationToken ct)
    {
        if (!ProjectPaths.IsValidId(workItemId))
        {
            return new ImplementResult(workItemId, ImplementOutcome.Failed, ProjectPaths.InvalidIdMessage(workItemId));
        }

        var item = await options.WorkItems.GetAsync(workItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new ImplementResult(workItemId, ImplementOutcome.NotFound, $"Work item {workItemId} was not found.");
        }

        if (!ProjectPaths.IsValidId(item.Id))
        {
            return new ImplementResult(item.Id, ImplementOutcome.Failed, ProjectPaths.InvalidIdMessage(item.Id));
        }

        var id = item.Id;
        var record = await _triageStore.LoadRecordAsync(id, ct).ConfigureAwait(false);
        var triage = record?.Result;
        if (triage is null)
        {
            return new ImplementResult(id, ImplementOutcome.NotReady, $"Work item {id} has no triage yet. Run 'autopilot triage {id}' first.");
        }

        // Triage decided on one text; an edited work item needs a new decision.
        if (!string.Equals(record!.TextHash, TriageStore.TextHash(item), StringComparison.Ordinal))
        {
            return new ImplementResult(id, ImplementOutcome.NotReady,
                $"The text of work item {id} changed since it was triaged. Run 'autopilot triage {id}' again.");
        }

        if (triage.Label != TriageLabel.AgentReady)
        {
            return new ImplementResult(id, ImplementOutcome.NotReady, $"Triage marked {id} as needs-human: {triage.Reason}");
        }

        var repoRoot = await _git.GetRepositoryRootAsync(ct).ConfigureAwait(false);
        if (!SamePath(repoRoot, _projectRoot))
        {
            return new ImplementResult(id, ImplementOutcome.Failed, $"The project folder must be the git repository root ({repoRoot}).");
        }

        var branch = _config.BranchPrefix + id;
        if (await _git.BranchExistsAsync(branch, ct).ConfigureAwait(false))
        {
            return new ImplementResult(id, ImplementOutcome.Failed,
                $"Branch {branch} already exists from an earlier run. Review it, then delete it with 'git branch -D {branch}' to try again.", branch);
        }

        var baseCommit = await ResolveBaseAsync(ct).ConfigureAwait(false);
        if (baseCommit is null)
        {
            return new ImplementResult(id, ImplementOutcome.Failed, BaseMissingMessage);
        }

        var context = await LoadContextAsync(baseCommit, ct).ConfigureAwait(false);
        if (context.Missing.Count > 0)
        {
            return new ImplementResult(id, ImplementOutcome.Failed, ContextMissingMessage(context));
        }

        var runFolder = ProjectPaths.RunFolder(_projectRoot, id, options.DataFolder);
        Directory.CreateDirectory(runFolder);
        var previousFindings = ReadPreviousFindings(Path.Combine(runFolder, "REJECTED.md"));
        DeleteIfExists(Path.Combine(runFolder, "REJECTED.md"));
        DeleteIfExists(Path.Combine(runFolder, "PULL_REQUEST.md"));
        await File.WriteAllTextAsync(Path.Combine(runFolder, BaseFileName), $"{_config.BaseBranch} {baseCommit}", ct).ConfigureAwait(false);

        var worktree = Path.Combine(options.WorktreeRoot, id);
        await RemoveWorktreeIfPresentAsync(worktree).ConfigureAwait(false);
        var retry = await CreateWorktreeAsync(id, branch, baseCommit, worktree, previousFindings, ct).ConfigureAwait(false);
        try
        {
            return await ImplementInWorktreeAsync(item, triage, branch, baseCommit, worktree, runFolder, context, retry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteRejectedAsync(runFolder, item, branch, $"The run stopped with an error: {ex.Message}", [], null).ConfigureAwait(false);
            return new ImplementResult(id, ImplementOutcome.Failed, ex.Message, branch, runFolder);
        }
        finally
        {
            await RemoveWorktreeIfPresentAsync(worktree).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the worktree and the attempt branch. A retry (pool.retry.continue, a kept attempt branch)
    /// starts from the latest kept branch merged with the base; a conflict falls back to a clean start.
    /// Returns what the prompt needs when the retry continued, else null.
    /// </summary>
    private async Task<PromptLibrary.RetryContinuation?> CreateWorktreeAsync(
        string id, string branch, string baseCommit, string worktree, IReadOnlyList<string> findings, CancellationToken ct)
    {
        if (_config.Pool.Retry.Continue)
        {
            var attempt = 0;
            while (await _git.BranchExistsAsync($"{branch}-attempt-{attempt + 1}", ct).ConfigureAwait(false))
            {
                attempt++;
            }

            if (attempt > 0)
            {
                var kept = $"{branch}-attempt-{attempt}";
                await _git.AddWorktreeAsync(worktree, branch, kept, ct).ConfigureAwait(false);
                var merged = await _git.MergeNoFastForwardAsync(worktree, baseCommit, $"Merge {_config.BaseBranch} into {branch}", ct).ConfigureAwait(false);
                if (merged is not null)
                {
                    Write($"{id}: continued from attempt {attempt} ({kept}), merged with {_config.BaseBranch}");
                    return new PromptLibrary.RetryContinuation(attempt, findings);
                }

                Write($"{id}: could not continue from attempt {attempt} ({kept}): merging {_config.BaseBranch} conflicts. Starting clean instead.");
                await RemoveWorktreeIfPresentAsync(worktree).ConfigureAwait(false);
                await _git.DeleteBranchAsync(branch, ct).ConfigureAwait(false);
            }
        }

        Write($"{id}: creating worktree on {branch} from {_config.BaseBranch}");
        await _git.AddWorktreeAsync(worktree, branch, baseCommit, ct).ConfigureAwait(false);
        return null;
    }

    // The bullet lines under "## Findings" and "## Reviewer findings" of a rejected run's REJECTED.md.
    private static List<string> ReadPreviousFindings(string rejectedFile)
    {
        var found = new List<string>();
        if (!File.Exists(rejectedFile))
        {
            return found;
        }

        var inFindings = false;
        foreach (var line in File.ReadAllLines(rejectedFile))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inFindings = line is "## Findings" or "## Reviewer findings";
            }
            else if (inFindings && line.StartsWith("- ", StringComparison.Ordinal))
            {
                found.Add(line[2..]);
            }
        }

        return found;
    }

    public async Task<RunSummary> RunAsync(int? limit, CancellationToken ct)
    {
        var triage = await TriageAsync(null, ct).ConfigureAwait(false);
        var implemented = new List<ImplementResult>();
        var skipped = new List<string>();
        foreach (var entry in triage.Where(t => t.Triage?.Label == TriageLabel.AgentReady))
        {
            if (limit is { } max && implemented.Count >= max)
            {
                skipped.Add($"{entry.WorkItemId}: not started, the limit of {max} was reached.");
                continue;
            }

            var branch = _config.BranchPrefix + entry.WorkItemId;
            if (await _git.BranchExistsAsync(branch, ct).ConfigureAwait(false))
            {
                skipped.Add($"{entry.WorkItemId}: branch {branch} already exists from an earlier run.");
                continue;
            }

            implemented.Add(await ImplementAsync(entry.WorkItemId, ct).ConfigureAwait(false));
        }

        return new RunSummary(triage, implemented, skipped);
    }

    private async Task<TriageItemResult> TriageOneAsync(WorkItem item, string worktree, ProjectContext context, CancellationToken ct)
    {
        Write($"{item.Id}: triage");
        var prompt = PromptLibrary.TriagePrompt(item, _config.RiskPaths, MergeGate.ProtectedPaths.Concat(ContextPaths.ProtectedPatterns(_config.Context)).ToList(), context);
        var run = await RunAgentAsync(item.Id, AgentRole.Triage, worktree, prompt, [], ct).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return new TriageItemResult(item.Id, item.Title, null, $"The triage agent failed: {run.Error}");
        }

        if (!OutputParser.TryParseTriage(run, out var triage, out var error))
        {
            return new TriageItemResult(item.Id, item.Title, null, $"Could not read the triage answer: {error}");
        }

        await _triageStore.SaveAsync(item, triage, ct).ConfigureAwait(false);
        await options.WorkItems.RecordTriageAsync(item.Id, triage, ct).ConfigureAwait(false);
        return new TriageItemResult(item.Id, item.Title, triage, null);
    }

    private async Task<ImplementResult> ImplementInWorktreeAsync(
        WorkItem item, TriageResult triage, string branch, string baseCommit, string worktree, string runFolder, ProjectContext context, PromptLibrary.RetryContinuation? retry, CancellationToken ct)
    {
        var id = item.Id;
        var writable = _config.WritablePaths;

        // Round 1: implement.
        Write($"{id}: implementer");
        var implement = await RunAgentAsync(id, AgentRole.Implementer, worktree,
            PromptLibrary.ImplementerPrompt(item, triage, writable, _config.Commands, context: context, retry: retry), writable, ct).ConfigureAwait(false);
        if (!implement.Succeeded)
        {
            return await RejectAsync(item, branch, runFolder, ImplementOutcome.Failed, $"The implementer run failed: {implement.Error}").ConfigureAwait(false);
        }

        OutputParser.TryParseSelfReview(implement, out var selfReview, out _);
        var commit = await _git.CommitAllAsync(worktree, CommitMessages.Build(_config.CommitStyle, item, 1), ct).ConfigureAwait(false);
        if (commit is null && retry is null)
        {
            return await RejectAsync(item, branch, runFolder, ImplementOutcome.Rejected, "The implementer made no changes.").ConfigureAwait(false);
        }

        var review = await ReviewAsync(item, triage, baseCommit, branch, worktree, context, ct).ConfigureAwait(false);

        // Round 2: one more implementer round when the reviewer asked for changes.
        if (review is { Verdict: ReviewVerdict.RequestChanges })
        {
            Write($"{id}: reviewer asked for changes, implementer round 2");
            var fix = await RunAgentAsync(id, AgentRole.Implementer, worktree,
                PromptLibrary.ImplementerPrompt(item, triage, writable, _config.Commands, review.Findings, context), writable, ct).ConfigureAwait(false);
            if (fix.Succeeded)
            {
                if (OutputParser.TryParseSelfReview(fix, out var secondSelfReview, out _))
                {
                    selfReview = secondSelfReview;
                }

                await _git.CommitAllAsync(worktree, CommitMessages.Build(_config.CommitStyle, item, 2), ct).ConfigureAwait(false);
                review = await ReviewAsync(item, triage, baseCommit, branch, worktree, context, ct).ConfigureAwait(false);
            }
        }

        // The gate collects its own facts on clean checkouts.
        Write($"{id}: merge gate (build and tests on clean checkouts)");
        var headCommit = await _git.GetCommitAsync(branch, ct).ConfigureAwait(false);
        var evidence = new Evidence(id, triage, selfReview, review);
        var input = await options.Facts.CollectAsync(id, baseCommit, headCommit, evidence, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(runFolder, "gate-log.txt"), input.Tests.Log, ct).ConfigureAwait(false);
        var decision = options.EvaluateGate(input, _config);
        var report = GateReport.Create(input, decision, _config);
        var diff = await _git.GetDiffAsync(baseCommit, headCommit, ct).ConfigureAwait(false);

        if (decision.Outcome != GateOutcome.Passed)
        {
            await File.WriteAllTextAsync(Path.Combine(runFolder, "change.patch"), diff, ct).ConfigureAwait(false);
            await report.WriteAsync(runFolder, ct).ConfigureAwait(false);
            await WriteRejectedAsync(runFolder, item, branch, "The merge gate rejected the change.", decision.Findings, decision, report, review?.Findings).ConfigureAwait(false);
            return new ImplementResult(id, ImplementOutcome.Rejected,
                $"The merge gate rejected the change ({decision.Findings.Count} finding(s)).", branch, runFolder, decision);
        }

        Write($"{id}: gate passed, publishing");
        var published = await options.Publisher.PublishAsync(new PublishRequest(item, branch, diff, decision, evidence, report, CommitMessages.PullRequestTitle(_config.CommitStyle, item)), ct).ConfigureAwait(false);
        if (!published.Succeeded)
        {
            return new ImplementResult(id, ImplementOutcome.Failed, $"The gate passed but publishing failed: {published.Error}", branch, runFolder, decision);
        }

        return new ImplementResult(id, ImplementOutcome.Published, "The gate passed. A draft is waiting for a person.", published.BranchName ?? branch, published.Location, decision);
    }

    private async Task<ReviewResult?> ReviewAsync(WorkItem item, TriageResult triage, string baseCommit, string branch, string worktree, ProjectContext context, CancellationToken ct)
    {
        Write($"{item.Id}: reviewer");
        var diff = await _git.GetDiffAsync(baseCommit, branch, ct).ConfigureAwait(false);
        var run = await RunAgentAsync(item.Id, AgentRole.Reviewer, worktree, PromptLibrary.ReviewerPrompt(item, triage, diff, context), [], ct).ConfigureAwait(false);
        return OutputParser.TryParseReview(run, out var review, out _) ? review : null;
    }

    private async Task<AgentRunResult> RunAgentAsync(
        string id, AgentRole role, string workingDirectory, string prompt, IReadOnlyList<string> writablePaths, CancellationToken ct)
    {
        var runFolder = ProjectPaths.RunFolder(_projectRoot, id, options.DataFolder);
        var number = NextRunNumber(runFolder);
        var roleName = role.ToString().ToLowerInvariant();
        var runDirectory = Path.Combine(runFolder, $"{number}-{roleName}");
        Directory.CreateDirectory(runDirectory);

        var roleConfig = _config.Roles.For(role);
        IReadOnlyDictionary<string, McpServerSpec>? mcpServers = null;
        if (options.DiagnosticsServer is { } diagnostics && DiagnosticsMcp.RoleUsesDiagnostics(role))
        {
            roleConfig = DiagnosticsMcp.WithDiagnosticsTools(roleConfig);
            mcpServers = new Dictionary<string, McpServerSpec> { [DiagnosticsMcp.ServerName] = diagnostics };
        }

        // The implementer may always run the project's own build and test command lines, exactly as configured.
        IReadOnlyList<string>? configuredCommands = null;
        if (role == AgentRole.Implementer)
        {
            configuredCommands = ConfiguredCommands(_config);
            roleConfig = roleConfig with
            {
                AllowedCommands = roleConfig.AllowedCommands.Concat(configuredCommands).Distinct(StringComparer.Ordinal).ToList(),
            };
        }

        var request = new AgentRunRequest(
            role,
            workingDirectory,
            PromptLibrary.SystemPrompt(role),
            prompt,
            roleConfig,
            writablePaths,
            PromptLibrary.OutputSchema(role),
            runDirectory,
            _config.RiskPaths.Concat(ContextPaths.ProtectedPatterns(_config.Context)).Distinct(StringComparer.Ordinal).ToList(),
            mcpServers,
            configuredCommands);

        AgentRunResult result;
        try
        {
            result = await options.Runner.RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new AgentRunResult(false, string.Empty, null, 0, TokenUsage.Zero, TimeSpan.Zero, null, ex.Message);
        }

        await AppendRunLogAsync(runFolder, number, role, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>The configured build and test command lines, trimmed, without empty or repeated ones.</summary>
    public static IReadOnlyList<string> ConfiguredCommands(AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new[] { config.Commands?.Build, config.Commands?.Test }
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string BaseMissingMessage =>
        $"Base branch \"{_config.BaseBranch}\" does not exist, locally or as a remote-tracking branch. Create it, run 'git fetch', or set baseBranch in .autopilot/autopilot.json.";

    /// <summary>File in the run folder with the base branch and the full commit the run was built on.</summary>
    public const string BaseFileName = "base.txt";

    private bool _baseNoteWritten;

    private ProjectContext? _context;

    /// <summary>The base commit: the local branch, else a remote-tracking branch (with a note in the log).</summary>
    private async Task<string?> ResolveBaseAsync(CancellationToken ct)
    {
        var resolved = await _git.ResolveBranchAsync(_config.BaseBranch, ct).ConfigureAwait(false);
        if (resolved?.RemoteNote is { } note && !_baseNoteWritten)
        {
            _baseNoteWritten = true;
            Write("Note: base " + note);
        }

        if (resolved is not null)
        {
            Write($"Base: {_config.BaseBranch} ({resolved.Commit[..Math.Min(12, resolved.Commit.Length)]})");
            if (await _git.GetCommitsBehindOriginAsync(_config.BaseBranch, ct).ConfigureAwait(false) is > 0 and var behind)
            {
                Write($"Warning: {_config.BaseBranch} is {behind} commits behind origin/{_config.BaseBranch}; this run uses the local {_config.BaseBranch}. Run 'git fetch' and update it to build on the newer code.");
            }
        }

        return resolved?.Commit;
    }

    /// <summary>The context files at the base commit, read once per commit.</summary>
    private async Task<ProjectContext> LoadContextAsync(string baseCommit, CancellationToken ct)
    {
        if (_config.Context.Count == 0)
        {
            return ProjectContext.Empty;
        }

        if (_context is { } cached && cached.Commit == baseCommit)
        {
            return cached;
        }

        _context = await ProjectContext.LoadAsync(_git, baseCommit, _config.Context, ct).ConfigureAwait(false);
        if (_context.Files.Count > 0)
        {
            Write($"Project context: {_context.Files.Count} file(s), {_context.TotalChars} characters from {baseCommit[..Math.Min(12, baseCommit.Length)]}{(_context.Truncated ? " (cut at the size limit)" : string.Empty)}");
        }

        return _context;
    }

    private string ContextMissingMessage(ProjectContext context) =>
        $"These \"context\" paths do not exist in the base commit of \"{_config.BaseBranch}\": {string.Join(", ", context.Missing)}. " +
        "Context is read from the base commit, so commit the files there, or fix \"context\" in .autopilot/autopilot.json.";

    private static int NextRunNumber(string runFolder)
    {
        if (!Directory.Exists(runFolder))
        {
            return 1;
        }

        var numbers = Directory.EnumerateDirectories(runFolder)
            .Select(d => RunNumber().Match(Path.GetFileName(d)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value));
        return numbers.DefaultIfEmpty(0).Max() + 1;
    }

    private static async Task AppendRunLogAsync(string runFolder, int number, AgentRole role, AgentRunResult result, CancellationToken ct)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            run = number,
            role = role.ToString().ToLowerInvariant(),
            succeeded = result.Succeeded,
            turns = result.Turns,
            usage = result.Usage,
            durationMs = (long)result.Duration.TotalMilliseconds,
            model = result.Model,
            error = result.Error,
        };
        var line = JsonSerializer.Serialize(entry, RunLogOptions) + "\n";
        await File.AppendAllTextAsync(Path.Combine(runFolder, "runs.jsonl"), line, ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions RunLogOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private async Task<ImplementResult> RejectAsync(WorkItem item, string branch, string runFolder, ImplementOutcome outcome, string reason)
    {
        await WriteRejectedAsync(runFolder, item, branch, reason, [], null).ConfigureAwait(false);
        return new ImplementResult(item.Id, outcome, reason, branch, runFolder);
    }

    private static async Task WriteRejectedAsync(
        string runFolder, WorkItem item, string branch, string reason, IReadOnlyList<GateFinding> findings, GateDecision? decision, GateReport? report = null, IReadOnlyList<string>? reviewerFindings = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Rejected: {item.Id} {item.Title}");
        text.AppendLine();
        text.AppendLine(reason);
        text.AppendLine();
        if (findings.Count > 0)
        {
            text.AppendLine("## Findings");
            text.AppendLine();
            foreach (var finding in findings)
            {
                text.AppendLine($"- [{finding.RuleId}] {finding.Message}{(finding.HighRisk ? " (high risk)" : string.Empty)}");
            }

            text.AppendLine();
        }

        if (reviewerFindings is { Count: > 0 })
        {
            text.AppendLine("## Reviewer findings");
            text.AppendLine();
            foreach (var finding in reviewerFindings)
            {
                text.AppendLine($"- {finding}");
            }

            text.AppendLine();
        }

        if (decision?.HighRisk == true)
        {
            text.AppendLine("The change touches a risk path, so a person must look at it in any case.");
            text.AppendLine();
        }

        if (report is not null)
        {
            text.AppendLine("## Gate facts");
            text.AppendLine();
            text.Append(report.FactsTable());
        }

        text.AppendLine("## What a person can do");
        text.AppendLine();
        text.AppendLine($"- The branch `{branch}` is kept. Look at it with `git log {branch}` and `git diff {branch}~..{branch}`.");
        text.AppendLine("- The agent run logs are in the numbered folders next to this file, and `runs.jsonl` lists every run.");
        text.AppendLine($"- To try again, delete the branch (`git branch -D {branch}`), improve the task file, and run `autopilot implement {item.Id}`.");
        Directory.CreateDirectory(runFolder);
        await File.WriteAllTextAsync(Path.Combine(runFolder, "REJECTED.md"), text.ToString()).ConfigureAwait(false);
    }

    private async Task RemoveWorktreeIfPresentAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await _git.RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd('\\', '/'),
        Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void Write(string line) => options.Log?.WriteLine(line);

    [GeneratedRegex(@"^(\d+)-")]
    private static partial Regex RunNumber();
}
