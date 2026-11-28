using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;

namespace Autopilot.Cli.Tests;

public sealed class IntegrationBranchReadTests
{
    private const string Branch = "integration/test";
    private const string OnlyOnIntegration = "src/Tree.cs";

    private sealed class NoFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static async Task<TestRepo> CreateRepoAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            ["README.md"] = "hello\n",
        });
        repo.Write(".autopilot/tasks/001-ui.md", "# Tree list UI\nShow the tree.\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("-c", "user.name=t", "-c", "user.email=t@t", "commit", "--quiet", "-m", "task");
        await repo.GitAsync("checkout", "--quiet", "-b", Branch);
        repo.Write(OnlyOnIntegration, "class Tree { }\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("-c", "user.name=t", "-c", "user.email=t@t", "commit", "--quiet", "-m", "tree endpoint");
        await repo.GitAsync("checkout", "--quiet", "main");
        return repo;
    }

    private static FakeAgentRunner Recording(List<(string Dir, bool HasFile)> seen) => new((request, _) =>
    {
        seen.Add((request.WorkingDirectory, File.Exists(Path.Combine(request.WorkingDirectory, "src", "Tree.cs"))));
        return request.OutputJsonSchema is not null && request.Prompt.Contains("Question", StringComparison.Ordinal)
            ? FakeAgentRunner.Answer(new { decide = true, option = (int?)null, answer = "ok", reason = "r", category = "other", reversible = true })
            : FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Clear.", acceptanceCriteria = new[] { "x" }, risk = "low" });
    });

    private static AutopilotPipeline Pipeline(TestRepo repo, IAgentRunner runner, string baseBranch) => new(new AutopilotPipelineOptions
    {
        ProjectRoot = repo.Root,
        Config = new AutopilotConfig
        {
            BaseBranch = baseBranch,
            Commands = new CommandsConfig { Build = "echo build", Test = "echo test" },
            WritablePaths = ["src/**", "tests/**"],
        },
        WorkItems = new LocalWorkItemSource(repo.Root),
        Runner = runner,
        Publisher = new LocalPublisher(repo.Root),
        Git = repo.Git,
        Facts = new NoFacts(),
        WorktreeRoot = repo.WorktreeRoot,
    });

    [Fact]
    public async Task Triage_with_the_integration_branch_as_base_sees_a_file_the_main_branch_lacks()
    {
        using var repo = await CreateRepoAsync();
        var seen = new List<(string Dir, bool HasFile)>();

        var result = Assert.Single(await Pipeline(repo, Recording(seen), Branch).TriageAsync("001", CancellationToken.None));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Assert.Single(seen).HasFile);
    }

    [Fact]
    public async Task Triage_with_main_as_base_does_not_see_the_integration_file()
    {
        using var repo = await CreateRepoAsync();
        var seen = new List<(string Dir, bool HasFile)>();

        var result = Assert.Single(await Pipeline(repo, Recording(seen), "main").TriageAsync("001", CancellationToken.None));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(Assert.Single(seen).HasFile);
    }

    private static ClaudePoolMaster Master(TestRepo repo, FakeAgentRunner runner, string? branch, string runFolder) =>
        new(runner, new PoolMasterConfig(), repo.Root, runFolder, "", repo.Git, () => branch, repo.WorktreeRoot);

    private static readonly MasterQuestion Ask = new("001", "Tree list UI", "text", "Question?");

    [Fact]
    public async Task Master_runs_in_a_worktree_at_the_integration_branch_that_is_removed_afterwards()
    {
        using var repo = await CreateRepoAsync();
        using var runs = new TempFolder();
        var seen = new List<(string Dir, bool HasFile)>();
        var triageDir = Path.Combine(repo.WorktreeRoot, AutopilotPipeline.TriageWorktreeName);
        Directory.CreateDirectory(triageDir);

        var answer = await Master(repo, Recording(seen), Branch, runs.Path).DecideAsync(Ask, CancellationToken.None);

        Assert.True(answer.Decide);
        var (dir, hasFile) = Assert.Single(seen);
        Assert.True(hasFile);
        Assert.NotEqual(Path.GetFullPath(repo.Root), Path.GetFullPath(dir));
        Assert.StartsWith(Path.GetFullPath(repo.WorktreeRoot), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ClaudePoolMaster.WorktreeNamePrefix, dir, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(triageDir));
    }

    [Fact]
    public async Task Master_without_an_integration_branch_reads_the_project_folder()
    {
        using var repo = await CreateRepoAsync();
        using var runs = new TempFolder();
        var seen = new List<(string Dir, bool HasFile)>();

        await Master(repo, Recording(seen), null, runs.Path).DecideAsync(Ask, CancellationToken.None);

        var (dir, hasFile) = Assert.Single(seen);
        Assert.Equal(repo.Root, dir);
        Assert.False(hasFile);
        Assert.Empty(Directory.Exists(repo.WorktreeRoot) ? Directory.GetDirectories(repo.WorktreeRoot) : []);
    }
}
