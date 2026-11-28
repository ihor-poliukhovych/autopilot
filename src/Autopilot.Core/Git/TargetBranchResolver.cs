using System.Globalization;
using System.Text.RegularExpressions;

namespace Autopilot.Core.Git;

/// <summary>The outcome of mapping a tracker iteration or fix version to an existing release branch.</summary>
public sealed record TargetBranchResolution(string? Branch, string? Reason)
{
    public bool Found => Branch is not null;

    public static TargetBranchResolution Match(string branch) => new(branch, null);

    public static TargetBranchResolution NoMatch(string reason) => new(null, reason);
}

/// <summary>
/// Pure mapping from a version string such as <c>Contoso\2026\7.14</c> to the newest existing branch that
/// fits a template such as <c>release/release-{major}.{minor}.*</c>. Never falls back to another branch.
/// </summary>
public static partial class TargetBranchResolver
{
    public static bool HasWildcardOrPlaceholder(string template) =>
        template.Contains('*', StringComparison.Ordinal) || PlaceholderRegex().IsMatch(template);

    public static TargetBranchResolution Resolve(string? iteration, string? template, IEnumerable<string> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (string.IsNullOrWhiteSpace(template))
        {
            return TargetBranchResolution.NoMatch("No target branch template is set.");
        }

        var version = ExtractVersion(iteration);
        if (version is null)
        {
            return TargetBranchResolution.NoMatch($"The iteration \"{iteration}\" has no version number such as 7.14.");
        }

        var pattern = BuildPattern(template, version);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var best = branches
            .Where(b => !string.IsNullOrWhiteSpace(b) && regex.IsMatch(b))
            .Select(b => (Name: b, Numbers: Numbers(b)))
            .OrderByDescending(x => x.Numbers, VersionComparer.Instance)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name)
            .FirstOrDefault();

        return best is null
            ? TargetBranchResolution.NoMatch($"No existing branch matches \"{template}\" for version {string.Join('.', version.Select(v => v.ToString(CultureInfo.InvariantCulture)))}.")
            : TargetBranchResolution.Match(best);
    }

    /// <summary>The numbers of the last path segment that holds a dotted version, or null.</summary>
    internal static IReadOnlyList<int>? ExtractVersion(string? iteration)
    {
        if (string.IsNullOrWhiteSpace(iteration))
        {
            return null;
        }

        var segments = iteration.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var m = DottedVersionRegex().Match(segments[i]);
            if (m.Success)
            {
                return m.Value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            }
        }

        return null;
    }

    private static string BuildPattern(string template, IReadOnlyList<int> version)
    {
        string Part(int index) => (index < version.Count ? version[index] : 0).ToString(CultureInfo.InvariantCulture);

        var sb = new System.Text.StringBuilder("^");
        var pos = 0;
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            AppendLiteral(sb, template[pos..m.Index]);
            sb.Append(Regex.Escape(m.Groups[1].Value.ToLowerInvariant() switch
            {
                "major" => Part(0),
                "minor" => Part(1),
                _ => Part(2),
            }));
            pos = m.Index + m.Length;
        }

        AppendLiteral(sb, template[pos..]);
        return sb.Append('$').ToString();
    }

    private static void AppendLiteral(System.Text.StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            sb.Append(ch == '*' ? ".*" : Regex.Escape(ch.ToString()));
        }
    }

    private static int[] Numbers(string branch) =>
        DigitsRegex().Matches(branch)
            .Select(m => int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .ToArray();

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var a = i < x.Length ? x[i] : -1;
                var b = i < y.Length ? y[i] : -1;
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            return 0;
        }
    }

    [GeneratedRegex(@"\{(major|minor|patch)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\d+(\.\d+)+", RegexOptions.CultureInvariant)]
    private static partial Regex DottedVersionRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();
}
