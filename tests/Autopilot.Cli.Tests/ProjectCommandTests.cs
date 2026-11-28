using System.Text.Json;
using Autopilot.Core.Config;
using Microsoft.Data.Sqlite;

namespace Autopilot.Cli.Tests;

public sealed class ProjectCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string WorkspacePath => _temp.Combine("ws");

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--workspace", WorkspacePath], new CliContext(TextReader.Null, output, error) { DoctorChecksTools = false });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Add_list_show_work_against_a_temp_workspace_and_leave_the_repository_alone()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        var before = Directory.EnumerateFileSystemEntries(repo.Root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".git", StringComparison.Ordinal)).Order().ToList();

        var add = await RunAsync("project", "add", "demo", "--repo", repo.Root, "--name", "Demo");
        Assert.Equal(0, add.Exit);
        Assert.Contains("Added project demo", add.Out);
        Assert.True(File.Exists(Path.Combine(WorkspacePath, "projects", "demo", "autopilot.json")));
        Assert.True(Directory.Exists(Path.Combine(WorkspacePath, "projects", "demo", "tasks")));
        var after = Directory.EnumerateFileSystemEntries(repo.Root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".git", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(before, after);

        Assert.Contains("demo", (await RunAsync("project", "list")).Out);
        using var listJson = JsonDocument.Parse((await RunAsync("project", "list", "--json")).Out);
        Assert.Equal("demo", listJson.RootElement[0].GetProperty("key").GetString());

        Assert.Contains("Demo", (await RunAsync("project", "show", "demo")).Out);
        using var showJson = JsonDocument.Parse((await RunAsync("project", "show", "demo", "--json")).Out);
        Assert.Equal("local", showJson.RootElement.GetProperty("taskSource").GetString());
        Assert.Equal(Path.GetFullPath(repo.Root), showJson.RootElement.GetProperty("repositoryFolder").GetString());
    }

    [Fact]
    public async Task Add_refuses_a_duplicate_key_and_a_folder_that_is_not_a_repository()
    {
        using var repo = await TestRepo.CreateAsync();
        Assert.Equal(0, (await RunAsync("project", "add", "demo", "--repo", repo.Root)).Exit);

        var duplicate = await RunAsync("project", "add", "demo", "--repo", repo.Root);
        Assert.Equal(2, duplicate.Exit);
        Assert.Contains("already exists", duplicate.Err);

        var notRepo = await RunAsync("project", "add", "other", "--repo", _temp.Path);
        Assert.Equal(2, notRepo.Exit);
        Assert.Contains("git repository", notRepo.Err);
    }

    [Fact]
    public async Task Remove_keeps_data_unless_delete_data_and_yes_are_given()
    {
        using var repo = await TestRepo.CreateAsync();
        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        var folder = Path.Combine(WorkspacePath, "projects", "demo");

        var refused = await RunAsync("project", "remove", "demo", "--delete-data");
        Assert.Equal(2, refused.Exit);
        Assert.Contains("--yes", refused.Err);
        Assert.Contains("demo", (await RunAsync("project", "list")).Out);

        Assert.Equal(0, (await RunAsync("project", "remove", "demo")).Exit);
        Assert.True(Directory.Exists(folder));

        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        Assert.Equal(0, (await RunAsync("project", "remove", "demo", "--delete-data", "--yes")).Exit);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Import_local_copies_settings_and_tasks_and_leaves_the_source_untouched()
    {
        using var repo = await TestRepo.CreateAsync();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".autopilot", "tasks"));
        File.WriteAllText(Path.Combine(repo.Root, ".autopilot", "autopilot.json"), "{ \"baseBranch\": \"main\" }");
        var task = Path.Combine(repo.Root, ".autopilot", "tasks", "001-first.md");
        File.WriteAllText(task, "# First\n\nDo it.\n");

        var result = await RunAsync("project", "import-local", repo.Root, "--key", "imported");

        Assert.Equal(0, result.Exit);
        var folder = Path.Combine(WorkspacePath, "projects", "imported");
        Assert.Equal("main", ConfigLoader.LoadFile(Path.Combine(folder, "autopilot.json")).BaseBranch);
        Assert.Equal(File.ReadAllText(task), File.ReadAllText(Path.Combine(folder, "tasks", "001-first.md")));
        Assert.True(File.Exists(task));

        var missing = await RunAsync("project", "import-local", _temp.Path);
        Assert.Equal(2, missing.Exit);
    }

    [Fact]
    public async Task Project_key_selects_workspace_data_and_a_folder_still_selects_repository_data()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        File.WriteAllText(Path.Combine(WorkspacePath, "projects", "demo", "tasks", "001-ws.md"), "# From workspace\n");

        var viaKey = await RunAsync("doctor", "--project", "demo", "--skip-build");
        Assert.Contains(Path.Combine(WorkspacePath, "projects", "demo", "autopilot.json"), viaKey.Out);
        Assert.Contains("1 open task(s)", viaKey.Out);

        // The old flow: a folder path reads .autopilot/ inside the repository, which has none here.
        var viaFolder = await RunAsync("doctor", "--project", repo.Root, "--skip-build");
        Assert.Contains("Config file not found", viaFolder.Out + viaFolder.Err, StringComparison.OrdinalIgnoreCase);

        var unknown = await RunAsync("doctor", "--project", "nothing");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("no project", unknown.Err);
    }

    [Fact]
    public async Task Analyze_prints_every_value_with_its_evidence_and_changes_nothing()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["App.sln"] = "", ["src/App/App.csproj"] = "<Project/>", ["tests/App.Tests/App.Tests.csproj"] = "<Project/>" });

        var text = await RunAsync("project", "analyze", repo.Root);
        Assert.Equal(0, text.Exit);
        Assert.Contains("dotnet build \"App.sln\"", text.Out);
        Assert.Contains("[high]", text.Out);
        Assert.Contains("Solution file App.sln", text.Out);

        using var json = JsonDocument.Parse((await RunAsync("project", "analyze", repo.Root, "--json")).Out);
        Assert.Equal("dotnet test \"App.sln\"", json.RootElement.GetProperty("test").GetProperty("value").GetString());
        Assert.Equal("high", json.RootElement.GetProperty("test").GetProperty("confidence").GetString());
        Assert.False(Directory.Exists(Path.Combine(WorkspacePath, "projects")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".autopilot")));

        var missing = await RunAsync("project", "analyze", Path.Combine(_temp.Path, "nope"));
        Assert.Equal(2, missing.Exit);
        Assert.Contains("does not exist", missing.Err);
    }

    private async Task<(int Exit, string Out, string Err)> RunFakeAsync(string repoRoot, string input, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var detection = new Autopilot.Cli.Commands.ProjectDetection(
            new Autopilot.Core.Analysis.ProjectAnalysis
            {
                Folder = repoRoot,
                IsGitRepository = true,
                BaseBranch = new Autopilot.Core.Analysis.Detected("develop", "fake", Autopilot.Core.Analysis.Confidence.High),
                Build = new Autopilot.Core.Analysis.Detected("make build", "fake", Autopilot.Core.Analysis.Confidence.High),
                Test = new Autopilot.Core.Analysis.Detected("make test", "fake", Autopilot.Core.Analysis.Confidence.High),
                TaskSource = new Autopilot.Core.Analysis.Detected("local", "fake", Autopilot.Core.Analysis.Confidence.High),
                Key = new Autopilot.Core.Analysis.Detected("fake", "fake", Autopilot.Core.Analysis.Confidence.High),
                DisplayName = new Autopilot.Core.Analysis.Detected("Fake", "fake", Autopilot.Core.Analysis.Confidence.High),
            },
            new Autopilot.Core.Analysis.ConventionProposal(
                true, null,
                new("develop", 1, []),
                new("task/task-<id>", 0.87, ["task/task-12", "task/task-13"], 0.87),
                new(Autopilot.Core.Analysis.ConventionDetector.CommitIdSubject, 0.9, ["#12 add x"], 0.9),
                new(Autopilot.Core.Analysis.ConventionDetector.CommitConventional, 0.8, ["feat: y"], 0.8),
                new("#<number>", 0.9, []), new("github", 0.9, []), new("make build", 1, []), new("make test", 1, []), new("src/**", 1, []), new(string.Empty, 0, [])));
        var context = new CliContext(new StringReader(input), output, error) { DoctorChecksTools = false, Detector = (_, _) => Task.FromResult(detection) };
        var exit = await CliApp.RunAsync(["project", "add", "fake", "--repo", repoRoot, "--detect", .. args, "--workspace", WorkspacePath], context);
        return (exit, output.ToString(), error.ToString());
    }

    private string SettingsPath => Path.Combine(WorkspacePath, "projects", "fake", "autopilot.json");

    [Fact]
    public async Task Detect_shows_the_proposal_asks_and_writes_the_confirmed_settings()
    {
        using var repo = await TestRepo.CreateAsync();

        var result = await RunFakeAsync(repo.Root, "y\n");

        Assert.Equal(0, result.Exit);
        Assert.Contains("task/task-{id}", result.Out);
        Assert.Contains("87%", result.Out);
        Assert.Contains("task/task-12", result.Out);
        Assert.Contains("#{id} {subject}", result.Out);
        Assert.Contains("{type}: {title}", result.Out);
        Assert.Contains("make build", result.Out);
        Assert.Contains("Add this project?", result.Out);
        var config = ConfigLoader.LoadFile(SettingsPath);
        Assert.Equal("task/task-{id}", config.BranchTemplate);
        Assert.Equal("#{id} {subject}", config.CommitTemplate);
        Assert.Equal("{type}: {title}", config.PrTitleTemplate);
        Assert.Equal("develop", config.BaseBranch);
        Assert.Equal("make test", config.Commands.Test);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")]
    public async Task Detect_without_a_yes_answer_writes_nothing_and_says_so(string answer)
    {
        using var repo = await TestRepo.CreateAsync();

        var result = await RunFakeAsync(repo.Root, answer);

        Assert.Equal(0, result.Exit);
        Assert.Contains("Add this project?", result.Out);
        Assert.Contains("Nothing was added", result.Out);
        Assert.False(Directory.Exists(WorkspacePath));
        Assert.False(File.Exists(SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(WorkspacePath, "projects", "fake")));
    }

    [Fact]
    public async Task Detect_with_yes_does_not_ask()
    {
        using var repo = await TestRepo.CreateAsync();

        var result = await RunFakeAsync(repo.Root, string.Empty, "--yes");

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain("Add this project?", result.Out);
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Set_overrides_values_before_saving()
    {
        using var repo = await TestRepo.CreateAsync();

        var result = await RunFakeAsync(repo.Root, string.Empty, "--yes",
            "--set", "branchTemplate=feat/{id}-{slug}", "--set", "commitTemplate=", "--set", "prTitleTemplate=PR {id}: {title}",
            "--set", "baseBranch=release", "--set", "commands.build=npm run b", "--set", "commands.test=npm t");

        Assert.Equal(0, result.Exit);
        var config = ConfigLoader.LoadFile(SettingsPath);
        Assert.Equal("feat/{id}-{slug}", config.BranchTemplate);
        Assert.Null(config.CommitTemplate);
        Assert.Equal("PR {id}: {title}", config.PrTitleTemplate);
        Assert.Equal("release", config.BaseBranch);
        Assert.Equal("npm run b", config.Commands.Build);
        Assert.Equal("npm t", config.Commands.Test);
    }

    [Theory]
    [InlineData("colour=red")]
    [InlineData("branchTemplate=feature/{slug}")]
    [InlineData("commitTemplate={nope}")]
    [InlineData("baseBranch=two words")]
    [InlineData("noequals")]
    public async Task An_invalid_set_is_a_usage_error_and_writes_nothing(string set)
    {
        using var repo = await TestRepo.CreateAsync();

        var result = await RunFakeAsync(repo.Root, "y\n", "--yes", "--set", set);

        Assert.Equal(2, result.Exit);
        Assert.NotEmpty(result.Err);
        Assert.False(Directory.Exists(Path.Combine(WorkspacePath, "projects", "fake")));
    }

    [Fact]
    public async Task Add_with_detect_uses_the_detected_values_and_says_why()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["App.sln"] = "", ["src/App/App.csproj"] = "<Project/>", ["CLAUDE.md"] = "rules" });
        await Autopilot.Core.Process.ProcessRunner.RunAsync("git", ["remote", "add", "origin", "https://dev.azure.com/contoso/Shop/_git/shop-api"], repo.Root);

        var add = await RunAsync("project", "add", "shop", "--repo", repo.Root, "--detect", "--yes");

        Assert.Equal(0, add.Exit);
        Assert.Contains("What was detected, and why", add.Out);
        Assert.Contains("Solution file App.sln", add.Out);
        var config = ConfigLoader.Load(repo.Root, Path.Combine(WorkspacePath, "projects", "shop"));
        Assert.Equal("dotnet build \"App.sln\"", config.Commands.Build);
        Assert.Contains("src/**", config.WritablePaths);
        Assert.Contains("CLAUDE.md", config.Context);
        Assert.Equal("azure-devops", config.Modules.WorkItems.Type);
        Assert.Equal("contoso", config.Modules.WorkItems.Settings["organizationUrl"].Split('/')[^1]);
        Assert.Equal("Shop", config.Modules.WorkItems.Settings["project"]);
        using var show = JsonDocument.Parse((await RunAsync("project", "show", "shop", "--json")).Out);
        Assert.Equal("azure-devops", show.RootElement.GetProperty("taskSource").GetString());

        // An explicit --source and --base-branch win over what was detected.
        var local = await RunAsync("project", "add", "shop-local", "--repo", repo.Root, "--detect", "--yes", "--source", "local", "--base-branch", "trunk");
        Assert.Equal(0, local.Exit);
        var localConfig = ConfigLoader.Load(repo.Root, Path.Combine(WorkspacePath, "projects", "shop-local"));
        Assert.Equal("local", localConfig.Modules.WorkItems.Type);
        Assert.Equal("trunk", localConfig.BaseBranch);
    }
}
