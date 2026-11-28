using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;

namespace Autopilot.Cli.Tests;

/// <summary>A retry continues from the previous attempt's kept branch (pool.retry.continue).</summary>
public sealed class PipelineRetryTests
{
    private sealed class Facts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 2, 0, "fake log"), evidence));
    }

    private static AutopilotConfig Config(bool continueRetry = true) => new()
    {
        BaseBranch = "main",
        Commands = new CommandsConfig { Build = "dotnet build", Test = "dotnet test" },
        WritablePaths = ["src/**", "tests/**"],
        Pool = new PoolConfig { Retry = new PoolRetryConfig { Continue = continueRetry } },
    };

    private static async Task<TestRepo> ProjectAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            ["src/Calc.cs"] = "public static class Calc { }\n",
        });
        repo.Write(".autopilot/tasks/001-add.md", "# Add numbers\nCalc.Add(a, b) should return a + b.\n");
        return repo;
    }

    // What the first attempt left: a kept branch with one commit, off main.
    private static async Task KeepAttemptAsync(TestRepo repo, string calcText)
    {
        await repo.GitAsync("checkout", "-b", "autopilot/001-attempt-1");
        repo.Write("src/Calc.cs", calcText);
        repo.Write("src/Previous.cs", "// work of attempt 1\n");
        await repo.GitAsync("add", "src");
        await repo.GitAsync("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "attempt 1");
        await repo.GitAsync("checkout", "main");
    }

    private static string RejectedFile(TestRepo repo) =>
        Path.Combine(ProjectPaths.RunFolder(repo.Root, "001", null), "REJECTED.md");

    private static void WritePreviousRejection(TestRepo repo)
    {
        var file = RejectedFile(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "# Rejected: 001 Add numbers\n\nThe merge gate rejected the change.\n\n## Findings\n\n- [docs] The docs file is missing\n\n## Reviewer findings\n\n- Add a test for 404\n\n## What a person can do\n\n- The branch is kept.\n");
    }

    private sealed class Seen
    {
        public string? ImplementerPrompt { get; set; }
        public bool PreviousFileThere { get; set; }
    }

    private static FakeAgentRunner Runner(Seen seen) => new((request, _) =>
    {
        switch (request.Role)
        {
            case AgentRole.Triage:
                return FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Small.", acceptanceCriteria = new[] { "Add(2, 3) returns 5" }, risk = "low" });
            case AgentRole.Implementer:
                seen.ImplementerPrompt = request.Prompt;
                seen.PreviousFileThere = File.Exists(Path.Combine(request.WorkingDirectory, "src", "Previous.cs"));
                File.WriteAllText(Path.Combine(request.WorkingDirectory, "src", "Added.cs"), "// added in this attempt\n");
                return FakeAgentRunner.Answer(new { summary = "Done.", testsThatProveIt = new[] { "CalcTests.Add" }, doubts = Array.Empty<string>() });
            default:
                return FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() });
        }
    });

    private static async Task<ImplementResult> ImplementAsync(TestRepo repo, AutopilotConfig config, Seen seen, TextWriter log)
    {
        var pipeline = new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = repo.Root,
            Config = config,
            WorkItems = new LocalWorkItemSource(repo.Root),
            Runner = Runner(seen),
            Publisher = new LocalPublisher(repo.Root),
            Git = repo.Git,
            Facts = new Facts(),
            WorktreeRoot = repo.WorktreeRoot,
            EvaluateGate = (_, _) => new GateDecision(GateOutcome.Passed, []),
            Log = log,
        });
        Assert.True(Assert.Single(await pipeline.TriageAsync("001", CancellationToken.None)).Succeeded);
        return await pipeline.ImplementAsync("001", CancellationToken.None);
    }

    [Fact]
    public async Task Retry_starts_from_the_kept_branch_and_logs_the_attempt()
    {
        using var repo = await ProjectAsync();
        await KeepAttemptAsync(repo, "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        WritePreviousRejection(repo);
        var seen = new Seen();
        var log = new StringWriter();

        var result = await ImplementAsync(repo, Config(), seen, log);

        Assert.Equal(ImplementOutcome.Published, result.Outcome);
        Assert.True(seen.PreviousFileThere);
        Assert.Contains("continued from attempt 1", log.ToString(), StringComparison.Ordinal);
        Assert.True(await repo.Git.IsAncestorAsync("autopilot/001-attempt-1", "autopilot/001"));
    }

    [Fact]
    public async Task Retry_prompt_has_the_findings_and_the_continue_instruction()
    {
        using var repo = await ProjectAsync();
        await KeepAttemptAsync(repo, "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        WritePreviousRejection(repo);
        var seen = new Seen();

        await ImplementAsync(repo, Config(), seen, new StringWriter());

        Assert.Contains("The docs file is missing", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.Contains("Add a test for 404", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.Contains("Continue the existing change", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.Contains("fix exactly the points below and do not rewrite what works", seen.ImplementerPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflict_with_the_base_falls_back_to_a_clean_start_and_says_so()
    {
        using var repo = await ProjectAsync();
        await KeepAttemptAsync(repo, "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        repo.Write("src/Calc.cs", "public static class Calc { public static int Other() => 1; }\n");
        await repo.GitAsync("add", "src");
        await repo.GitAsync("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "base moved on");
        WritePreviousRejection(repo);
        var seen = new Seen();
        var log = new StringWriter();

        var result = await ImplementAsync(repo, Config(), seen, log);

        Assert.Equal(ImplementOutcome.Published, result.Outcome);
        Assert.False(seen.PreviousFileThere);
        Assert.Contains("Starting clean", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("continued from attempt", log.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Implement this work item.", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The docs file is missing", seen.ImplementerPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_attempt_has_a_clean_start_and_the_normal_prompt()
    {
        using var repo = await ProjectAsync();
        var seen = new Seen();
        var log = new StringWriter();

        var result = await ImplementAsync(repo, Config(), seen, log);

        Assert.Equal(ImplementOutcome.Published, result.Outcome);
        Assert.StartsWith("Implement this work item.", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("continued from attempt", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_turned_off_starts_clean_with_the_unchanged_prompt()
    {
        using var repo = await ProjectAsync();
        await KeepAttemptAsync(repo, "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        WritePreviousRejection(repo);
        var seen = new Seen();
        var log = new StringWriter();

        var result = await ImplementAsync(repo, Config(continueRetry: false), seen, log);

        Assert.Equal(ImplementOutcome.Published, result.Outcome);
        Assert.False(seen.PreviousFileThere);
        Assert.StartsWith("Implement this work item.", seen.ImplementerPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("continued from attempt", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pool_retry_continue_defaults_to_true()
    {
        Assert.True(new AutopilotConfig().Pool.Retry.Continue);
    }
}
