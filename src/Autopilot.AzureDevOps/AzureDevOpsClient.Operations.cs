using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Autopilot.AzureDevOps;

// Typed operations beyond what the module itself uses. Nothing in the module calls the pull request
// mark-ready or abandon methods; they exist for callers that are allowed to.
public sealed partial class AzureDevOpsClient
{
    private const string ZeroObjectId = "0000000000000000000000000000000000000000";

    // ---------- Work items ----------

    /// <summary>Creates a work item and returns its id, revision and the fields that were set.</summary>
    public async Task<AdoWorkItemChange> CreateWorkItemAsync(WorkItemCreate request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        var fields = new List<(string Field, string Value)> { (WorkItemFields.Title, request.Title) };
        AddIfSet(fields, WorkItemFields.Description, request.Description);
        AddIfSet(fields, "System.AreaPath", request.AreaPath);
        AddIfSet(fields, "System.IterationPath", request.IterationPath);
        if (request.Tags is { Count: > 0 })
        {
            fields.Add((WorkItemFields.Tags, string.Join("; ", request.Tags)));
        }

        AddIfSet(fields, WorkItemFields.AssignedTo, request.AssignedTo);

        var patch = fields.Select(f => (object)new { op = "add", path = "/fields/" + f.Field, value = f.Value }).ToList();
        var url = ProjectUrl("_apis/wit/workitems/$" + Uri.EscapeDataString(request.Type));
        using var doc = await SendJsonAsync($"create a {request.Type}", HttpMethod.Post, url, PatchContent(patch), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var value) ? value : 0;
        var rev = root.TryGetProperty("rev", out var revElement) && revElement.TryGetInt32(out var r) ? r : 0;
        return new AdoWorkItemChange(id, rev, String(root, "url"), new AdoChange("create work item", [Id(id)], fields.Select(f => f.Field).ToList()));
    }

    /// <summary>Links a work item to a pull request with an artifact link.</summary>
    public async Task<AdoWorkItemChange> LinkWorkItemToPullRequestAsync(
        int workItemId, string projectId, string repositoryId, int pullRequestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var artifact = $"vstfs:///Git/PullRequestId/{projectId}%2F{repositoryId}%2F{pullRequestId.ToString(CultureInfo.InvariantCulture)}";
        object[] patch =
        [
            new { op = "add", path = "/relations/-", value = new { rel = "ArtifactLink", url = artifact, attributes = new { name = "Pull Request" } } },
        ];
        var url = ProjectUrl($"_apis/wit/workitems/{Id(workItemId)}");
        using var doc = await SendJsonAsync(
            $"link work item {workItemId} to pull request {pullRequestId}", HttpMethod.Patch, url, PatchContent(patch), ct).ConfigureAwait(false);
        var rev = doc.RootElement.TryGetProperty("rev", out var revElement) && revElement.TryGetInt32(out var r) ? r : 0;
        return new AdoWorkItemChange(
            workItemId, rev, String(doc.RootElement, "url"),
            new AdoChange("link work item to pull request", [Id(workItemId), Id(pullRequestId)], ["relations"]));
    }

    // ---------- Pull requests ----------

    /// <summary>Changes the branch a pull request merges into.</summary>
    public Task<AdoPullRequestChange> UpdatePullRequestTargetBranchAsync(string repositoryId, int pullRequestId, string targetBranch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        return PatchPullRequestAsync(
            repositoryId, pullRequestId, "retarget", new Dictionary<string, object> { ["targetRefName"] = "refs/heads/" + targetBranch }, ct);
    }

    /// <summary>Sets a pull request to draft (<c>true</c>) or ready for review (<c>false</c>).</summary>
    public Task<AdoPullRequestChange> SetPullRequestDraftAsync(string repositoryId, int pullRequestId, bool isDraft, CancellationToken ct) =>
        PatchPullRequestAsync(
            repositoryId, pullRequestId, isDraft ? "set draft" : "mark ready", new Dictionary<string, object> { ["isDraft"] = isDraft }, ct);

    /// <summary>Abandons a pull request.</summary>
    public Task<AdoPullRequestChange> AbandonPullRequestAsync(string repositoryId, int pullRequestId, CancellationToken ct) =>
        PatchPullRequestAsync(repositoryId, pullRequestId, "abandon", new Dictionary<string, object> { ["status"] = "abandoned" }, ct);

    private async Task<AdoPullRequestChange> PatchPullRequestAsync(
        string repositoryId, int pullRequestId, string what, Dictionary<string, object> body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests/{Id(pullRequestId)}");
        using var doc = await SendJsonAsync($"{what} pull request {pullRequestId}", HttpMethod.Patch, url, Json(body), ct).ConfigureAwait(false);
        return new AdoPullRequestChange(
            ReadPullRequest(doc.RootElement),
            new AdoChange(what + " pull request", [Id(pullRequestId)], body.Keys.ToList()));
    }

    /// <summary>Lists pull requests of a repository, optionally by creator id and status.</summary>
    public async Task<IReadOnlyList<AdoPullRequest>> ListPullRequestsAsync(string repositoryId, PullRequestQuery query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(query.CreatorId))
        {
            parameters.Add(("searchCriteria.creatorId", query.CreatorId));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            parameters.Add(("searchCriteria.status", query.Status));
        }

        parameters.Add(("$top", query.Top.ToString(CultureInfo.InvariantCulture)));
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests", [.. parameters]);
        using var doc = await SendJsonAsync("list pull requests", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        return Items(doc.RootElement).Select(ReadPullRequest).Where(p => p.PullRequestId > 0).ToList();
    }

    /// <summary>The comment threads of a pull request.</summary>
    public async Task<IReadOnlyList<AdoThread>> GetPullRequestThreadsAsync(string repositoryId, int pullRequestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests/{Id(pullRequestId)}/threads");
        using var doc = await SendJsonAsync($"read the threads of pull request {pullRequestId}", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var result = new List<AdoThread>();
        foreach (var thread in Items(doc.RootElement))
        {
            var comments = new List<AdoThreadComment>();
            if (thread.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var comment in list.EnumerateArray())
                {
                    comments.Add(new AdoThreadComment(
                        comment.TryGetProperty("id", out var cid) && cid.TryGetInt32(out var c) ? c : 0,
                        String(comment, "content"),
                        comment.TryGetProperty("author", out var author) ? String(author, "displayName") : null,
                        Bool(comment, "isDeleted")));
                }
            }

            string? path = null;
            if (thread.TryGetProperty("threadContext", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                path = String(context, "filePath");
            }

            result.Add(new AdoThread(
                thread.TryGetProperty("id", out var id) && id.TryGetInt32(out var t) ? t : 0,
                String(thread, "status"),
                path,
                comments));
        }

        return result;
    }

    // ---------- Branches ----------

    /// <summary>Lists branches whose name starts with <paramref name="pattern"/> (all branches when empty).</summary>
    public async Task<IReadOnlyList<AdoBranch>> ListBranchesAsync(string repositoryId, string? pattern, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var url = ProjectUrl(
            $"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/refs",
            ("filter", "heads/" + (pattern ?? string.Empty)));
        using var doc = await SendJsonAsync("list branches", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var result = new List<AdoBranch>();
        foreach (var element in Items(doc.RootElement))
        {
            if (String(element, "name") is { } name)
            {
                result.Add(new AdoBranch(BranchName(name), String(element, "objectId") ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>Creates a branch pointing at <paramref name="fromObjectId"/> (a commit id).</summary>
    public Task<AdoBranchChange> CreateBranchAsync(string repositoryId, string branch, string fromObjectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromObjectId);
        return UpdateRefAsync(repositoryId, branch, ZeroObjectId, fromObjectId, "create branch", ct);
    }

    /// <summary>Deletes a branch. <paramref name="currentObjectId"/> must be the commit it points at now.</summary>
    public Task<AdoBranchChange> DeleteBranchAsync(string repositoryId, string branch, string currentObjectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentObjectId);
        return UpdateRefAsync(repositoryId, branch, currentObjectId, ZeroObjectId, "delete branch", ct);
    }

    private async Task<AdoBranchChange> UpdateRefAsync(
        string repositoryId, string branch, string oldObjectId, string newObjectId, string what, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var url = ProjectUrl($"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/refs");
        var body = Json(new[] { new { name = "refs/heads/" + branch, oldObjectId, newObjectId } });
        using var doc = await SendJsonAsync($"{what} {branch}", HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        foreach (var element in Items(doc.RootElement))
        {
            if (element.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                var status = String(element, "updateStatus") ?? "unknown";
                var message = $"Azure DevOps did not {what} {branch}: {status}.";
                throw status.Contains("policy", StringComparison.OrdinalIgnoreCase)
                    ? new AzureDevOpsPolicyRejectedException("A branch policy rejected the request to " + message, null)
                    : new AzureDevOpsException(message, null);
            }
        }

        return new AdoBranchChange(branch, oldObjectId, newObjectId, new AdoChange(what, [branch], ["objectId"]));
    }

    /// <summary>How far <paramref name="target"/> is ahead of and behind <paramref name="baseBranch"/>.</summary>
    public async Task<AdoBranchComparison> CompareBranchesAsync(string repositoryId, string baseBranch, string target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var url = ProjectUrl(
            $"_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/diffs/commits",
            ("baseVersion", baseBranch), ("baseVersionType", "branch"),
            ("targetVersion", target), ("targetVersionType", "branch"));
        using var doc = await SendJsonAsync($"compare {target} with {baseBranch}", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new AdoBranchComparison(Int(root, "aheadCount"), Int(root, "behindCount"), String(root, "commonCommit"));
    }

    // ---------- Helpers ----------

    private static bool IsPolicyRejection(System.Net.HttpStatusCode status, string body)
    {
        if ((int)status is not (400 or 403 or 409))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (String(root, "typeKey") is { } key && key.Contains("Policy", StringComparison.OrdinalIgnoreCase))
                || (String(root, "message") is { } message
                    && (message.Contains("TF402455", StringComparison.Ordinal) || message.Contains("branch policy", StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BranchName(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static void AddIfSet(List<(string Field, string Value)> fields, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add((field, value));
        }
    }

    private static StringContent PatchContent(object patch) =>
        new(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");
}
