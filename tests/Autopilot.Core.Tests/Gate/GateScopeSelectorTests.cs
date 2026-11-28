using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Pool;

namespace Autopilot.Core.Tests.Gate;

public sealed class GateScopeSelectorTests
{
    // Core <- Web <- Web.Tests ; Core <- Core.Tests ; Util (nobody references it) ; Util.Tests -> Util
    private static ProjectGraph Graph() => new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Core/Core.csproj"] = [],
            ["src/Web/Web.csproj"] = ["src/Core/Core.csproj"],
            ["src/Util/Util.csproj"] = [],
            ["tests/Core.Tests/Core.Tests.csproj"] = ["src/Core/Core.csproj"],
            ["tests/Web.Tests/Web.Tests.csproj"] = ["src/Web/Web.csproj"],
            ["tests/Util.Tests/Util.Tests.csproj"] = ["src/Util/Util.csproj"],
        },
        new HashSet<string>(["tests/Core.Tests/Core.Tests.csproj", "tests/Web.Tests/Web.Tests.csproj", "tests/Util.Tests/Util.Tests.csproj"]));

    private static FileChange Change(string path, FileChangeKind kind = FileChangeKind.Modified, string? old = null) => new(path, kind, 1, 0, old);

    private static TestPlan Select(string scope, params FileChange[] changes) => GateScopeSelector.Select(scope, changes, Graph());

    [Fact]
    public void Config_defaults_to_full_and_rejects_unknown_values()
    {
        Assert.Equal("full", new GateConfig().Scope);
        var problems = ConfigLoader.Validate(new AutopilotConfig { Gate = new GateConfig { Scope = "partial" } });
        Assert.Contains(problems, p => p.Contains("gate.scope", StringComparison.Ordinal));
        Assert.DoesNotContain(ConfigLoader.Validate(new AutopilotConfig { Gate = new GateConfig { Scope = "affected" } }), p => p.Contains("gate.scope", StringComparison.Ordinal));
    }

    [Fact]
    public void Scope_full_selects_everything()
    {
        var plan = Select("full", Change("src/Util/A.cs"));
        Assert.False(plan.IsAffected);
        Assert.Contains("gate scope: full", plan.Description);
    }

    [Fact]
    public void Unknown_scope_runs_full()
    {
        Assert.False(Select("bogus", Change("src/Util/A.cs")).IsAffected);
    }

    [Fact]
    public void Changed_source_selects_only_test_projects_that_reference_it()
    {
        var plan = Select("affected", Change("src/Util/A.cs"));
        Assert.True(plan.IsAffected);
        Assert.Equal(["tests/Util.Tests/Util.Tests.csproj"], plan.TestProjects);
        Assert.Contains("gate scope: affected", plan.Description);
    }

    [Fact]
    public void Transitive_dependents_are_selected()
    {
        var plan = Select("affected", Change("src/Core/X.cs"));
        Assert.Equal(["tests/Core.Tests/Core.Tests.csproj", "tests/Web.Tests/Web.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void Changed_test_project_runs_itself_only()
    {
        var plan = Select("affected", Change("tests/Web.Tests/FooTests.cs"));
        Assert.Equal(["tests/Web.Tests/Web.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void Deeply_nested_file_maps_to_its_project()
    {
        var plan = Select("affected", Change("src/Util/Sub/Deep/B.cs"));
        Assert.Equal(["tests/Util.Tests/Util.Tests.csproj"], plan.TestProjects);
    }

    [Theory]
    [InlineData("Autopilot.slnx")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/Directory.Packages.props")]
    [InlineData("build/common.targets")]
    [InlineData("global.json")]
    [InlineData("README.md")]
    [InlineData("shared/helper.cs")]
    public void Shared_or_unowned_files_fall_back_to_full(string path)
    {
        var plan = Select("affected", Change("src/Util/A.cs"), Change(path));
        Assert.False(plan.IsAffected);
        Assert.Contains("full by fallback", plan.Description);
        Assert.Contains(path, plan.Description);
    }

    [Fact]
    public void Deleted_project_file_falls_back_to_full()
    {
        Assert.False(Select("affected", Change("src/Util/Util.csproj", FileChangeKind.Deleted)).IsAffected);
    }

    [Fact]
    public void Rename_uses_the_old_path_too()
    {
        var plan = Select("affected", Change("src/Util/B.cs", FileChangeKind.Renamed, "src/Core/A.cs"));
        Assert.Equal(3, plan.TestProjects.Count);
    }

    [Fact]
    public void No_changes_or_no_test_projects_fall_back_to_full()
    {
        Assert.False(GateScopeSelector.Select("affected", [], Graph()).IsAffected);
        var noTests = new ProjectGraph(Graph().References, new HashSet<string>());
        Assert.False(GateScopeSelector.Select("affected", [Change("src/Util/A.cs")], noTests).IsAffected);
    }

    [Fact]
    public void Change_that_no_test_project_depends_on_falls_back_to_full()
    {
        var graph = new ProjectGraph(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["src/Tool/Tool.csproj"] = [],
                ["tests/T/T.csproj"] = [],
            },
            new HashSet<string>(["tests/T/T.csproj"]));
        var plan = GateScopeSelector.Select("affected", [Change("src/Tool/a.cs")], graph);
        Assert.False(plan.IsAffected);
        Assert.Contains("no test project", plan.Description);
    }

    [Fact]
    public void Graph_load_reads_references_and_test_projects_from_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "gss-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "A.Tests"));
            File.WriteAllText(Path.Combine(root, "src", "A", "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "tests", "A.Tests", "A.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"1\" />"
                + "<ProjectReference Include=\"..\\..\\src\\A\\A.csproj\" /></ItemGroup></Project>");
            var graph = ProjectGraph.Load(root);
            Assert.Equal(["tests/A.Tests/A.Tests.csproj"], graph.TestProjects);
            Assert.Equal(["src/A/A.csproj"], graph.References["tests/A.Tests/A.Tests.csproj"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Affected_test_command_targets_one_project_without_building_again()
    {
        Assert.Equal("dotnet test \"tests/A/A.csproj\" -c Release --no-build",
            GateFactsCollector.AffectedTestCommand("dotnet test \"Autopilot.slnx\" -c Release", "tests/A/A.csproj"));
        Assert.Equal("dotnet test \"tests/A/A.csproj\" --no-build",
            GateFactsCollector.AffectedTestCommand("dotnet test", "tests/A/A.csproj"));
    }

    [Fact]
    public async Task Final_pr_and_post_merge_check_run_the_full_commands_whatever_gate_scope_says()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var config = new AutopilotConfig
            {
                Gate = new GateConfig { Scope = "affected" },
                Commands = new CommandsConfig { Build = "echo build>> ran.txt", Test = "echo test>> ran.txt" },
            };
            var (ok, message) = await IntegrationService.BuildAndTestAsync(config, folder, CancellationToken.None);
            Assert.True(ok, message);
            var lines = (await File.ReadAllLinesAsync(Path.Combine(folder, "ran.txt"))).Select(l => l.Trim()).ToArray();
            Assert.Equal(["build", "test"], lines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
