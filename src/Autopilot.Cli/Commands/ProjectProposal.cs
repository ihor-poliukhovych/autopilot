using System.Text;
using Autopilot.Core;
using Autopilot.Core.Analysis;
using Autopilot.Core.Config;
using Autopilot.Core.Git;

namespace Autopilot.Cli.Commands;

/// <summary>What the detector found about a folder: the analysis and, when there is a git history, the conventions.</summary>
public sealed record ProjectDetection(ProjectAnalysis Analysis, ConventionProposal? Conventions);

/// <summary>Turns detected conventions into settings, applies <c>--set key=value</c> overrides and says it all in plain words.</summary>
internal static class ProjectProposal
{
    private static readonly WorkItem Sample = new("12", "Add login page", string.Empty, []);

    public static readonly IReadOnlyList<string> SettableKeys = ["branchTemplate", "commitTemplate", "prTitleTemplate", "baseBranch", "commands.build", "commands.test"];

    public static async Task<ProjectDetection> DetectAsync(string folder, CancellationToken ct)
    {
        var analysis = await ProjectAnalyzer.AnalyzeAsync(folder, ct).ConfigureAwait(false);
        ConventionProposal? conventions = null;
        if (analysis.IsGitRepository)
        {
            conventions = ConventionDetector.Detect(await ConventionGitReader.ReadAsync(analysis.Folder, null, ct).ConfigureAwait(false));
        }

        return new ProjectDetection(analysis, conventions);
    }

    /// <summary>The detected templates written into <paramref name="config"/>. Findings without history leave the defaults.</summary>
    public static AutopilotConfig WithConventions(AutopilotConfig config, ConventionProposal? c)
    {
        if (c is null || !c.HasHistory)
        {
            return config;
        }

        string? branch = null;
        if (c.BranchPattern.Confidence > 0)
        {
            var t = c.BranchPattern.Value.Replace("<id>", "{id}", StringComparison.Ordinal).Replace("<slug>", "{slug}", StringComparison.Ordinal);
            branch = t.Contains("{id}", StringComparison.Ordinal) ? t : null;
        }

        return config with
        {
            BranchTemplate = branch,
            CommitTemplate = FromPattern(c.CommitPattern, "{subject}"),
            PrTitleTemplate = FromPattern(c.PullRequestTitlePattern, "{title}"),
        };
    }

    private static string? FromPattern(ConventionFinding f, string text)
    {
        if (f.Confidence <= 0)
        {
            return null;
        }

        return f.Value switch
        {
            ConventionDetector.CommitIdSubject => "#{id} " + text,
            ConventionDetector.CommitConventional => "{type}: " + text,
            _ => null,
        };
    }

    /// <summary>Applies the overrides; throws <see cref="UsageException"/> for an unknown key or an invalid value.</summary>
    public static AutopilotConfig ApplySets(AutopilotConfig config, IEnumerable<string> sets)
    {
        var problems = new List<string>();
        foreach (var raw in sets)
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                problems.Add($"--set \"{raw}\" must look like key=value. Keys: {string.Join(", ", SettableKeys)}.");
                continue;
            }

            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            var canonical = SettableKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            switch (canonical)
            {
                case "branchTemplate":
                    problems.AddRange(NamingTemplates.Validate("branchTemplate", value, requireId: true));
                    config = config with { BranchTemplate = value.Length == 0 ? null : value };
                    break;
                case "commitTemplate":
                    problems.AddRange(NamingTemplates.Validate("commitTemplate", value, requireId: false));
                    config = config with { CommitTemplate = value.Length == 0 ? null : value };
                    break;
                case "prTitleTemplate":
                    problems.AddRange(NamingTemplates.Validate("prTitleTemplate", value, requireId: false));
                    config = config with { PrTitleTemplate = value.Length == 0 ? null : value };
                    break;
                case "baseBranch":
                    if (value.Length == 0 || value.Any(char.IsWhiteSpace))
                    {
                        problems.Add($"baseBranch \"{value}\" is not a branch name: give one name without spaces.");
                    }

                    config = config with { BaseBranch = value };
                    break;
                case "commands.build":
                    config = config with { Commands = new CommandsConfig { Build = value, Test = config.Commands.Test } };
                    break;
                case "commands.test":
                    config = config with { Commands = new CommandsConfig { Build = config.Commands.Build, Test = value } };
                    break;
                default:
                    problems.Add($"--set key \"{key}\" is not known. Keys: {string.Join(", ", SettableKeys)}.");
                    break;
            }
        }

        if (problems.Count > 0)
        {
            throw new UsageException(string.Join(Environment.NewLine, problems));
        }

        return config;
    }

    public static string Describe(AutopilotConfig config, ConventionProposal? c)
    {
        var text = new StringBuilder();
        text.AppendLine("Here is what was found:");
        var branchTemplate = NamingTemplates.EffectiveBranchTemplate(config);
        text.AppendLine($"  Branches:  {branchTemplate}   e.g. {NamingTemplates.Branch(config, Sample)}");
        if (c is { HasHistory: true, BranchPattern: { Confidence: > 0, Share: { } bs } })
        {
            text.AppendLine($"             seen in {Percent(bs)} of the branches in the history{Examples(c.BranchPattern)}");
        }

        text.AppendLine($"  Commits:   {(string.IsNullOrWhiteSpace(config.CommitTemplate) ? "plain subject (no template)" : config.CommitTemplate)}   e.g. {NamingTemplates.Commit(config, Sample, 1)}");
        if (c is { HasHistory: true, CommitPattern: { Confidence: > 0, Share: { } cs } })
        {
            text.AppendLine($"             seen in {Percent(cs)} of the commits in the history{Examples(c.CommitPattern)}");
        }

        text.AppendLine($"  PR titles: {NamingTemplates.EffectivePrTitleTemplate(config)}   e.g. {NamingTemplates.PullRequestTitle(config, Sample)}");
        text.AppendLine($"  Base branch: {config.BaseBranch}");
        text.AppendLine($"  Build:     {(string.IsNullOrWhiteSpace(config.Commands.Build) ? "(none)" : config.Commands.Build)}");
        text.AppendLine($"  Test:      {(string.IsNullOrWhiteSpace(config.Commands.Test) ? "(none)" : config.Commands.Test)}");
        if (c is { HasHistory: false, Note: { } note })
        {
            text.AppendLine($"  Note: {note}");
        }

        return text.ToString().TrimEnd();
    }

    private static string Percent(double share) => $"{Math.Round(share * 100)}%";

    private static string Examples(ConventionFinding f) =>
        f.Examples.Count == 0 ? string.Empty : " (" + string.Join(", ", f.Examples.Select(e => $"\"{e}\"")) + ")";
}
