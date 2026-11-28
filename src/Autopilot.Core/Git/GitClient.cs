using System.Security.Cryptography;
using System.Text;
using Autopilot.Core.Gate;
using Autopilot.Core.Process;

namespace Autopilot.Core.Git;

/// <summary>The name and email the harness commits under.</summary>
public sealed record GitIdentity(string Name, string Email)
{
    public static GitIdentity Default { get; } = new("Autopilot", "autopilot@localhost");
}

/// <summary>Thin wrapper over the git command line for one repository.</summary>
public sealed class GitClient
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    private const string HeadsPrefix = "refs/heads/";
    private const string RemotesPrefix = "refs/remotes/";

    public GitClient(string repositoryPath, GitIdentity? identity = null)
    {
        RepositoryPath = Path.GetFullPath(repositoryPath);
        Identity = identity ?? GitIdentity.Default;
    }

    /// <summary>Any folder inside the repository; commands run from here.</summary>
    public string RepositoryPath { get; }

    public GitIdentity Identity { get; }

    /// <summary>Returns the top-level folder of the repository that contains <paramref name="path"/>, or null.</summary>
    public static async Task<string?> FindRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var result = await RunGitAsync(path, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        return result.Succeeded ? Path.GetFullPath(result.StandardOutput.Trim()) : null;
    }

    /// <summary>
    /// Default folder for this repository's worktrees. It is outside the project on purpose: Claude Code
    /// reads CLAUDE.md files from parent folders, and the agent must not see the project's own ones.
    /// </summary>
    public static string DefaultWorktreeRoot(string repositoryRoot)
    {
        var full = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name))
        {
            name = "repo";
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "autopilot",
            "worktrees",
            $"{name}-{hash}");
    }

    public async Task<string> GetRepositoryRootAsync(CancellationToken ct = default) =>
        Path.GetFullPath((await RunCheckedAsync(RepositoryPath, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim());

    /// <summary>Full commit hash of a revision (branch, tag, HEAD, hash).</summary>
    public async Task<string> GetCommitAsync(string revision, CancellationToken ct = default) =>
        (await RunCheckedAsync(RepositoryPath, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct).ConfigureAwait(false)).Trim();

    public async Task<string?> TryGetCommitAsync(string revision, CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public async Task<bool> BranchExistsAsync(string branch, CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Finds a branch by name: the local branch <c>refs/heads/&lt;name&gt;</c> first, then a remote-tracking
    /// branch <c>refs/remotes/&lt;remote&gt;/&lt;name&gt;</c> (origin first, then the other remotes by name),
    /// then any other revision git understands (for example "origin/main" or a commit hash).
    /// Returns null when nothing matches.
    /// </summary>
    public async Task<ResolvedBranch?> ResolveBranchAsync(string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var name = branch.Trim();
        if (name.StartsWith(HeadsPrefix, StringComparison.Ordinal))
        {
            name = name[HeadsPrefix.Length..];
        }

        var local = await TryGetCommitAsync(HeadsPrefix + name, ct).ConfigureAwait(false);
        if (local is not null)
        {
            return new ResolvedBranch(name, HeadsPrefix + name, local);
        }

        var remotes = await ListRemotesAsync(ct).ConfigureAwait(false);
        foreach (var remote in remotes.OrderBy(r => r == "origin" ? 0 : 1).ThenBy(r => r, StringComparer.Ordinal))
        {
            var remoteRef = $"{RemotesPrefix}{remote}/{name}";
            var commit = await TryGetCommitAsync(remoteRef, ct).ConfigureAwait(false);
            if (commit is not null)
            {
                return new ResolvedBranch(name, remoteRef, commit);
            }
        }

        var other = await TryGetCommitAsync(name, ct).ConfigureAwait(false);
        if (other is null)
        {
            return null;
        }

        var symbolic = await RunGitAsync(RepositoryPath, ["rev-parse", "--verify", "--quiet", "--symbolic-full-name", name], ct).ConfigureAwait(false);
        var fullName = symbolic.Succeeded ? symbolic.StandardOutput.Trim() : string.Empty;
        return new ResolvedBranch(name, fullName.Length > 0 ? fullName : name, other);
    }

    /// <summary>
    /// How many commits <c>origin/&lt;branch&gt;</c> has that the local branch lacks, from the refs already
    /// fetched (nothing is fetched). Null when there is no local branch or no origin ref; 0 when not behind. Never throws.
    /// </summary>
    public async Task<int?> GetCommitsBehindOriginAsync(string branch, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branch))
            {
                return null;
            }

            var name = branch.Trim();
            var local = await TryGetCommitAsync(HeadsPrefix + name, ct).ConfigureAwait(false);
            var remote = await TryGetCommitAsync($"{RemotesPrefix}origin/{name}", ct).ConfigureAwait(false);
            if (local is null || remote is null)
            {
                return null;
            }

            var result = await RunGitAsync(RepositoryPath, ["rev-list", "--count", $"{HeadsPrefix}{name}..{RemotesPrefix}origin/{name}"], ct).ConfigureAwait(false);
            return result.Succeeded && int.TryParse(result.StandardOutput.Trim(), out var count) ? count : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Names of the configured remotes (<c>git remote</c>).</summary>
    public async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["remote"], ct).ConfigureAwait(false);
        return result.Succeeded
            ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>
    /// Files at <paramref name="path"/> in a commit: the file itself, or every file under a folder, as
    /// repository-relative paths with forward slashes, sorted. Empty when the path is not in the commit.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListFilesAtAsync(string commit, string path, CancellationToken ct = default)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var output = await RunCheckedAsync(RepositoryPath, ["-c", "core.quotepath=false", "ls-tree", "-r", "-z", "--full-tree", "--name-only", commit, "--", normalized], ct).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim('\r', '\n'))
            .Where(f => f.Length > 0 && (f == normalized || f.StartsWith(normalized + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The text of one file in a commit (<c>git cat-file blob &lt;commit&gt;:&lt;path&gt;</c>).</summary>
    public Task<string> ReadFileAtAsync(string commit, string path, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["cat-file", "blob", $"{commit}:{path.Replace('\\', '/')}"], ct);

    /// <summary>
    /// On Windows, runs <c>git config --local core.longpaths true</c> in the repository so later git commands in
    /// its worktrees (for example <c>git add --all</c>) do not fail with "Filename too long". Local config only,
    /// never global or system. Setting it again is harmless. Does nothing on other platforms.
    /// Returns true when the setting was written.
    /// </summary>
    public Task<bool> EnsureLongPathsAsync(CancellationToken ct = default) =>
        EnsureLongPathsAsync(OperatingSystem.IsWindows(), ct);

    /// <summary>Same as <see cref="EnsureLongPathsAsync(CancellationToken)"/> with the platform given (for tests).</summary>
    public async Task<bool> EnsureLongPathsAsync(bool isWindows, CancellationToken ct = default)
    {
        if (!isWindows)
        {
            return false;
        }

        await RunCheckedAsync(RepositoryPath, ["config", "--local", "core.longpaths", "true"], ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Runs <c>git worktree add -b &lt;branch&gt; &lt;path&gt; &lt;base&gt;</c>.</summary>
    public async Task AddWorktreeAsync(string path, string newBranch, string baseRevision, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await EnsureLongPathsAsync(ct).ConfigureAwait(false);
        await RunCheckedAsync(RepositoryPath, ["-c", "core.longpaths=true", "worktree", "add", "-b", newBranch, path, baseRevision], ct).ConfigureAwait(false);
    }

    /// <summary>Creates a local branch at a revision without checking it out.</summary>
    public Task CreateBranchAsync(string branch, string startPoint, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["branch", branch, startPoint], ct);

    /// <summary>Renames a local branch (used to keep a failed attempt before the next one).</summary>
    public Task RenameBranchAsync(string from, string to, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["branch", "-m", from, to], ct);

    /// <summary>Force-deletes a local branch. The branch must not be checked out in a worktree.</summary>
    public Task DeleteBranchAsync(string branch, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["branch", "-D", branch], ct);

    /// <summary>True when every commit of <paramref name="revision"/> is already in <paramref name="descendant"/>, so deleting the first loses nothing.</summary>
    public async Task<bool> IsAncestorAsync(string revision, string descendant, CancellationToken ct = default) =>
        (await RunGitAsync(RepositoryPath, ["merge-base", "--is-ancestor", revision, descendant], ct).ConfigureAwait(false)).Succeeded;

    /// <summary>The folders of the worktrees that have <paramref name="branch"/> checked out (normally none or one).</summary>
    public async Task<IReadOnlyList<string>> WorktreesOfBranchAsync(string branch, CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        var found = new List<string>();
        if (!result.Succeeded)
        {
            return found;
        }

        string? path = null;
        foreach (var line in result.StandardOutput.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal) && path is not null
                && string.Equals(line["branch ".Length..].Trim(), HeadsPrefix + branch, StringComparison.Ordinal)
                && !string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), Path.GetFullPath(RepositoryPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }

        return found;
    }

    /// <summary>Checks out an existing branch into a new worktree. The branch must not be checked out anywhere else.</summary>
    public async Task AddBranchWorktreeAsync(string path, string branch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await EnsureLongPathsAsync(ct).ConfigureAwait(false);
        await RunCheckedAsync(RepositoryPath, ["-c", "core.longpaths=true", "worktree", "add", path, branch], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges <paramref name="branch"/> into the branch checked out in <paramref name="worktreePath"/> with a merge commit.
    /// Returns the new head, or null (after aborting) when the merge conflicts.
    /// </summary>
    public async Task<string?> MergeNoFastForwardAsync(string worktreePath, string branch, string message, CancellationToken ct = default)
    {
        var result = await RunGitAsync(worktreePath,
            ["-c", "user.name=" + Identity.Name, "-c", "user.email=" + Identity.Email, "-c", "commit.gpgsign=false", "merge", "--no-ff", "--no-edit", "-m", message, branch], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await RunGitAsync(worktreePath, ["merge", "--abort"], ct).ConfigureAwait(false);
            return null;
        }

        return (await RunCheckedAsync(worktreePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
    }

    /// <summary>Moves the checked-out branch of a worktree back to a commit, dropping what came after.</summary>
    public Task ResetHardAsync(string worktreePath, string commit, CancellationToken ct = default) =>
        RunCheckedAsync(worktreePath, ["reset", "--hard", commit], ct);

    /// <summary>Runs git in any folder and returns the result without throwing on a non-zero exit code.</summary>
    public static Task<ProcessResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
        RunGitAsync(workingDirectory, arguments, ct);

    /// <summary>Checks out a commit into a new worktree without creating a branch.</summary>
    public async Task AddDetachedWorktreeAsync(string path, string revision, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await EnsureLongPathsAsync(ct).ConfigureAwait(false);
        await RunCheckedAsync(RepositoryPath, ["-c", "core.longpaths=true", "worktree", "add", "--detach", path, revision], ct).ConfigureAwait(false);
    }

    /// <summary>Removes a worktree (even with local changes), deletes its folder and prunes git's records.</summary>
    public async Task RemoveWorktreeAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        await RunGitAsync(RepositoryPath, ["worktree", "remove", "--force", "--force", full], ct).ConfigureAwait(false);
        if (Directory.Exists(full))
        {
            await DeleteDirectoryAsync(full).ConfigureAwait(false);
        }

        await RunGitAsync(RepositoryPath, ["worktree", "prune"], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages everything in the worktree and commits it under <see cref="Identity"/>.
    /// Returns the new commit hash, or null when there was nothing to commit.
    /// </summary>
    public async Task<string?> CommitAllAsync(string worktreePath, string message, CancellationToken ct = default)
    {
        await RunCheckedAsync(worktreePath, ["add", "--all"], ct).ConfigureAwait(false);
        var status = await RunCheckedAsync(worktreePath, ["status", "--porcelain"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        await RunCheckedAsync(
            worktreePath,
            [
                "-c", "user.name=" + Identity.Name,
                "-c", "user.email=" + Identity.Email,
                "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-edit", "-m", message,
            ],
            ct,
            new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = Identity.Name,
                ["GIT_AUTHOR_EMAIL"] = Identity.Email,
                ["GIT_COMMITTER_NAME"] = Identity.Name,
                ["GIT_COMMITTER_EMAIL"] = Identity.Email,
            }).ConfigureAwait(false);

        return (await RunCheckedAsync(worktreePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
    }

    /// <summary>Unified diff between two revisions, with rename detection.</summary>
    public Task<string> GetDiffAsync(string baseRevision, string headRevision, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-M", baseRevision, headRevision], ct);

    /// <summary>Every changed file between two revisions with added and removed line counts.</summary>
    public async Task<IReadOnlyList<FileChange>> GetChangesAsync(string baseRevision, string headRevision, CancellationToken ct = default)
    {
        var numstat = await RunCheckedAsync(RepositoryPath, ["diff", "--no-color", "--no-ext-diff", "--numstat", "-M", "-z", baseRevision, headRevision], ct).ConfigureAwait(false);
        var nameStatus = await RunCheckedAsync(RepositoryPath, ["diff", "--no-color", "--no-ext-diff", "--name-status", "-M", "-z", baseRevision, headRevision], ct).ConfigureAwait(false);
        return ParseChanges(numstat, nameStatus);
    }

    /// <summary>Joins <c>--numstat -z</c> and <c>--name-status -z</c> output into file changes.</summary>
    public static IReadOnlyList<FileChange> ParseChanges(string numstatZ, string nameStatusZ)
    {
        var lines = new Dictionary<string, (int Added, int Removed, bool Binary)>(StringComparer.Ordinal);
        var tokens = numstatZ.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].TrimStart('\n', '\r');
            if (token.Length == 0)
            {
                continue;
            }

            var parts = token.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var binary = parts[0] == "-" || parts[1] == "-"; // numstat shows "-" for binary files
            var added = int.TryParse(parts[0], out var a) ? a : 0;
            var removed = int.TryParse(parts[1], out var r) ? r : 0;
            string path;
            if (parts[2].Length == 0 && i + 2 < tokens.Length)
            {
                // Rename or copy: an empty path, then the old and the new path.
                path = tokens[i + 2];
                i += 2;
            }
            else
            {
                path = parts[2];
            }

            lines[path] = (added, removed, binary);
        }

        var changes = new List<FileChange>();
        var items = nameStatusZ.Split('\0');
        for (var i = 0; i < items.Length; i++)
        {
            var status = items[i].Trim('\n', '\r');
            if (status.Length == 0)
            {
                continue;
            }

            var letter = status[0];
            string path;
            string? oldPath = null;
            if (letter is 'R' or 'C')
            {
                if (i + 2 >= items.Length)
                {
                    break;
                }

                oldPath = items[i + 1];
                path = items[i + 2];
                i += 2;
            }
            else
            {
                if (i + 1 >= items.Length)
                {
                    break;
                }

                path = items[i + 1];
                i += 1;
            }

            var kind = letter switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Added,
                _ => FileChangeKind.Modified,
            };
            if (letter == 'C')
            {
                oldPath = null;
            }

            lines.TryGetValue(path, out var counts);
            changes.Add(new FileChange(path, kind, counts.Added, counts.Removed, oldPath, counts.Binary));
        }

        return changes;
    }

    private async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await RunGitAsync(workingDirectory, arguments, ct, environment).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new GitException(
                $"git {string.Join(' ', arguments)} failed (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                env[key] = value;
            }
        }

        return ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeout, env, ct);
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// A branch found by <see cref="GitClient.ResolveBranchAsync"/>: the name asked for, the full ref it
/// resolved to (<c>refs/heads/main</c> or <c>refs/remotes/origin/main</c>) and its commit.
/// </summary>
public sealed record ResolvedBranch(string Name, string Ref, string Commit)
{
    /// <summary>True when there is no local branch and a remote-tracking branch was used.</summary>
    public bool RemoteOnly => Ref.StartsWith("refs/remotes/", StringComparison.Ordinal);

    /// <summary>The ref without <c>refs/heads/</c> or <c>refs/remotes/</c>, for example "origin/main".</summary>
    public string ShortRef =>
        Ref.StartsWith("refs/remotes/", StringComparison.Ordinal) ? Ref["refs/remotes/".Length..]
        : Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? Ref["refs/heads/".Length..]
        : Ref;

    /// <summary>For a remote-only branch: what is used and the command that creates the local branch; otherwise null.</summary>
    public string? RemoteNote => RemoteOnly
        ? $"branch \"{Name}\" exists only as {ShortRef}; using {ShortRef} ({Commit[..Math.Min(12, Commit.Length)]}). To track it locally: git branch {Name} {ShortRef}"
        : null;
}

/// <summary>A git command failed. The message includes git's own error text.</summary>
public sealed class GitException(string message) : Exception(message);
