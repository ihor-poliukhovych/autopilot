using System.CommandLine;
using System.Text.Json;
using Autopilot.Core.Analysis;
using Autopilot.Core.Config;
using Autopilot.Core.Local;
using Autopilot.Core.Workspace;

namespace Autopilot.Cli.Commands;

/// <summary><c>autopilot project add|list|show|remove|import-local</c>: the project registry in the workspace.</summary>
public static class ProjectCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Command Create(ProjectOptions options, CliContext context)
    {
        var command = new Command("project", "Register projects in the workspace. Settings, tasks and run output live there, never in the repository.")
        {
            CreateAdd(options, context),
            CreateAnalyze(context),
            CreateList(options, context),
            CreateShow(options, context),
            CreateRemove(options, context),
            CreateImportLocal(options, context),
        };
        return command;
    }

    private static Command CreateAdd(ProjectOptions options, CliContext context)
    {
        var key = new Argument<string>("key") { Description = "Short name: lower-case letters, digits, '-' and '_'." };
        var repo = new Option<string?>("--repo") { Description = "The repository's top-level folder. Default: the current folder." };
        var name = new Option<string?>("--name") { Description = "Display name. Default: the key." };
        var baseBranch = new Option<string?>("--base-branch") { Description = "Branch agent work starts from. Default: main, else master, else the current branch." };
        var source = new Option<string>("--source") { Description = "Where tasks come from: 'local' (files in the workspace) or 'azure-devops'.", DefaultValueFactory = _ => ProjectRegistry.LocalSource };
        source.AcceptOnlyFromAmong(ProjectRegistry.LocalSource, ProjectRegistry.AzureDevOpsSource);
        var detect = new Option<bool>("--detect") { Description = "Analyze the folder and use what it finds: base branch, build and test commands, paths, context documents and, for an Azure DevOps remote, the task source. Prints what was chosen and why. --name, --base-branch and --source still win." };
        var yes = new Option<bool>("--yes") { Description = "With --detect: do not ask 'Add this project?', save right away." };
        var set = new Option<string[]>("--set") { Description = "Change a value before saving: key=value. Keys: branchTemplate, commitTemplate, prTitleTemplate, baseBranch, commands.build, commands.test. Repeatable.", AllowMultipleArgumentsPerToken = false };
        var command = new Command("add", "Register a project and create its folder in the workspace. Nothing is written to the repository.") { key, repo, name, baseBranch, source, detect, yes, set };
        command.SetAction(async (parse, ct) =>
        {
            var registry = new ProjectRegistry(options.OpenWorkspace(parse));
            var repoFolder = Path.GetFullPath(parse.GetValue(repo) ?? Directory.GetCurrentDirectory());
            var sets = parse.GetValue(set) ?? [];
            ProjectAnalysis? detected = null;
            ConventionProposal? conventions = null;
            if (parse.GetValue(detect))
            {
                ProjectDetection found;
                try
                {
                    found = await (context.Detector ?? ProjectProposal.DetectAsync)(repoFolder, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    throw new UsageException(ex.Message);
                }

                detected = found.Analysis;
                conventions = found.Conventions;
            }

            repoFolder = detected?.Folder ?? repoFolder;
            var sourceGiven = parse.GetResult(source) is { Implicit: false };
            var taskSource = sourceGiven || detected is null ? parse.GetValue(source)! : detected.TaskSource.Value;
            var branch = parse.GetValue(baseBranch)?.Trim();
            var projectKey = parse.GetRequiredValue(key);
            var first = registry.Validate(new NewProject(projectKey, repoFolder, BaseBranch: branch ?? detected?.BaseBranch.Value ?? "main", TaskSource: taskSource));
            if (first.Count > 0)
            {
                throw new UsageException(string.Join(Environment.NewLine, first));
            }

            AutopilotConfig config;
            if (detected is not null)
            {
                config = AnalysisApplier.ToConfig(detected);
                config = branch is null ? config : config with { BaseBranch = branch };
                config = ProjectProposal.WithConventions(config, conventions);
            }
            else
            {
                config = await InitCommand.BuildDefaultsAsync(repoFolder, ct, branch).ConfigureAwait(false);
            }

            if (taskSource == ProjectRegistry.LocalSource && config.Modules.WorkItems.Type != "local")
            {
                // --source local after a detected Azure DevOps remote: keep the person's choice.
                config = config with { Modules = config.Modules with { WorkItems = new ModuleConfig { Type = "local" }, Publisher = new ModuleConfig { Type = "local" } } };
            }

            if (taskSource == ProjectRegistry.AzureDevOpsSource && config.Modules.WorkItems.Type != ProjectRegistry.AzureDevOpsSource)
            {
                config = config with
                {
                    Modules = config.Modules with
                    {
                        WorkItems = new ModuleConfig { Type = "azure-devops" },
                        Publisher = new ModuleConfig { Type = "azure-devops" },
                    },
                };
            }

            config = ProjectProposal.ApplySets(config, sets);
            if (detected is not null)
            {
                await context.Out.WriteLineAsync(ProjectProposal.Describe(config, conventions)).ConfigureAwait(false);
                await context.Out.WriteLineAsync().ConfigureAwait(false);
                if (!parse.GetValue(yes))
                {
                    await context.Out.WriteAsync("Add this project? [y/N] ").ConfigureAwait(false);
                    var answer = (await context.In.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        await context.Out.WriteLineAsync().ConfigureAwait(false);
                        await context.Out.WriteLineAsync("Nothing was added: no project, folder or settings were written.").ConfigureAwait(false);
                        return ExitCodes.Ok;
                    }
                }
            }

            var record = registry.Add(new NewProject(projectKey, repoFolder, parse.GetValue(name) ?? detected?.DisplayName.Value, config.BaseBranch, taskSource));
            var folder = registry.Workspace.ProjectFolder(record.Key);
            ConfigLoader.Save(config, record.RepositoryFolder, folder);
            await context.Out.WriteLineAsync($"Added project {record.Key} ({record.DisplayName}).").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"  Repository:   {record.RepositoryFolder}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"  Data folder:  {folder}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"  Settings:     {ConfigLoader.ConfigPath(record.RepositoryFolder, folder)}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"  Task source:  {record.TaskSource}").ConfigureAwait(false);
            if (detected is not null)
            {
                await context.Out.WriteLineAsync().ConfigureAwait(false);
                await context.Out.WriteLineAsync("What was detected, and why (values in brackets say how sure the analyzer is):").ConfigureAwait(false);
                await context.Out.WriteLineAsync(AnalysisApplier.Describe(detected)).ConfigureAwait(false);
                await context.Out.WriteLineAsync().ConfigureAwait(false);
            }

            await context.Out.WriteLineAsync($"Next: 'autopilot doctor --project {record.Key}'.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static async Task<ProjectAnalysis> Analyze(string folder, CancellationToken ct)
    {
        try
        {
            return await ProjectAnalyzer.AnalyzeAsync(folder, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
    }

    private static Command CreateAnalyze(CliContext context)
    {
        var folder = new Argument<string>("folder") { Description = "A repository folder (or any folder inside one)." };
        var json = new Option<bool>("--json") { Description = "Print JSON: every value with its evidence and confidence." };
        var command = new Command("analyze", "Look at a folder and suggest every project setting, with the file or rule behind each value. Read-only: nothing is written and no model is used.") { folder, json };
        command.SetAction(async (parse, ct) =>
        {
            var analysis = await Analyze(Path.GetFullPath(parse.GetRequiredValue(folder)), ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync(parse.GetValue(json) ? analysis.ToJson() : AnalysisApplier.Describe(analysis)).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateList(ProjectOptions options, CliContext context)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON." };
        var command = new Command("list", "List the registered projects.") { json };
        command.SetAction(async (parse, _) =>
        {
            var workspace = options.OpenWorkspace(parse);
            var projects = new ProjectRegistry(workspace).List();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(projects.Select(Describe), Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (projects.Count == 0)
            {
                await context.Out.WriteLineAsync($"No projects in {workspace.Root}. Add one with 'autopilot project add <key> --repo <folder>'.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            foreach (var p in projects)
            {
                await context.Out.WriteLineAsync($"{p.Key,-20} {p.TaskSource,-13} {p.RepositoryFolder}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateShow(ProjectOptions options, CliContext context)
    {
        var key = new Argument<string>("key") { Description = "Project key." };
        var json = new Option<bool>("--json") { Description = "Print JSON." };
        var command = new Command("show", "Show one project.") { key, json };
        command.SetAction(async (parse, _) =>
        {
            var workspace = options.OpenWorkspace(parse);
            var k = parse.GetRequiredValue(key);
            var p = new ProjectRegistry(workspace).Get(k) ?? throw new UsageException($"There is no project \"{k}\" in {workspace.Root}.");
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(Describe(p) with { DataFolder = workspace.ProjectFolder(p.Key) }, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync($"Key:          {p.Key}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Name:         {p.DisplayName}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Repository:   {p.RepositoryFolder}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Base branch:  {p.BaseBranch}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Task source:  {p.TaskSource}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Data folder:  {workspace.ProjectFolder(p.Key)}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Created:      {p.CreatedAt:u}").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateRemove(ProjectOptions options, CliContext context)
    {
        var key = new Argument<string>("key") { Description = "Project key." };
        var deleteData = new Option<bool>("--delete-data") { Description = "Also delete the project's workspace folder (settings, tasks, run output). Needs --yes." };
        var yes = new Option<bool>("--yes") { Description = "Confirm deleting the workspace data." };
        var command = new Command("remove", "Unlink a project. The repository is never touched; workspace data stays unless --delete-data --yes is given.") { key, deleteData, yes };
        command.SetAction(async (parse, _) =>
        {
            var wipe = parse.GetValue(deleteData);
            if (wipe && !parse.GetValue(yes))
            {
                throw new UsageException("--delete-data removes the project's settings, tasks and run output for good. Add --yes to confirm.");
            }

            var workspace = options.OpenWorkspace(parse);
            var k = parse.GetRequiredValue(key);
            if (!new ProjectRegistry(workspace).Remove(k, wipe))
            {
                throw new UsageException($"There is no project \"{k}\" in {workspace.Root}.");
            }

            await context.Out.WriteLineAsync(wipe
                ? $"Removed project {k} and deleted {workspace.ProjectFolder(k)}."
                : $"Removed project {k}. Its data stays in {workspace.ProjectFolder(k)}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateImportLocal(ProjectOptions options, CliContext context)
    {
        var folder = new Argument<string>("folder") { Description = "A repository folder that has a .autopilot/ folder." };
        var key = new Option<string?>("--key") { Description = "Project key. Default: made from the folder name." };
        var name = new Option<string?>("--name") { Description = "Display name. Default: the folder name." };
        var command = new Command("import-local", "Register a project from an existing .autopilot/ folder: copies its settings and tasks into the workspace and leaves the source untouched.") { folder, key, name };
        command.SetAction(async (parse, _) =>
        {
            var repo = Path.GetFullPath(parse.GetRequiredValue(folder));
            var source = ProjectPaths.AutopilotFolder(repo);
            if (!Directory.Exists(repo) || !ConfigLoader.Exists(repo))
            {
                throw new UsageException($"{source} has no {AutopilotConfig.FileName}. Point to a repository that has a .autopilot/ folder.");
            }

            AutopilotConfig config;
            try
            {
                config = ConfigLoader.Load(repo);
            }
            catch (ConfigException ex)
            {
                throw new UsageException(ex.Message);
            }

            var registry = new ProjectRegistry(options.OpenWorkspace(parse));
            var projectKey = parse.GetValue(key) ?? KeyFromName(Path.GetFileName(repo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var taskSource = config.Modules.WorkItems.Type.Equals(ProjectRegistry.AzureDevOpsSource, StringComparison.OrdinalIgnoreCase)
                ? ProjectRegistry.AzureDevOpsSource
                : ProjectRegistry.LocalSource;
            var project = new NewProject(projectKey, repo, parse.GetValue(name) ?? Path.GetFileName(repo), config.BaseBranch, taskSource);
            var problems = registry.Validate(project);
            if (problems.Count > 0)
            {
                throw new UsageException(string.Join(Environment.NewLine, problems));
            }

            var record = registry.Add(project);
            var target = registry.Workspace.ProjectFolder(record.Key);
            File.Copy(ConfigLoader.ConfigPath(repo), ConfigLoader.ConfigPath(repo, target), overwrite: true);
            var copied = CopyFolder(ProjectPaths.TasksFolder(repo), ProjectPaths.TasksFolder(repo, target));
            await context.Out.WriteLineAsync($"Imported {source} as project {record.Key}: settings and {copied} task file(s) copied to {target}. The source was not changed.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static int CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            count++;
        }

        return count;
    }

    private static string KeyFromName(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var key = new string(chars).Trim('-');
        return key.Length > ProjectRegistry.MaxKeyLength ? key[..ProjectRegistry.MaxKeyLength] : key;
    }

    private static ProjectView Describe(ProjectRecord p) =>
        new(p.Key, p.DisplayName, p.RepositoryFolder, p.BaseBranch, p.TaskSource, p.CreatedAt, null);

    private sealed record ProjectView(string Key, string DisplayName, string RepositoryFolder, string BaseBranch, string TaskSource, DateTimeOffset CreatedAt, string? DataFolder);
}
