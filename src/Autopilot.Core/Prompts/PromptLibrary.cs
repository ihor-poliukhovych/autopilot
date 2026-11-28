using System.Reflection;
using System.Text;
using Autopilot.Core.Config;

namespace Autopilot.Core.Prompts;

/// <summary>System prompts and output schemas (embedded resources) and the per-run user prompts.</summary>
public static class PromptLibrary
{
    /// <summary>Diffs longer than this are cut in the reviewer prompt; the reviewer can run git diff itself.</summary>
    public const int MaxDiffChars = 80_000;

    public static string SystemPrompt(AgentRole role) => ReadResource(ResourceBase(role) + ".md");

    public static string OutputSchema(AgentRole role) => ReadResource(ResourceBase(role) + ".schema.json");

    public static string TriagePrompt(
        WorkItem item,
        IReadOnlyList<string>? riskPaths = null,
        IReadOnlyList<string>? protectedPaths = null,
        ProjectContext? context = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Triage this work item.");
        text.AppendLine();
        AppendWorkItem(text, item, criteria: null);
        AppendPathRules(text, riskPaths ?? [], protectedPaths ?? []);
        text.Append(context?.Render());
        return text.ToString();
    }

    /// <summary>The rule triage applies to the paths no agent may change.</summary>
    public const string PathRule =
        "If the change can only be made by touching one of these paths, answer `needs-human` and name the path in the reason.";

    private static void AppendPathRules(StringBuilder text, IReadOnlyList<string> riskPaths, IReadOnlyList<string> protectedPaths)
    {
        var risk = riskPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var protectedOnes = protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (risk.Count == 0 && protectedOnes.Count == 0)
        {
            return;
        }

        text.AppendLine("## Paths an agent may not change");
        text.AppendLine();
        if (risk.Count > 0)
        {
            text.AppendLine("Risk paths (glob patterns; a change here always goes to a person):");
            foreach (var path in risk)
            {
                text.AppendLine($"- `{path}`");
            }

            text.AppendLine();
        }

        if (protectedOnes.Count > 0)
        {
            text.AppendLine("Protected paths (glob patterns; no agent may write them):");
            foreach (var path in protectedOnes)
            {
                text.AppendLine($"- `{path}`");
            }

            text.AppendLine();
        }

        text.AppendLine(PathRule);
        text.AppendLine();
    }

    /// <summary>A retry that continues from the previous attempt: which attempt, and what its reviewer and gate found.</summary>
    public sealed record RetryContinuation(int Attempt, IReadOnlyList<string> Findings);

    public static string ImplementerPrompt(
        WorkItem item,
        TriageResult triage,
        IReadOnlyList<string> writablePaths,
        CommandsConfig commands,
        IReadOnlyList<string>? reviewFindings = null,
        ProjectContext? context = null,
        RetryContinuation? retry = null)
    {
        var text = new StringBuilder();
        if (retry is not null)
        {
            text.AppendLine($"An earlier attempt (attempt {retry.Attempt}) at this item was not accepted. Its change is already committed in this worktree (merged with the current base). Continue the existing change: fix exactly the points below and do not rewrite what works. Then answer with a new self-review of the whole change.");
            text.AppendLine();
            text.AppendLine("## Findings of the previous attempt");
            text.AppendLine();
            if (retry.Findings.Count == 0)
            {
                text.AppendLine("- (no findings were recorded; check the change against the acceptance criteria)");
            }

            foreach (var finding in retry.Findings)
            {
                text.AppendLine($"- {finding}");
            }

            text.AppendLine();
        }
        else if (reviewFindings is { Count: > 0 })
        {
            text.AppendLine("A reviewer asked for changes to your earlier work on this item. Your earlier changes are already committed in this worktree. Fix every finding below, then answer with a new self-review of the whole change.");
            text.AppendLine();
            text.AppendLine("## Reviewer findings");
            text.AppendLine();
            foreach (var finding in reviewFindings)
            {
                text.AppendLine($"- {finding}");
            }

            text.AppendLine();
        }
        else
        {
            text.AppendLine("Implement this work item.");
            text.AppendLine();
        }

        AppendWorkItem(text, item, triage.AcceptanceCriteria);
        text.AppendLine("## Writable paths");
        text.AppendLine();
        text.AppendLine("You may create or change files only where one of these glob patterns matches (relative to the worktree root). Never under `.autopilot/`.");
        text.AppendLine();
        foreach (var path in writablePaths)
        {
            text.AppendLine($"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("## Commands");
        text.AppendLine();
        text.AppendLine($"- Build: `{commands.Build}`");
        text.AppendLine($"- Test: `{commands.Test}`");
        text.AppendLine();
        text.AppendLine("Both must pass before you finish.");
        text.AppendLine();
        text.Append(context?.Render());
        return text.ToString();
    }

    public static string ReviewerPrompt(WorkItem item, TriageResult triage, string diff, ProjectContext? context = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Review this change.");
        text.AppendLine();
        AppendWorkItem(text, item, triage.AcceptanceCriteria);
        text.AppendLine("## Diff");
        text.AppendLine();
        var shown = diff.Length > MaxDiffChars
            ? diff[..MaxDiffChars] + "\n... (diff cut here; run `git diff HEAD~1` to see the rest)\n"
            : diff;
        text.AppendLine("```diff");
        text.AppendLine(shown.TrimEnd());
        text.AppendLine("```");
        var rules = context?.Render();
        if (!string.IsNullOrEmpty(rules))
        {
            text.AppendLine();
            text.Append(rules);
        }

        return text.ToString();
    }

    private static void AppendWorkItem(StringBuilder text, WorkItem item, IReadOnlyList<string>? criteria)
    {
        text.AppendLine($"## Work item {item.Id}: {item.Title}");
        text.AppendLine();
        text.AppendLine(string.IsNullOrWhiteSpace(item.Body) ? "(no description)" : item.Body.Trim());
        text.AppendLine();
        if (item.Labels.Count > 0)
        {
            text.AppendLine($"Labels: {string.Join(", ", item.Labels)}");
            text.AppendLine();
        }

        if (criteria is not null)
        {
            text.AppendLine("## Acceptance criteria");
            text.AppendLine();
            if (criteria.Count == 0)
            {
                text.AppendLine("(none given)");
            }

            foreach (var criterion in criteria)
            {
                text.AppendLine($"- {criterion}");
            }

            text.AppendLine();
        }
    }

    private static string ResourceBase(AgentRole role) => role switch
    {
        AgentRole.Triage => "triage",
        AgentRole.Implementer => "implementer",
        AgentRole.Reviewer => "reviewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string ReadResource(string fileName)
    {
        var name = "Autopilot.Core.Prompts." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
