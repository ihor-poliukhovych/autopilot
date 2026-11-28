using System.Globalization;
using System.Net;
using System.Text;
using Autopilot.Core;

namespace Autopilot.AzureDevOps;

/// <summary>
/// Work items from an Azure DevOps project. The list comes from the configured WIQL query; the id is
/// the work item number. Triage results are written back as one tag (<c>&lt;prefix&gt;agent-ready</c> or
/// <c>&lt;prefix&gt;needs-human</c>) and one comment, unless <c>recordTriage</c> is <c>local</c>.
/// </summary>
public sealed class AzureDevOpsWorkItemSource(AzureDevOpsClient client, AzureDevOpsSettings settings) : IWorkItemSource
{
    public const string AgentReadyTag = "agent-ready";
    public const string NeedsHumanTag = "needs-human";

    /// <summary>First line of every comment the module writes, so people can tell it apart.</summary>
    public const string CommentMarker = "Written by Autopilot";

    public async Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct)
    {
        var ids = await client.QueryWorkItemIdsAsync(settings.Query, settings.MaxItems, ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var items = await client.GetWorkItemsAsync(ids, ct).ConfigureAwait(false);
        return items.Select(i => ToWorkItem(i, settings)).ToList();
    }

    public async Task<WorkItem?> GetAsync(string id, CancellationToken ct)
    {
        if (!TryParseId(id, out var number))
        {
            return null;
        }

        var item = await GetRawAsync(number, ct).ConfigureAwait(false);
        return item is null ? null : ToWorkItem(item, settings);
    }

    public async Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Read-only mode: the pipeline already stored the result in .autopilot/out/triage/.
        if (settings.RecordTriage == TriageRecording.Local)
        {
            return;
        }

        if (!TryParseId(id, out var number))
        {
            throw new ArgumentException($"\"{id}\" is not an Azure DevOps work item number.", nameof(id));
        }

        await new AzureDevOpsWriteback(client, settings).RecordTriageAsync(number, result, ct).ConfigureAwait(false);
    }

    public static WorkItem ToWorkItem(AdoWorkItem item, AzureDevOpsSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var body = new StringBuilder(HtmlText.ToPlainText(item.GetString(WorkItemFields.Description)));
        var criteria = HtmlText.ToPlainText(item.GetString(WorkItemFields.AcceptanceCriteria));
        if (criteria.Length > 0)
        {
            if (body.Length > 0)
            {
                body.Append("\n\n");
            }

            body.Append("## Acceptance criteria\n\n").Append(criteria);
        }

        return new WorkItem(
            item.Id.ToString(CultureInfo.InvariantCulture),
            item.GetString(WorkItemFields.Title) ?? $"Work item {item.Id}",
            body.ToString(),
            SplitTags(item.GetString(WorkItemFields.Tags)),
            settings is null ? null : WebUrl(settings, item.Id),
            item.GetString(WorkItemFields.State),
            item.GetIdentityName(WorkItemFields.AssignedTo),
            item.GetString(WorkItemFields.IterationPath));
    }

    public static string WebUrl(AzureDevOpsSettings settings, int id) =>
        $"{settings.OrganizationUrl.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(settings.Project)}/_workitems/edit/{id.ToString(CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Removes every tag starting with <paramref name="prefix"/> and adds the one for <paramref name="label"/>.</summary>
    public static string ReplaceTriageTag(string? tags, string prefix, TriageLabel label)
    {
        var kept = SplitTags(tags)
            .Where(t => !t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        kept.Add(prefix + (label == TriageLabel.AgentReady ? AgentReadyTag : NeedsHumanTag));
        return string.Join("; ", kept.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The comment body (HTML, since work item comments are HTML). Every value is encoded.</summary>
    public static string TriageComment(TriageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        static string E(string text) => WebUtility.HtmlEncode(text);
        var label = result.Label == TriageLabel.AgentReady ? AgentReadyTag : NeedsHumanTag;
        var text = new StringBuilder();
        text.Append("<p><b>").Append(E(CommentMarker)).Append("</b>: triage result <b>").Append(E(label)).Append("</b></p>");
        text.Append("<p><b>Reason:</b> ").Append(E(result.Reason)).Append("</p>");
        text.Append("<p><b>Acceptance criteria:</b></p>");
        if (result.AcceptanceCriteria.Count == 0)
        {
            text.Append("<p>(none)</p>");
        }
        else
        {
            text.Append("<ul>");
            foreach (var criterion in result.AcceptanceCriteria)
            {
                text.Append("<li>").Append(E(criterion)).Append("</li>");
            }

            text.Append("</ul>");
        }

        text.Append("<p><b>Risk:</b> ").Append(E(result.Risk.ToString().ToLowerInvariant())).Append("</p>");
        text.Append(result.Label == TriageLabel.AgentReady
            ? "<p>An agent may implement this item. The result can only be a draft pull request that a person reviews and merges.</p>"
            : "<p>A person needs to decide on this item before an agent works on it.</p>");
        return text.ToString();
    }

    private static string NormalizeTags(string? tags) => string.Join("; ", SplitTags(tags));

    private async Task<AdoWorkItem?> GetRawAsync(int number, CancellationToken ct)
    {
        var items = await client.GetWorkItemsAsync([number], ct).ConfigureAwait(false);
        return items.FirstOrDefault();
    }

    private static bool TryParseId(string? id, out int number) =>
        int.TryParse(id?.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
}
