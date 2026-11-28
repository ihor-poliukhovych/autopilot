using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autopilot.Core.Process;

namespace Autopilot.Core.Config;

/// <summary>Reads, checks and writes <c>.autopilot/autopilot.json</c>.</summary>
public static class ConfigLoader
{
    public static IReadOnlyList<string> KnownWorkItemTypes { get; } = ["local", "azure-devops"];
    public static IReadOnlyList<string> KnownPublisherTypes { get; } = ["local", "azure-devops"];
    public static IReadOnlyList<string> KnownDiagnosticsTypes { get; } = ["none", "jsonl"];

    /// <summary>Module types that are known but not built yet.</summary>
    public static IReadOnlyList<string> NotAvailableYet { get; } = [];

    /// <summary>camelCase, comments and trailing commas allowed, enums as kebab-case strings.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static string ConfigPath(string projectRoot, string? dataFolder = null) =>
        Path.Combine(dataFolder ?? Path.Combine(projectRoot, AutopilotConfig.FolderName), AutopilotConfig.FileName);

    public static bool Exists(string projectRoot, string? dataFolder = null) => File.Exists(ConfigPath(projectRoot, dataFolder));

    /// <summary>Loads the project's config. Throws <see cref="ConfigException"/> with a readable message.</summary>
    public static AutopilotConfig Load(string projectRoot, string? dataFolder = null) => LoadFile(ConfigPath(projectRoot, dataFolder));

    public static AutopilotConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Config file not found: {path}. Run 'autopilot init' to create it.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>
    /// Parses the file onto the defaults: the JSON is deep-merged over the serialized
    /// <see cref="AutopilotConfig"/> defaults (objects merge key by key, arrays and values replace), so a
    /// file that sets only <c>roles.triage.model</c> keeps every other triage default.
    /// </summary>
    public static AutopilotConfig Parse(string json, string source = "autopilot.json")
    {
        try
        {
            var node = JsonNode.Parse(json, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new ConfigException($"{source} is empty.");
            if (node is not JsonObject user)
            {
                throw new ConfigException($"{source} must hold one JSON object ({{ ... }}).");
            }

            var merged = JsonSerializer.SerializeToNode(new AutopilotConfig(), JsonOptions)!.AsObject();
            MergeInto(merged, user);
            return merged.Deserialize<AutopilotConfig>(JsonOptions)
                ?? throw new ConfigException($"{source} is empty.");
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            throw new ConfigException($"{source} is not valid JSON{where}: {ex.Message}");
        }
    }

    /// <summary>Copies <paramref name="overlay"/> onto <paramref name="target"/>: objects merge, everything else replaces. Keys match ignoring case.</summary>
    public static void MergeInto(JsonObject target, JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overlay);
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null && target[existingKey] is JsonObject targetChild && value is JsonObject overlayChild)
            {
                MergeInto(targetChild, overlayChild);
                continue;
            }

            if (existingKey is not null)
            {
                target.Remove(existingKey);
            }

            target[existingKey ?? key] = value?.DeepClone();
        }
    }

    public static string Serialize(AutopilotConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static void Save(AutopilotConfig config, string projectRoot, string? dataFolder = null) => SaveFile(config, ConfigPath(projectRoot, dataFolder));

    public static void SaveFile(AutopilotConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(config) + Environment.NewLine);
    }

    /// <summary>Returns every problem in plain English. An empty list means the config is usable.</summary>
    public static IReadOnlyList<string> Validate(AutopilotConfig config)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(config.BaseBranch))
        {
            problems.Add("baseBranch is empty. Set it to the branch agent work starts from, for example \"main\".");
        }

        if (string.IsNullOrWhiteSpace(config.BranchPrefix))
        {
            problems.Add("branchPrefix is empty. Use something like \"autopilot/\" so agent branches are easy to spot.");
        }

        if (config.Commands is null)
        {
            problems.Add("commands is missing. Add \"commands\": { \"build\": \"...\", \"test\": \"...\" }.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.Commands.Build))
            {
                problems.Add("commands.build is empty. Set the command that builds the project, for example \"dotnet build\".");
            }

            if (string.IsNullOrWhiteSpace(config.Commands.Test))
            {
                problems.Add("commands.test is empty. Set the command that runs the tests, for example \"dotnet test\".");
            }
        }

        var writable = config.WritablePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        if (writable.Count == 0)
        {
            problems.Add("writablePaths is empty. Fill in the folders the agent may change, as glob patterns relative to the project root, for example \"src/MyApp/**\" and \"tests/MyApp.Tests/**\". Keep it to source and test folders.");
        }

        foreach (var pattern in writable.Where(TargetsAutopilotFolder))
        {
            problems.Add($"writablePaths entry \"{pattern}\" points into .autopilot/. The agent may never change its own settings; remove it.");
        }

        foreach (var problem in (config.Context ?? []).Select(ContextPaths.Problem).OfType<string>())
        {
            problems.Add(problem);
        }

        if (!string.Equals(config.Merge?.By, MergeConfig.Human, StringComparison.Ordinal))
        {
            problems.Add($"merge.by is \"{config.Merge?.By}\". The only value is \"{MergeConfig.Human}\": a person always merges.");
        }

        if (config.AutoQueue is null)
        {
            problems.Add("autoQueue is missing. Remove the \"autoQueue\" key to use the defaults, or fill in enabled and maxRisk.");
        }

        if (config.Identity is { } identity)
        {
            if (string.IsNullOrWhiteSpace(identity.Name) || identity.Name.Length > 100 || identity.Name.Any(char.IsControl))
            {
                problems.Add("identity.name must be a short name such as \"Autopilot\".");
            }

            if (string.IsNullOrWhiteSpace(identity.Email) || identity.Email.Length > 200 || identity.Email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || !identity.Email.Contains('@', StringComparison.Ordinal))
            {
                problems.Add("identity.email must be an address such as \"autopilot@localhost\", with no spaces.");
            }
        }

        if (!TrustLevels.All.Contains(config.Trust ?? string.Empty, StringComparer.Ordinal))
        {
            problems.Add($"trust is \"{config.Trust}\". Use one of: {string.Join(", ", TrustLevels.All)}.");
        }

        if (!CommitStyles.All.Contains(config.CommitStyle ?? string.Empty, StringComparer.Ordinal))
        {
            problems.Add($"commitStyle is \"{config.CommitStyle}\". Use one of: {string.Join(", ", CommitStyles.All)}.");
        }

        problems.AddRange(Autopilot.Core.Git.NamingTemplates.Validate("branchTemplate", config.BranchTemplate, requireId: true));
        problems.AddRange(Autopilot.Core.Git.NamingTemplates.Validate("commitTemplate", config.CommitTemplate, requireId: false));
        problems.AddRange(Autopilot.Core.Git.NamingTemplates.Validate("prTitleTemplate", config.PrTitleTemplate, requireId: false));

        ValidatePool(config, problems);

        if (config.Gate is null)
        {
            problems.Add("gate is missing. Remove the \"gate\" key to use the defaults, or fill in its limits.");
        }
        else
        {
            AddIfNotPositive(problems, "gate.maxFilesChanged", config.Gate.MaxFilesChanged);
            AddIfNotPositive(problems, "gate.maxSourceLinesChanged", config.Gate.MaxSourceLinesChanged);
            AddIfNotPositive(problems, "gate.maxTestLinesChanged", config.Gate.MaxTestLinesChanged);
            if (!GateScopes.All.Contains(config.Gate.Scope ?? string.Empty, StringComparer.Ordinal))
            {
                problems.Add($"gate.scope is \"{config.Gate.Scope}\". Use one of: {string.Join(", ", GateScopes.All)}.");
            }
        }

        if (config.Roles is null)
        {
            problems.Add("roles is missing. Remove the \"roles\" key to use the defaults.");
        }
        else
        {
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(role.ToString());
                var roleConfig = config.Roles.For(role);
                if (roleConfig is null)
                {
                    problems.Add($"roles.{name} is missing. Remove it to use the default, or fill it in.");
                    continue;
                }

                AddIfNotPositive(problems, $"roles.{name}.maxToolCalls", roleConfig.MaxToolCalls);
                if (roleConfig.AllowedTools is null || roleConfig.AllowedTools.Count == 0)
                {
                    problems.Add($"roles.{name}.allowedTools is empty. The {name} agent needs at least Read, Grep and Glob.");
                }
            }
        }

        foreach (var name in config.PassEnvironment ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("passEnvironment has an empty entry. Remove it.");
            }
            else if (ChildEnvironment.IsNeverPassed(name))
            {
                problems.Add($"passEnvironment entry \"{name}\" is not allowed: ANTHROPIC_* and AUTOPILOT_* variables are never passed to the agent or to build and test commands. Remove it.");
            }
        }

        if (config.Modules is null)
        {
            problems.Add("modules is missing. Remove the \"modules\" key to use local files.");
        }
        else
        {
            CheckModule(problems, "modules.workItems", config.Modules.WorkItems, KnownWorkItemTypes);
            CheckModule(problems, "modules.publisher", config.Modules.Publisher, KnownPublisherTypes);
            CheckModule(problems, "modules.diagnostics", config.Modules.Diagnostics, KnownDiagnosticsTypes);
        }

        return problems;
    }

    /// <summary>
    /// True when a glob pattern names something under <c>.autopilot/</c> explicitly. Broad patterns such
    /// as <c>**</c> are allowed: <c>.autopilot/**</c> is always protected by the hook and the gate.
    /// </summary>
    public static bool TargetsAutopilotFolder(string pattern)
    {
        var normalized = pattern.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        return normalized.Equals(AutopilotConfig.FolderName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(AutopilotConfig.FolderName + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("**/" + AutopilotConfig.FolderName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".autopilot*", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".*", StringComparison.Ordinal);
    }

    private static void ValidatePool(AutopilotConfig config, List<string> problems)
    {
        if (config.Pool is not { } pool)
        {
            problems.Add("pool is missing. Remove the \"pool\" key to use the defaults.");
            return;
        }

        if (pool.Concurrency is < 1 or > PoolConfig.MaxConcurrency)
        {
            problems.Add($"pool.concurrency is {pool.Concurrency}. Use a number from 1 to {PoolConfig.MaxConcurrency}.");
        }

        if (pool.Master is null)
        {
            problems.Add("pool.master is missing. Remove it to use the defaults.");
        }
        else if (pool.Master.MaxDecisionsPerRun < 0)
        {
            problems.Add("pool.master.maxDecisionsPerRun cannot be negative.");
        }

        if (pool.OnFailure is null)
        {
            problems.Add("pool.onFailure is missing. Remove it to use the defaults.");
        }
        else
        {
            if (pool.OnFailure.Retries is < 0 or > 3)
            {
                problems.Add("pool.onFailure.retries must be from 0 to 3.");
            }

            if (!string.Equals(pool.OnFailure.Then, PoolFailureActions.Park, StringComparison.Ordinal))
            {
                problems.Add($"pool.onFailure.then is \"{pool.OnFailure.Then}\". The only value is \"{PoolFailureActions.Park}\".");
            }

            if (pool.OnFailure.PauseMinutes < 1)
            {
                problems.Add("pool.onFailure.pauseMinutes must be at least 1.");
            }
        }

        foreach (var category in pool.AlwaysAsk ?? [])
        {
            if (!PoolCategories.All.Contains(category, StringComparer.Ordinal))
            {
                problems.Add($"pool.alwaysAsk entry \"{category}\" is unknown. Use any of: {string.Join(", ", PoolCategories.All)}.");
            }
        }

        if (pool.Budget is null)
        {
            problems.Add("pool.budget is missing. Remove it to use the defaults.");
        }
        else
        {
            if (pool.Budget.MaxAgentRuns is <= 0)
            {
                problems.Add("pool.budget.maxAgentRuns must be greater than 0, or left out for no limit.");
            }

            if (pool.Budget.StopAtUsagePercent is < 1 or > 100)
            {
                problems.Add("pool.budget.stopAtUsagePercent must be from 1 to 100.");
            }
        }

        if (pool.Integration is not { } integration)
        {
            problems.Add("pool.integration is missing. Remove it to use the defaults.");
            return;
        }

        if (!string.Equals(integration.FinalPr, "draft", StringComparison.Ordinal))
        {
            problems.Add($"pool.integration.finalPr is \"{integration.FinalPr}\". The only value is \"draft\": a person marks the final PR ready and merges it.");
        }

        if (!string.IsNullOrWhiteSpace(integration.Branch))
        {
            var branch = integration.Branch.Trim();
            var baseBranch = integration.BaseBranch ?? config.BaseBranch;
            if (!branch.StartsWith(IntegrationConfig.BranchPrefix, StringComparison.Ordinal) || branch.Length == IntegrationConfig.BranchPrefix.Length
                || branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || branch.Contains("..", StringComparison.Ordinal))
            {
                problems.Add($"pool.integration.branch \"{integration.Branch}\" must look like \"integration/<name>\", with no spaces.");
            }
            else if (string.Equals(branch, baseBranch, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("pool.integration.branch is the same as the base branch. The pool never merges into the base branch.");
            }
        }
    }

    private static void AddIfNotPositive(List<string> problems, string name, int value)
    {
        if (value <= 0)
        {
            problems.Add($"{name} is {value}. It must be greater than 0.");
        }
    }

    private static void CheckModule(List<string> problems, string name, ModuleConfig? module, IReadOnlyList<string> known)
    {
        if (module is null || string.IsNullOrWhiteSpace(module.Type))
        {
            problems.Add($"{name}.type is missing. Use one of: {string.Join(", ", known)}.");
            return;
        }

        if (!known.Contains(module.Type, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{name}.type \"{module.Type}\" is unknown. Use one of: {string.Join(", ", known)}.");
            return;
        }

        if (NotAvailableYet.Contains(module.Type, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{name}.type \"{module.Type}\" is not available yet. Use \"{known[0]}\" for now.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,

            // Readable files: quotes and plus signs are written as they are, not as unicode escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>The config file is missing or cannot be read. The message says what to do.</summary>
public sealed class ConfigException(string message) : Exception(message);
