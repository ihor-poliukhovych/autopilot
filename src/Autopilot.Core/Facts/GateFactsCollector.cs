using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Process;

namespace Autopilot.Core.Facts;

/// <summary>Collects the facts the merge gate decides on.</summary>
public interface IGateFactsCollector
{
    Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct);
}

/// <summary>
/// Builds and tests the base commit and the branch commit in fresh worktrees of their own (never the
/// agent's worktree, which may hold files the agent did not commit) and reads the diff from git.
/// </summary>
public sealed class GateFactsCollector(GitClient git, AutopilotConfig config, string worktreeRoot) : IGateFactsCollector
{
    private const int MaxLogCharsPerStep = 20_000;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Set on top of the clean <see cref="ChildEnvironment"/> for build and test runs: English output,
    /// no build servers that hold files open.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> CommandEnvironment { get; } = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["UseSharedCompilation"] = "false",
    };

    public async Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct)
    {
        var changes = await git.GetChangesAsync(baseCommit, headCommit, ct).ConfigureAwait(false);
        var log = new StringBuilder();

        CheckoutRun before, after;
        if (string.Equals(config.Gate.Scope, GateScopes.Affected, StringComparison.Ordinal))
        {
            // The branch runs first: its project files decide which test projects the base runs too, so the
            // test counts of both sides cover the same projects.
            after = await BuildAndTestCommitAsync(workItemId, "gate-head", headCommit, log, ct,
                planner: root => PlanFor(root, changes)).ConfigureAwait(false);
            before = await BaseAsync(workItemId, baseCommit, log, ct, after.Plan).ConfigureAwait(false);
        }
        else
        {
            log.AppendLine(TestPlan.Full.Description);
            before = await BaseAsync(workItemId, baseCommit, log, ct).ConfigureAwait(false);
            after = await BuildAndTestCommitAsync(workItemId, "gate-head", headCommit, log, ct).ConfigureAwait(false);
        }

        var tests = new TestFacts(
            BuildSucceeded: after.BuildSucceeded,
            TestsSucceeded: after.BuildSucceeded && after.TestsSucceeded,
            TestCountBefore: before.Counts.Total,
            TestCountAfter: after.Counts.Total,
            SkipsAdded: Math.Max(0, after.Counts.Skipped - before.Counts.Skipped),
            Log: log.ToString(),
            TestCountsKnown: before.Counts.Found && after.Counts.Found);
        return new GateInput(changes, tests, evidence);
    }

    /// <summary>
    /// Internal folder names start with '@', which work item ids may not contain, so they can never
    /// collide with an agent worktree named after an id.
    /// </summary>
    public static string InternalName(string workItemId, string label) => $"@{label}-{workItemId}";

    /// <summary>
    /// Folder for cached base-commit results (<c>.autopilot/out/cache/gate-base/</c>). Null turns the cache off.
    /// </summary>
    public string? BaseCacheFolder { get; init; }

    /// <summary>Part of the cache key, so a new tool version never reuses an old result.</summary>
    public string ToolVersion { get; init; } = DefaultToolVersion;

    public static string DefaultToolVersion { get; } =
        typeof(GateFactsCollector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(GateFactsCollector).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// The cache key for a base result: SHA-256 over the base commit, the build and test command lines and
    /// the tool version. Changing any of them gives a new key, so an old result is never reused for it.
    /// </summary>
    public static string BaseCacheKey(string baseCommit, string buildCommand, string testCommand, string toolVersion)
    {
        var text = string.Join("\n", "gate-base/v1", baseCommit.Trim(), buildCommand.Trim(), testCommand.Trim(), toolVersion.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>The base commit's facts: from the cache when a matching entry exists, otherwise built and tested.</summary>
    private async Task<CheckoutRun> BaseAsync(string workItemId, string baseCommit, StringBuilder log, CancellationToken ct, TestPlan? plan = null)
    {
        var affected = plan is { IsAffected: true };
        if (BaseCacheFolder is null)
        {
            return await BuildAndTestCommitAsync(workItemId, "gate-base", baseCommit, log, ct, fixedPlan: affected ? plan : null).ConfigureAwait(false);
        }

        // An affected run counts only some projects, so the projects are part of the key.
        var testKey = affected ? config.Commands.Test + " #affected: " + string.Join(",", plan!.TestProjects) : config.Commands.Test;
        var key = BaseCacheKey(baseCommit, config.Commands.Build, testKey, ToolVersion);
        var file = Path.Combine(BaseCacheFolder, key + ".json");
        var cached = ReadCache(file);
        if (cached is not null
            && cached.BaseCommit == baseCommit
            && cached.Build == config.Commands.Build.Trim()
            && cached.Test == testKey.Trim()
            && cached.ToolVersion == ToolVersion)
        {
            var counts = new TrxCounts(cached.TrxFiles, cached.Total, cached.Skipped, cached.Failed);
            log.AppendLine($"== gate-base ({Short(baseCommit)}): reused the cached result in {file} (made {cached.CreatedAt:u}); build and tests were not run again");
            log.AppendLine($"-> cached: build {(cached.BuildSucceeded ? "ok" : "failed")}, tests {(cached.TestsSucceeded ? "passed" : "failed")}, {counts.Total} tests, {counts.Skipped} skipped, {counts.Failed} failed");
            return new CheckoutRun(cached.BuildSucceeded, cached.TestsSucceeded, counts, null, null, config.Commands.Test, FromCache: true);
        }

        var run = await BuildAndTestCommitAsync(workItemId, "gate-base", baseCommit, log, ct, fixedPlan: affected ? plan : null).ConfigureAwait(false);

        // Only a complete result is kept: a failed build or uncounted tests may be a passing problem.
        if (run.BuildSucceeded && run.Counts.Found)
        {
            var entry = new BaseCacheEntry(1, baseCommit, config.Commands.Build.Trim(), testKey.Trim(), ToolVersion,
                run.BuildSucceeded, run.TestsSucceeded, run.Counts.Files, run.Counts.Total, run.Counts.Skipped, run.Counts.Failed, DateTimeOffset.UtcNow);
            if (WriteCache(file, entry))
            {
                log.AppendLine($"-> base result cached in {file}");
            }
        }

        return run;
    }

    private static BaseCacheEntry? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<BaseCacheEntry>(File.ReadAllText(file), CacheJson) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WriteCache(string file, BaseCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(entry, CacheJson));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string Short(string commit) => commit[..Math.Min(12, commit.Length)];

    /// <summary>What the cache file holds.</summary>
    public sealed record BaseCacheEntry(
        int Version,
        string BaseCommit,
        string Build,
        string Test,
        string ToolVersion,
        bool BuildSucceeded,
        bool TestsSucceeded,
        int TrxFiles,
        int Total,
        int Skipped,
        int Failed,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Builds and tests one commit in a fresh detached worktree of its own, the way the gate does: the
    /// configured build command, then the test command with a TRX logger into a folder only this run
    /// writes. <paramref name="name"/> and <paramref name="label"/> name the worktree. Doctor uses it too.
    /// </summary>
    public async Task<CheckoutRun> BuildAndTestCommitAsync(string name, string label, string commit, StringBuilder log, CancellationToken ct,
        Func<string, TestPlan>? planner = null, TestPlan? fixedPlan = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        var workItemId = name;
        var path = Path.Combine(worktreeRoot, InternalName(workItemId, label));
        if (Directory.Exists(path))
        {
            await git.RemoveWorktreeAsync(path, ct).ConfigureAwait(false);
        }

        await git.AddDetachedWorktreeAsync(path, commit, ct).ConfigureAwait(false);
        try
        {
            log.AppendLine($"== {label} ({commit[..Math.Min(12, commit.Length)]}): {config.Commands.Build}");
            var build = await ShellRunner.RunAsync(config.Commands.Build, path, CommandTimeout, CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
            AppendLog(log, build);
            if (!build.Succeeded)
            {
                return new CheckoutRun(false, false, TrxCounts.None, build, null, config.Commands.Test);
            }

            // A fresh results folder that only the harness knows, outside every worktree: only TRX files
            // this run wrote are counted. Console text is never used for counting.
            var results = Path.Combine(worktreeRoot, InternalName(workItemId, label + "-trx-" + Guid.NewGuid().ToString("N")[..8]));
            DeleteFolder(results);
            Directory.CreateDirectory(results);
            try
            {
                var plan = fixedPlan ?? planner?.Invoke(path);
                if (plan is { IsAffected: true } && !IsDotnetTest(config.Commands.Test))
                {
                    plan = TestPlan.FullByFallback("the test command is not dotnet test");
                }

                if (plan is not null)
                {
                    log.AppendLine($"== {label}: {plan.Description}");
                }

                if (plan is { IsAffected: true })
                {
                    return await RunAffectedAsync(path, label, plan, results, build, log, ct).ConfigureAwait(false);
                }

                var testCommand = TrxTestCounts.WithTrxLogger(config.Commands.Test, results) ?? config.Commands.Test;
                log.AppendLine($"== {label}: {testCommand}");
                var started = DateTime.UtcNow.AddSeconds(-2);
                var test = await ShellRunner.RunAsync(testCommand, path, CommandTimeout, CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
                AppendLog(log, test);
                var counts = ReadCounts(results, started, log);
                return new CheckoutRun(true, test.Succeeded, counts, build, test, testCommand, Plan: plan);
            }
            finally
            {
                DeleteFolder(results);
            }
        }
        finally
        {
            await git.RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private TestPlan PlanFor(string root, IReadOnlyList<FileChange> changes)
    {
        try
        {
            return GateScopeSelector.Select(config.Gate.Scope, changes, ProjectGraph.Load(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TestPlan.FullByFallback($"the project files could not be read: {ex.Message}");
        }
    }

    private static bool IsDotnetTest(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Equals("dotnet test", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("dotnet test ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The configured <c>dotnet test</c> command for one test project: its solution or project argument is
    /// replaced by <paramref name="project"/> and <c>--no-build</c> is added, because the solution was built once.
    /// </summary>
    public static string AffectedTestCommand(string testCommand, string project)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(testCommand.Trim(), "\"[^\"]*\"|\\S+").Select(m => m.Value).Skip(2);
        var kept = tokens.Where(t =>
        {
            var bare = t.Trim('"');
            return t.StartsWith('-') || !(bare.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        });
        return $"dotnet test \"{project}\" {string.Join(' ', kept)} --no-build".Replace("  ", " ", StringComparison.Ordinal);
    }

    private async Task<CheckoutRun> RunAffectedAsync(string path, string label, TestPlan plan, string results, ProcessResult build, StringBuilder log, CancellationToken ct)
    {
        int files = 0, total = 0, skipped = 0, failed = 0;
        var succeeded = true;
        ProcessResult? last = null;
        var ran = 0;
        foreach (var project in plan.TestProjects)
        {
            if (!File.Exists(Path.Combine(path, project)))
            {
                log.AppendLine($"== {label}: {project} does not exist in this checkout, so it is not run here");
                continue;
            }

            var folder = Path.Combine(results, $"p{ran++}");
            Directory.CreateDirectory(folder);
            var command = TrxTestCounts.WithTrxLogger(AffectedTestCommand(config.Commands.Test, project), folder)!;
            log.AppendLine($"== {label}: {command}");
            var started = DateTime.UtcNow.AddSeconds(-2);
            var test = await ShellRunner.RunAsync(command, path, CommandTimeout, CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
            AppendLog(log, test);
            last = test;
            succeeded &= test.Succeeded;
            var counts = ReadCounts(folder, started, log);
            files += counts.Files;
            total += counts.Total;
            skipped += counts.Skipped;
            failed += counts.Failed;
            if (!counts.Found)
            {
                // Not counted: leave Found false so the gate fails closed.
                files = 0;
                break;
            }
        }

        // No project to run here (the base of a brand new test project): zero tests, counted.
        var found = ran == 0 || files > 0;
        return new CheckoutRun(true, succeeded, ran == 0 ? new TrxCounts(1, 0, 0, 0) : new TrxCounts(found ? files : 0, total, skipped, failed),
            build, last, config.Commands.Test, Plan: plan);
    }

    private static TrxCounts ReadCounts(string results, DateTime started, StringBuilder log)
    {
        try
        {
            var counts = TrxTestCounts.Read(results, started);
            log.AppendLine(counts.Found
                ? $"-> TRX: {counts.Files} file(s), {counts.Total} tests, {counts.Skipped} skipped, {counts.Failed} failed"
                : "-> TRX: no result files, so the tests could not be counted");
            return counts;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.AppendLine($"-> TRX could not be read, so the tests could not be counted: {ex.Message}");
            return TrxCounts.None;
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover results folder is harmless: the next run uses a new name.
        }
    }

    private static void AppendLog(StringBuilder log, ProcessResult result)
    {
        var output = result.Output;
        if (output.Length > MaxLogCharsPerStep)
        {
            output = "(earlier output cut)\n" + output[^MaxLogCharsPerStep..];
        }

        log.AppendLine(output);
        log.AppendLine(result.TimedOut ? "-> timed out" : $"-> exit code {result.ExitCode}");
    }


}

/// <summary>
/// Build and test of one commit on a clean checkout. <see cref="Build"/> and <see cref="Test"/> hold the
/// full output; both are null when the result came from the base cache, and Test is null when the build failed.
/// </summary>
public sealed record CheckoutRun(
    bool BuildSucceeded,
    bool TestsSucceeded,
    TrxCounts Counts,
    ProcessResult? Build,
    ProcessResult? Test,
    string TestCommand,
    bool FromCache = false,
    TestPlan? Plan = null);
