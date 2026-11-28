using System.Text.RegularExpressions;

namespace Autopilot.Core.Gate.Detectors;

/// <summary>A changed file with the text of its added lines (one entry per line, no diff markers).</summary>
public sealed record ChangedFile(string Path, IReadOnlyList<string> AddedLines);

/// <summary>Minimal input for the deterministic detectors.</summary>
public sealed record DetectorInput(IReadOnlyList<ChangedFile> Files);

public sealed record DetectorFinding(string DetectorId, string FilePath, string Message);

public interface IDetector
{
    string Id { get; }
    IReadOnlyList<DetectorFinding> Detect(DetectorInput input);
}

/// <summary>Flags secrets committed in added lines.</summary>
public sealed class SecretDetector : IDetector
{
    public const string DetectorId = "secret";
    public string Id => DetectorId;

    private static readonly Regex PrivateKey = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex AwsKey = new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled);
    // Quoted literal only, so reads like config["password"] or Environment.GetEnvironmentVariable("X") do not match.
    private static readonly Regex Assignment = new(
        @"(password|passwd|pwd|token|api[_-]?key|apikey|secret)[A-Za-z0-9_]*""?\s*[:=]\s*@?""[^""\s]{4,}""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<DetectorFinding> Detect(DetectorInput input)
    {
        var findings = new List<DetectorFinding>();
        foreach (var file in input.Files)
        {
            foreach (var line in file.AddedLines)
            {
                string? what = null;
                if (PrivateKey.IsMatch(line)) what = "private key header";
                else if (AwsKey.IsMatch(line)) what = "AWS access key id";
                else if (Assignment.IsMatch(line)) what = "literal credential assignment";
                if (what is not null)
                    findings.Add(new DetectorFinding(DetectorId, file.Path, $"Possible committed secret: {what}."));
            }
        }
        return findings;
    }
}

/// <summary>
/// Flags newly added package references. Narrow definition of "risky": the package is not on the
/// allowlist, or its version is floating (missing, "*", a wildcard like "1.*", or a range that is not
/// an exact pin such as "[1.0,)"). Allowlisted packages with a pinned version are not flagged.
/// </summary>
public sealed class RiskyDependencyDetector : IDetector
{
    public const string DetectorId = "risky-dependency";
    public string Id => DetectorId;

    private static readonly Regex Reference = new(
        @"<PackageReference\s[^>]*?Include\s*=\s*""(?<name>[^""]+)""(?<rest>[^>]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Version = new(@"Version\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Pinned = new(@"^\[?\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?\]?$", RegexOptions.Compiled);

    private readonly HashSet<string> _allowlist;

    public RiskyDependencyDetector(IEnumerable<string>? allowlist = null)
    {
        _allowlist = new HashSet<string>(allowlist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DetectorFinding> Detect(DetectorInput input)
    {
        var findings = new List<DetectorFinding>();
        foreach (var file in input.Files)
        {
            foreach (var line in file.AddedLines)
            {
                var m = Reference.Match(line);
                if (!m.Success) continue;
                var name = m.Groups["name"].Value;
                var rest = m.Groups["rest"].Value;
                var v = Version.Match(rest);
                var version = v.Success ? v.Groups["v"].Value.Trim() : "";
                if (!_allowlist.Contains(name))
                    findings.Add(new DetectorFinding(DetectorId, file.Path, $"New package '{name}' is not on the allowlist."));
                if (!Pinned.IsMatch(version))
                    findings.Add(new DetectorFinding(DetectorId, file.Path, $"Package '{name}' uses a floating or missing version '{version}'."));
            }
        }
        return findings;
    }
}

/// <summary>Flags a changed *Controller.cs when no changed test file references ownership or authorization.</summary>
public sealed class ControllerTestDetector : IDetector
{
    public const string DetectorId = "controller-without-access-test";
    public string Id => DetectorId;

    private static readonly Regex AccessWords = new(@"owner|ownership|authoriz|forbid|\b403\b|\b401\b|unauthori", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<DetectorFinding> Detect(DetectorInput input)
    {
        var controllers = input.Files
            .Where(f => f.Path.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (controllers.Count == 0) return Array.Empty<DetectorFinding>();

        var covered = input.Files.Any(f => IsTestFile(f.Path) && f.AddedLines.Any(l => AccessWords.IsMatch(l)));
        if (covered) return Array.Empty<DetectorFinding>();

        return controllers
            .Select(c => new DetectorFinding(DetectorId, c.Path, "Controller changed but no changed test references ownership or authorization."))
            .ToList();
    }

    private static bool IsTestFile(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Runs all built-in detectors.</summary>
public static class BuiltInDetectors
{
    public static IReadOnlyList<DetectorFinding> Run(DetectorInput input, IEnumerable<string>? dependencyAllowlist = null)
    {
        IDetector[] all = { new SecretDetector(), new RiskyDependencyDetector(dependencyAllowlist), new ControllerTestDetector() };
        return all.SelectMany(d => d.Detect(input)).ToList();
    }
}
