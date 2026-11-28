using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;

namespace Autopilot.Cli.Tests;

public sealed class GateFactsCollectorTests
{
    private static readonly Evidence NoEvidence = new("001", null, null, null);

    /// <summary>
    /// A stand-in for dotnet, put first on PATH: "dotnet test ..." runs this script. It copies result.trx
    /// of the checked-out commit (the current folder) into the --results-directory it was given and prints
    /// a misleading summary line that must not be used for counting.
    /// </summary>
    private const string FakeDotnet = """
        @echo off
        set RD=
        :loop
        if "%~1"=="" goto done
        if "%~1"=="--results-directory" set "RD=%~2"
        shift
        goto loop
        :done
        echo Passed!  - Failed: 0, Passed: 999, Skipped: 0, Total: 999
        if "%RD%"=="" exit /b 7
        copy /y "%CD%\result.trx" "%RD%\r.trx" >nul
        """;

    internal static string Trx(int total, int notExecuted, int failed = 0) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <ResultSummary outcome="Completed">
            <Counters total="{total}" executed="{total - notExecuted}" passed="{total - notExecuted - failed}" failed="{failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="{notExecuted}" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
          </ResultSummary>
        </TestRun>
        """;

    private static Dictionary<string, string> Files(string trx) => new() { ["result.trx"] = trx };

    /// <summary>
    /// Puts the fake dotnet first on PATH until disposed. PATH is shared by the whole test process, so
    /// only one test at a time may hold it; no other test of this assembly runs dotnet.
    /// </summary>
    internal sealed class FakeDotnetOnPath : IDisposable
    {
        private static readonly SemaphoreSlim PathLock = new(1, 1);
        private readonly TempFolder _tools;
        private readonly string? _path;

        public FakeDotnetOnPath()
        {
            PathLock.Wait();
            _tools = new TempFolder();
            _path = Environment.GetEnvironmentVariable("PATH");
            _tools.Write("dotnet.cmd", FakeDotnet.Replace("\n", "\r\n", StringComparison.Ordinal));
            Environment.SetEnvironmentVariable("PATH", _tools.Path + Path.PathSeparator + _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path);
            _tools.Dispose();
            PathLock.Release();
        }
    }

    [Fact]
    public async Task Counts_tests_from_trx_files_on_clean_checkouts_of_base_and_branch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The fake dotnet is a cmd script.
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(Files(Trx(3, 0)));
        var baseCommit = await repo.Git.GetCommitAsync("main");
        var agentWorktree = Path.Combine(repo.WorktreeRoot, "001");
        await repo.Git.AddWorktreeAsync(agentWorktree, "autopilot/001", "main");
        File.WriteAllText(Path.Combine(agentWorktree, "result.trx"), Trx(5, 1));
        var head = await repo.Git.CommitAllAsync(agentWorktree, "change");
        // Uncommitted work in the agent's worktree must not reach the gate.
        File.WriteAllText(Path.Combine(agentWorktree, "result.trx"), Trx(99, 0));

        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo built", Test = "dotnet test" } };
        var collector = new GateFactsCollector(repo.Git, config, repo.WorktreeRoot);

        var input = await collector.CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);
        await repo.Git.RemoveWorktreeAsync(agentWorktree);

        Assert.True(input.Tests.BuildSucceeded);
        Assert.True(input.Tests.TestsSucceeded, input.Tests.Log);
        Assert.True(input.Tests.TestCountsKnown, input.Tests.Log);
        Assert.Equal(3, input.Tests.TestCountBefore);
        Assert.Equal(5, input.Tests.TestCountAfter);
        Assert.Equal(1, input.Tests.SkipsAdded);
        Assert.Contains("built", input.Tests.Log);
        Assert.Contains("--logger trx --results-directory", input.Tests.Log);
        Assert.Equal("result.trx", Assert.Single(input.Changes).Path);
        Assert.Same(NoEvidence, input.Evidence);
        Assert.Empty(Directory.GetDirectories(repo.WorktreeRoot));
    }

    [Fact]
    public async Task Base_result_is_cached_per_commit_commands_and_tool_version()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The fake dotnet is a cmd script.
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(Files(Trx(3, 0)));
        using var cache = new TempFolder();
        var baseCommit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo built", Test = "dotnet test" } };
        GateFactsCollector Collector(AutopilotConfig c, string version = "1.0.0") =>
            new(repo.Git, c, repo.WorktreeRoot) { BaseCacheFolder = cache.Path, ToolVersion = version };

        var first = await Collector(config).CollectAsync("001", baseCommit, baseCommit, NoEvidence, CancellationToken.None);
        var key = GateFactsCollector.BaseCacheKey(baseCommit, "echo built", "dotnet test", "1.0.0");
        var cacheFile = Path.Combine(cache.Path, key + ".json");
        var second = await Collector(config).CollectAsync("001", baseCommit, baseCommit, NoEvidence, CancellationToken.None);
        var otherVersion = await Collector(config, "1.0.1").CollectAsync("001", baseCommit, baseCommit, NoEvidence, CancellationToken.None);
        var otherTest = await Collector(config with { Commands = new CommandsConfig { Build = "echo built", Test = "dotnet test --no-build" } })
            .CollectAsync("001", baseCommit, baseCommit, NoEvidence, CancellationToken.None);

        Assert.True(File.Exists(cacheFile));
        Assert.Contains("base result cached in", first.Tests.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("reused the cached result", first.Tests.Log, StringComparison.Ordinal);
        Assert.Contains($"reused the cached result in {cacheFile}", second.Tests.Log, StringComparison.Ordinal);
        Assert.Single(second.Tests.Log.Split('\n'), l => l.StartsWith("== gate-", StringComparison.Ordinal) && l.Contains("echo built", StringComparison.Ordinal));
        Assert.Equal(3, second.Tests.TestCountBefore);
        Assert.True(second.Tests.TestCountsKnown);
        Assert.DoesNotContain("reused the cached result", otherVersion.Tests.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("reused the cached result", otherTest.Tests.Log, StringComparison.Ordinal);
        Assert.Equal(3, Directory.GetFiles(cache.Path, "*.json").Length);
    }

    [Fact]
    public async Task Base_result_without_counted_tests_is_not_cached()
    {
        using var repo = await TestRepo.CreateAsync();
        using var cache = new TempFolder();
        var commit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = "echo no trx" } };
        var collector = new GateFactsCollector(repo.Git, config, repo.WorktreeRoot) { BaseCacheFolder = cache.Path };

        await collector.CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);
        var again = await collector.CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(cache.Path));
        Assert.DoesNotContain("reused the cached result", again.Tests.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_key_changes_with_every_input()
    {
        var key = GateFactsCollector.BaseCacheKey("abc", "build", "test", "1");

        Assert.Equal(key, GateFactsCollector.BaseCacheKey(" abc ", "build ", " test", "1"));
        Assert.NotEqual(key, GateFactsCollector.BaseCacheKey("abd", "build", "test", "1"));
        Assert.NotEqual(key, GateFactsCollector.BaseCacheKey("abc", "build2", "test", "1"));
        Assert.NotEqual(key, GateFactsCollector.BaseCacheKey("abc", "build", "test2", "1"));
        Assert.NotEqual(key, GateFactsCollector.BaseCacheKey("abc", "build", "test", "2"));
        Assert.Matches("^[0-9a-f]{32}$", key);
    }

    [Fact]
    public async Task Summary_text_without_trx_leaves_the_counts_unknown()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["summary.txt"] = "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n",
        });
        var commit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = Shell.PrintFile("summary.txt") } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

        Assert.True(input.Tests.TestsSucceeded);
        Assert.False(input.Tests.TestCountsKnown);
        Assert.Equal(0, input.Tests.TestCountBefore);
        Assert.Contains("could not be counted", input.Tests.Log);
        Assert.Contains("tests.count", MergeGate.Evaluate(input, config).Findings.Select(f => f.RuleId));
    }

    [Fact]
    public async Task Failed_build_skips_tests()
    {
        using var repo = await TestRepo.CreateAsync();
        var commit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "exit 3", Test = "echo should not run" } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

        Assert.False(input.Tests.BuildSucceeded);
        Assert.False(input.Tests.TestsSucceeded);
        Assert.False(input.Tests.TestCountsKnown);
        Assert.Equal(0, input.Tests.TestCountAfter);
        Assert.DoesNotContain("should not run", input.Tests.Log);
        Assert.Contains("exit code 3", input.Tests.Log);
    }

    [Fact]
    public async Task Skips_added_never_goes_below_zero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(Files(Trx(4, 2)));
        var baseCommit = await repo.Git.GetCommitAsync("main");
        repo.Write("result.trx", Trx(4, 0, failed: 1));
        var head = await repo.Git.CommitAllAsync(repo.Root, "unskip");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = "dotnet test" } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);

        Assert.True(input.Tests.TestCountsKnown, input.Tests.Log);
        Assert.Equal(0, input.Tests.SkipsAdded);
        Assert.Equal(4, input.Tests.TestCountBefore);
        Assert.Equal(4, input.Tests.TestCountAfter);
    }

    private static Dictionary<string, string> TwoProjectFiles() => new()
    {
        ["result.trx"] = Trx(3, 0),
        ["src/Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
        ["src/Lib/a.cs"] = "class A {}\n",
        ["src/Other/Other.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
        ["tests/LibTests/LibTests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\" /><ProjectReference Include=\"..\\..\\src\\Lib\\Lib.csproj\" /></ItemGroup></Project>",
        ["tests/OtherTests/OtherTests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\" /><ProjectReference Include=\"..\\..\\src\\Other\\Other.csproj\" /></ItemGroup></Project>",
    };

    [Fact]
    public async Task Affected_scope_runs_only_the_test_projects_of_the_changed_project_on_both_sides()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(TwoProjectFiles());
        var baseCommit = await repo.Git.GetCommitAsync("main");
        repo.Write("src/Lib/a.cs", "class A { int x; }\n");
        var head = await repo.Git.CommitAllAsync(repo.Root, "change lib");
        var config = new AutopilotConfig
        {
            Gate = new GateConfig { Scope = "affected" },
            Commands = new CommandsConfig { Build = "echo ok", Test = "dotnet test \"Autopilot.slnx\"" },
        };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);

        Assert.True(input.Tests.TestsSucceeded, input.Tests.Log);
        Assert.True(input.Tests.TestCountsKnown, input.Tests.Log);
        // One project ran on each side (the fake gives 3 tests per run), so the counts are comparable.
        Assert.Equal(3, input.Tests.TestCountBefore);
        Assert.Equal(3, input.Tests.TestCountAfter);
        Assert.Contains("gate scope: affected (1 of 2 test projects: tests/LibTests/LibTests.csproj)", input.Tests.Log);
        Assert.Contains("\"tests/LibTests/LibTests.csproj\" --no-build", input.Tests.Log);
        Assert.DoesNotContain("OtherTests.csproj\" --no-build", input.Tests.Log);
    }

    [Fact]
    public async Task Affected_scope_with_a_shared_file_change_runs_the_full_suite_and_says_why()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(TwoProjectFiles());
        var baseCommit = await repo.Git.GetCommitAsync("main");
        repo.Write("Directory.Build.props", "<Project />");
        var head = await repo.Git.CommitAllAsync(repo.Root, "props");
        var config = new AutopilotConfig
        {
            Gate = new GateConfig { Scope = "affected" },
            Commands = new CommandsConfig { Build = "echo ok", Test = "dotnet test" },
        };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);

        Assert.True(input.Tests.TestCountsKnown, input.Tests.Log);
        Assert.Contains("gate scope: full by fallback (Directory.Build.props is a shared file", input.Tests.Log);
        Assert.DoesNotContain("--no-build", input.Tests.Log);
    }

    [Fact]
    public async Task Full_scope_log_says_full()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fake = new FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(Files(Trx(3, 0)));
        var commit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = "dotnet test" } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

        Assert.Contains("gate scope: full (all tests of the solution)", input.Tests.Log);
    }

    [Fact]
    public async Task Build_and_test_commands_get_a_clean_environment()
    {
        var secret = "AUTOPILOT_TEST_SECRET_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var other = "SOME_TOKEN_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Environment.SetEnvironmentVariable(secret, "leaked-autopilot");
        Environment.SetEnvironmentVariable(other, "leaked-other");
        Environment.SetEnvironmentVariable("MY_PASSED_VAR_X", "passed");
        try
        {
            using var repo = await TestRepo.CreateAsync();
            var commit = await repo.Git.GetCommitAsync("main");
            var print = OperatingSystem.IsWindows() ? "set" : "env";
            var config = new AutopilotConfig
            {
                Commands = new CommandsConfig { Build = print, Test = print },
                PassEnvironment = ["MY_PASSED_VAR_X"],
            };

            var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

            Assert.DoesNotContain("leaked-", input.Tests.Log);
            Assert.Contains("MY_PASSED_VAR_X=passed", input.Tests.Log);
            Assert.Contains("MSBUILDDISABLENODEREUSE=1", input.Tests.Log);
            Assert.Contains("DOTNET_CLI_UI_LANGUAGE=en", input.Tests.Log);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(other, null);
            Environment.SetEnvironmentVariable("MY_PASSED_VAR_X", null);
        }
    }
}

public sealed class TrxTestCountsTests
{
    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("  dotnet test --no-build ", true)]
    [InlineData("Dotnet Test", true)]
    [InlineData("dotnet testx", false)]
    [InlineData("npm test", false)]
    [InlineData("make test", false)]
    public void Trx_logger_is_added_only_to_dotnet_test(string command, bool added)
    {
        var result = TrxTestCounts.WithTrxLogger(command, @"C:\r\@trx");

        if (added)
        {
            Assert.NotNull(result);
            Assert.EndsWith(" --logger trx --results-directory \"C:\\r\\@trx\"", result);
            Assert.StartsWith(command.Trim(), result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void Counts_are_summed_over_files_and_old_files_are_ignored()
    {
        using var temp = new TempFolder();
        temp.Write("a/one.trx", GateFactsCollectorTests.Trx(10, 2, failed: 1));
        temp.Write("b/two.trx", GateFactsCollectorTests.Trx(5, 0));
        var old = temp.Write("old.trx", GateFactsCollectorTests.Trx(100, 50));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        temp.Write("notes.txt", "Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1");

        var counts = TrxTestCounts.Read(temp.Path, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(new TrxCounts(2, 15, 2, 1), counts);
        Assert.True(counts.Found);
    }

    [Fact]
    public void Missing_folder_or_no_files_is_not_found()
    {
        using var temp = new TempFolder();
        Assert.False(TrxTestCounts.Read(temp.Path, DateTime.MinValue).Found);
        Assert.False(TrxTestCounts.Read(temp.Combine("missing"), DateTime.MinValue).Found);
    }

    [Fact]
    public void Broken_trx_throws_so_the_caller_fails_closed()
    {
        using var temp = new TempFolder();
        temp.Write("bad.trx", "<TestRun><ResultSummary /></TestRun>");
        temp.Write("worse.trx", "not xml");

        Assert.Throws<InvalidDataException>(() => TrxTestCounts.ReadFile(temp.Combine("bad.trx")));
        Assert.Throws<InvalidDataException>(() => TrxTestCounts.ReadFile(temp.Combine("worse.trx")));
    }
}
