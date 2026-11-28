using Autopilot.Core.Git;

namespace Autopilot.Core.Tests.Git;

public sealed class GitClientLongPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-longpaths-" + Guid.NewGuid().ToString("N")[..8]);

    public GitClientLongPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup of a temp folder.
        }
    }

    private static async Task<string> GitAsync(string dir, params string[] args)
    {
        var result = await GitClient.RunAsync(dir, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static async Task<string?> ConfigAsync(string dir, params string[] args)
    {
        var result = await GitClient.RunAsync(dir, ["config", .. args]);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<GitClient> CreateRepoAsync()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init", "-q", "-b", "main");
        await GitAsync(repo, "config", "--local", "core.longpaths", "false");
        await GitAsync(repo, "config", "--local", "user.name", "t");
        await GitAsync(repo, "config", "--local", "user.email", "t@example.com");
        await GitAsync(repo, "config", "--local", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
        await GitAsync(repo, "add", "--all");
        await GitAsync(repo, "commit", "-q", "-m", "init");
        return new GitClient(repo);
    }

    [Fact]
    public async Task Windows_worktree_creation_sets_local_longpaths_and_never_touches_global()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var git = await CreateRepoAsync();
        var globalBefore = await ConfigAsync(_root, "--global", "--get-all", "core.longpaths");

        await git.AddWorktreeAsync(Path.Combine(_root, "wt"), "autopilot/1", "main");

        Assert.Equal("true", await ConfigAsync(git.RepositoryPath, "--local", "--get", "core.longpaths"));
        Assert.Equal(globalBefore, await ConfigAsync(_root, "--global", "--get-all", "core.longpaths"));
    }

    [Fact]
    public async Task Windows_git_add_all_succeeds_in_worktree_with_a_path_over_260_characters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var git = await CreateRepoAsync();
        var worktree = Path.Combine(_root, "wt");
        await git.AddWorktreeAsync(worktree, "autopilot/2", "main");

        var deep = worktree;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var longDir = @"\\?\" + deep;
        Directory.CreateDirectory(longDir);
        File.WriteAllText(Path.Combine(longDir, "file.txt"), "x");
        Assert.True(deep.Length > 260);

        var commit = await git.CommitAllAsync(worktree, "add deep file");

        Assert.NotNull(commit);
    }

    [Fact]
    public async Task EnsureLongPaths_on_windows_writes_local_config_and_is_idempotent()
    {
        var git = await CreateRepoAsync();
        var globalBefore = await ConfigAsync(_root, "--global", "--get-all", "core.longpaths");

        Assert.True(await git.EnsureLongPathsAsync(isWindows: true));
        Assert.True(await git.EnsureLongPathsAsync(isWindows: true));

        Assert.Equal("true", await ConfigAsync(git.RepositoryPath, "--local", "--get", "core.longpaths"));
        Assert.Equal(globalBefore, await ConfigAsync(_root, "--global", "--get-all", "core.longpaths"));
    }

    [Fact]
    public async Task EnsureLongPaths_on_other_platforms_writes_nothing()
    {
        var git = await CreateRepoAsync();

        Assert.False(await git.EnsureLongPathsAsync(isWindows: false));

        Assert.Equal("false", await ConfigAsync(git.RepositoryPath, "--local", "--get", "core.longpaths"));
    }
}
