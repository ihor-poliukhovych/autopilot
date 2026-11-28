using System.Xml.Linq;
using Autopilot.Core.Config;

namespace Autopilot.Core.Gate;

/// <summary>
/// What the gate runs after the one solution build: every test (<see cref="IsAffected"/> false) or only
/// <see cref="TestProjects"/>. <see cref="Description"/> is the line the gate log shows.
/// </summary>
public sealed record TestPlan(bool IsAffected, IReadOnlyList<string> TestProjects, string Description)
{
    public static TestPlan Full { get; } = new(false, [], "gate scope: full (all tests of the solution)");

    public static TestPlan FullByFallback(string reason) => new(false, [], $"gate scope: full by fallback ({reason})");
}

/// <summary>The project files of a checkout and what they reference. Paths use '/' and are relative to the root.</summary>
public sealed class ProjectGraph
{
    public ProjectGraph(IReadOnlyDictionary<string, IReadOnlyList<string>> references, IReadOnlySet<string> testProjects)
    {
        References = references;
        TestProjects = testProjects;
    }

    /// <summary>Project path to the project paths it references directly.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> References { get; }

    public IReadOnlySet<string> TestProjects { get; }

    /// <summary>Reads every <c>*.csproj</c> under <paramref name="root"/> (bin and obj folders skipped).</summary>
    public static ProjectGraph Load(string root)
    {
        var full = Path.GetFullPath(root);
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(full, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(full, file).Replace('\\', '/');
            if (relative.Split('/').Any(p => p is "bin" or "obj" or "node_modules" or ".git"))
            {
                continue;
            }

            var refs = new List<string>();
            try
            {
                var doc = XDocument.Load(file);
                foreach (var element in doc.Descendants())
                {
                    var name = element.Name.LocalName;
                    if (name == "ProjectReference" && element.Attribute("Include")?.Value is { } include)
                    {
                        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, include.Replace('\\', '/')));
                        refs.Add(Path.GetRelativePath(full, target).Replace('\\', '/'));
                    }
                    else if (name == "IsTestProject" && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        tests.Add(relative);
                    }
                    else if (name == "PackageReference" && string.Equals(element.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                    {
                        tests.Add(relative);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable project file is found again by the selector as an unknown change.
            }

            references[relative] = refs;
        }

        return new ProjectGraph(references, tests);
    }
}

/// <summary>
/// Picks the test projects the gate runs for a task. Anything it cannot map to a project falls back to the
/// full suite, and so does every case where the selection would be empty.
/// </summary>
public static class GateScopeSelector
{
    private static readonly string[] SharedFileNames =
    [
        "global.json", "nuget.config", "nuget.config", "packages.lock.json", "NuGet.Config",
    ];

    public static TestPlan Select(string? scope, IReadOnlyList<FileChange> changes, ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(graph);
        if (!string.Equals(scope, GateScopes.Affected, StringComparison.Ordinal))
        {
            return TestPlan.Full;
        }

        if (graph.TestProjects.Count == 0)
        {
            return TestPlan.FullByFallback("the solution has no test project");
        }

        if (changes.Count == 0)
        {
            return TestPlan.FullByFallback("no changed files");
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            foreach (var path in new[] { change.Path, change.OldPath })
            {
                if (path is null)
                {
                    continue;
                }

                var normalized = path.Replace('\\', '/');
                if (IsShared(normalized))
                {
                    return TestPlan.FullByFallback($"{normalized} is a shared file that can affect every project");
                }

                var project = OwningProject(normalized, graph);
                if (project is null)
                {
                    return TestPlan.FullByFallback($"{normalized} belongs to no project");
                }

                if (change.Kind is FileChangeKind.Deleted or FileChangeKind.Renamed
                    && normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return TestPlan.FullByFallback($"project file {normalized} was deleted or renamed");
                }

                changed.Add(project);
            }
        }

        // Dependents, transitively: a project is affected when it references an affected project.
        var affected = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (project, refs) in graph.References)
            {
                if (!affected.Contains(project) && refs.Any(r => affected.Contains(r)))
                {
                    affected.Add(project);
                    grew = true;
                }
            }
        }

        var tests = graph.TestProjects.Where(affected.Contains).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (tests.Count == 0)
        {
            return TestPlan.FullByFallback("no test project references the changed projects");
        }

        return new TestPlan(true, tests,
            $"gate scope: affected ({tests.Count} of {graph.TestProjects.Count} test projects: {string.Join(", ", tests)})");
    }

    private static bool IsShared(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || SharedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The project whose folder is the nearest ancestor of the file, or null when none is.</summary>
    private static string? OwningProject(string path, ProjectGraph graph)
    {
        if (graph.References.ContainsKey(path))
        {
            return path;
        }

        var folder = path;
        while (true)
        {
            var slash = folder.LastIndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            folder = folder[..slash];
            var prefix = folder + "/";
            var match = graph.References.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && p.IndexOf('/', prefix.Length) < 0).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }
    }
}
