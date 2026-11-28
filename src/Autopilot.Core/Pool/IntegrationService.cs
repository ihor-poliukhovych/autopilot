using System.Globalization;
using System.Text;
using Autopilot.Core.Config;
using Autopilot.Core.Git;
using Autopilot.Core.Process;
using Autopilot.Core.Workspace;

namespace Autopilot.Core.Pool;

public sealed record IntegrationMerge(string WorkItemId, string TaskBranch, string Commit, DateTimeOffset MergedAt);

public sealed record IntegrationInfo(bool Enabled, string? Branch, string BaseBranch, IReadOnlyList<IntegrationMerge> Merges, string? FinalPrUrl);

/// <summary>
/// The integration branch: task branches start from it and are merged into it after build and tests pass, so
/// the next task builds on the last one while a person has merged nothing. It never touches the base branch.
/// A person opens, reviews and merges the one final PR from the integration branch to the base branch.
/// </summary>
public sealed class IntegrationService(
    Workspace.Workspace workspace,
    string projectKey,
    GitClient git,
    AutopilotConfig config,
    string worktreeRoot,
    Func<string, CancellationToken, Task<(bool Ok, string Message)>>? verify = null)
{
    public const string WorktreeName = "@integration";

    private readonly SemaphoreSlim _mergeGate = new(1, 1);

    private static readonly string[] NeverMergedInto = ["main", "master"];

    private readonly Func<string, CancellationToken, Task<(bool Ok, string Message)>> _verify = verify ?? ((path, ct) => BuildAndTestAsync(config, path, ct));

    public bool Enabled => config.Pool.Integration.Enabled;

    public string BaseBranch => string.IsNullOrWhiteSpace(config.Pool.Integration.BaseBranch) ? config.BaseBranch : config.Pool.Integration.BaseBranch!;

    /// <summary>The branch name for a new integration branch: the configured one, with <c>{date}</c> filled in, or <c>integration/{date}</c>.</summary>
    public string NewBranchName(DateTimeOffset now)
    {
        var template = string.IsNullOrWhiteSpace(config.Pool.Integration.Branch) ? IntegrationConfig.BranchPrefix + "{date}" : config.Pool.Integration.Branch!.Trim();
        return template.Replace("{date}", now.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>The stored branch of this project, or null before the first pool run.</summary>
    public string? CurrentBranch()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => r.GetString(0), "SELECT branch FROM pool_integration WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
    }

    /// <summary>
    /// Makes sure the integration branch exists, creating it from the base branch the first time, and returns its name.
    /// Returns null when integration is switched off.
    /// </summary>
    public async Task<string?> EnsureBranchAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        var branch = CurrentBranch() ?? NewBranchName(now);
        Guard(branch);
        if (!await git.BranchExistsAsync(branch, ct).ConfigureAwait(false))
        {
            var baseRef = await git.ResolveBranchAsync(BaseBranch, ct).ConfigureAwait(false)
                ?? throw new GitException($"The base branch \"{BaseBranch}\" does not exist, so the integration branch cannot be created from it.");
            await git.CreateBranchAsync(branch, baseRef.Commit, ct).ConfigureAwait(false);
            await PushNewBranchAsync(branch, ct).ConfigureAwait(false);
        }

        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR IGNORE INTO pool_integration (project_key, branch, base_branch, created_at) VALUES ($k, $b, $base, $t)",
            ("$k", projectKey), ("$b", branch), ("$base", BaseBranch), ("$t", Db.Stamp(now)));
        return branch;
    }

    /// <summary>
    /// Pushes a new integration branch and sets its upstream, so the first task pull request can target it (a tracker
    /// answers that the target branch does not exist otherwise). Best effort: with no remote, or when the push
    /// fails, the pool goes on; the final PR step pushes again and reports a failure.
    /// </summary>
    private async Task PushNewBranchAsync(string branch, CancellationToken ct)
    {
        var remotes = await git.ListRemotesAsync(ct).ConfigureAwait(false);
        var remote = remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes.FirstOrDefault();
        if (remote is not null)
        {
            await GitClient.RunAsync(git.RepositoryPath, ["push", "--no-verify", "--set-upstream", remote, $"refs/heads/{branch}:refs/heads/{branch}"], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merges a task branch into the integration branch with a merge commit, then builds and tests the result.
    /// When the merge conflicts or the build or tests fail, the integration branch is put back as it was.
    /// </summary>
    public async Task<IntegrationResult> MergeAsync(string workItemId, string taskBranch, CancellationToken ct, string? checkLogPath = null)
    {
        var branch = CurrentBranch() ?? throw new GitException("There is no integration branch yet.");
        Guard(branch);
        if (string.Equals(taskBranch, branch, StringComparison.Ordinal))
        {
            return new IntegrationResult(false, "The task branch is the integration branch itself.");
        }

        // All merges share one worktree and one branch: with two agents, the second waits for the first.
        await _mergeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MergeLockedAsync(workItemId, taskBranch, branch, checkLogPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _mergeGate.Release();
        }
    }

    private async Task<IntegrationResult> MergeLockedAsync(string workItemId, string taskBranch, string branch, string? checkLogPath, CancellationToken ct)
    {
        var worktree = Path.Combine(worktreeRoot, WorktreeName);
        await RemoveAsync(worktree).ConfigureAwait(false);
        await git.AddBranchWorktreeAsync(worktree, branch, ct).ConfigureAwait(false);
        try
        {
            var before = (await GitClient.RunAsync(worktree, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).StandardOutput.Trim();
            var head = await git.MergeNoFastForwardAsync(worktree, taskBranch, $"Merge task {workItemId} ({taskBranch})", ct).ConfigureAwait(false);
            if (head is null)
            {
                return new IntegrationResult(false, $"Task {workItemId} conflicts with what is already in {branch}. A person has to resolve it.");
            }

            var (ok, message) = await _verify(worktree, ct).ConfigureAwait(false);
            if (!ok)
            {
                // A transient environment error must not undo good work: check once more on a fresh checkout of the merged result.
                (bool Ok, string Message) retry;
                try
                {
                    retry = await VerifyFreshAsync(head, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await git.ResetHardAsync(worktree, before, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
                {
                    // The fresh checkout could not be set up: the merge still must not stay in the branch.
                    retry = (false, $"The retry on a fresh checkout could not run: {ex.Message}");
                }

                if (!retry.Ok)
                {
                    SaveCheckLog(checkLogPath, message, retry.Message);
                    await git.ResetHardAsync(worktree, before, ct).ConfigureAwait(false);
                    var errors = FirstErrorLines(message);
                    return new IntegrationResult(false, $"Build or tests failed after merging task {workItemId} into {branch}; the merge was undone. {(errors.Length > 0 ? errors : message)}");
                }
            }

            using var connection = workspace.Open();
            Db.Execute(connection, "INSERT INTO pool_integration_merges (project_key, branch, work_item_id, task_branch, commit_sha, merged_at) VALUES ($k, $b, $i, $t, $c, $at)",
                ("$k", projectKey), ("$b", branch), ("$i", workItemId), ("$t", taskBranch), ("$c", head), ("$at", Db.Stamp(DateTimeOffset.UtcNow)));
            return new IntegrationResult(true, $"Merged into {branch}.");
        }
        finally
        {
            await RemoveAsync(worktree).ConfigureAwait(false);
        }
    }

    private async Task<(bool Ok, string Message)> VerifyFreshAsync(string commit, CancellationToken ct)
    {
        var fresh = Path.Combine(worktreeRoot, WorktreeName + "-check");
        await RemoveAsync(fresh).ConfigureAwait(false);
        await git.AddDetachedWorktreeAsync(fresh, commit, ct).ConfigureAwait(false);
        try
        {
            return await _verify(fresh, ct).ConfigureAwait(false);
        }
        finally
        {
            await RemoveAsync(fresh).ConfigureAwait(false);
        }
    }

    private static void SaveCheckLog(string? path, string first, string retry)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, $"=== Check after the merge ===\n{first}\n\n=== Retry on a fresh checkout ===\n{retry}\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The first lines of the output that mention an error, joined for a short reason.</summary>
    internal static string FirstErrorLines(string output) =>
        string.Join(" | ", output.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) && !l.Contains("Error(s)", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(5));

    public IntegrationInfo Info()
    {
        using var connection = workspace.Open();
        var row = Db.Query(connection, r => (Branch: r.GetString(0), Url: Db.StringOrNull(r, 1)),
            "SELECT branch, final_pr_url FROM pool_integration WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
        if (row.Branch is null)
        {
            return new IntegrationInfo(Enabled, null, BaseBranch, [], null);
        }

        var merges = Db.Query(connection, r => new IntegrationMerge(r.GetString(0), r.GetString(1), r.GetString(2), Db.ParseStamp(r.GetString(3))),
            "SELECT work_item_id, task_branch, commit_sha, merged_at FROM pool_integration_merges WHERE project_key = $k AND branch = $b ORDER BY id",
            ("$k", projectKey), ("$b", row.Branch));
        return new IntegrationInfo(Enabled, row.Branch, BaseBranch, merges, row.Url);
    }

    /// <summary>Forgets the stored branch so the next pool run starts a new one. The git branch stays.</summary>
    public bool StartNew()
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_integration WHERE project_key = $k", ("$k", projectKey)) > 0;
    }

    public void RecordFinalPr(string url)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "UPDATE pool_integration SET final_pr_url = $u WHERE project_key = $k", ("$u", url), ("$k", projectKey));
    }

    /// <summary>The title and text of the final PR: what the pool merged, task by task.</summary>
    public (string Title, string Body) FinalPrText()
    {
        var info = Info();
        var title = $"Pool work: {info.Merges.Count} task(s) from {info.Branch}";
        var body = new StringBuilder()
            .AppendLine("## What and why")
            .AppendLine()
            .AppendLine($"The pool merged these task branches into `{info.Branch}`, each after build and tests passed:")
            .AppendLine();
        foreach (var merge in info.Merges)
        {
            body.AppendLine($"- task {merge.WorkItemId} (`{merge.TaskBranch}`)");
        }

        body.AppendLine().AppendLine("## How it was checked").AppendLine()
            .AppendLine("Build and tests ran on the integration branch after every merge. Review the whole diff before merging.");
        return (title, body.ToString());
    }

    /// <summary>
    /// Pushes the integration branch and opens the final PR as a draft with <c>gh</c>. It never marks the PR ready
    /// and never merges it.
    /// </summary>
    public async Task<string> OpenFinalPrAsync(Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run, CancellationToken ct)
    {
        var info = Info();
        if (info.Branch is null || info.Merges.Count == 0)
        {
            throw new GitException("The integration branch has nothing merged yet, so there is no final PR to open.");
        }

        Guard(info.Branch);
        run ??= (file, args, token) => ProcessRunner.RunAsync(file, args, git.RepositoryPath, TimeSpan.FromMinutes(2), null, token);
        var push = await run("git", ["push", "--set-upstream", "origin", info.Branch], ct).ConfigureAwait(false);
        if (!push.Succeeded)
        {
            throw new GitException($"git push failed: {push.StandardError.Trim()}");
        }

        var (title, body) = FinalPrText();
        var create = await run("gh", ["pr", "create", "--draft", "--base", info.BaseBranch, "--head", info.Branch, "--title", title, "--body", body], ct).ConfigureAwait(false);
        if (!create.Succeeded)
        {
            throw new GitException($"gh pr create failed: {create.StandardError.Trim()}");
        }

        var url = create.StandardOutput.Trim().Split('\n').LastOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))?.Trim() ?? create.StandardOutput.Trim();
        RecordFinalPr(url);
        return url;
    }

    /// <summary>The pool refuses to merge into the base branch or into main or master, whatever the settings say.</summary>
    private void Guard(string branch)
    {
        if (!branch.StartsWith(IntegrationConfig.BranchPrefix, StringComparison.Ordinal) || IsProtected(branch))
        {
            throw new GitException($"\"{branch}\" cannot be the integration branch. It must look like integration/<name> and never be the base branch.");
        }
    }

    private bool IsProtected(string branch) =>
        string.Equals(branch, BaseBranch, StringComparison.OrdinalIgnoreCase)
        || string.Equals(branch, config.BaseBranch, StringComparison.OrdinalIgnoreCase)
        || NeverMergedInto.Contains(branch, StringComparer.OrdinalIgnoreCase);

    private async Task RemoveAsync(string worktree)
    {
        if (Directory.Exists(worktree))
        {
            await git.RemoveWorktreeAsync(worktree).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Ok, string Message)> BuildAndTestAsync(AutopilotConfig config, string path, CancellationToken ct)
    {
        foreach (var (name, command) in new[] { ("build", config.Commands.Build), ("test", config.Commands.Test) })
        {
            var result = await ShellRunner.RunAsync(command, path, TimeSpan.FromMinutes(30), null, ct, config.PassEnvironment).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return (false, $"The {name} command failed (exit code {result.ExitCode}).\n{result.StandardOutput}\n{result.StandardError}".Trim());
            }
        }

        return (true, string.Empty);
    }
}
