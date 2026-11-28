using System.Text.Json;

namespace Autopilot.AzureDevOps;

/// <summary>Field reference names the module reads.</summary>
public static class WorkItemFields
{
    public const string Id = "System.Id";
    public const string Title = "System.Title";
    public const string Description = "System.Description";
    public const string AcceptanceCriteria = "Microsoft.VSTS.Common.AcceptanceCriteria";
    public const string Tags = "System.Tags";
    public const string State = "System.State";
    public const string AssignedTo = "System.AssignedTo";
    public const string WorkItemType = "System.WorkItemType";

    public const string IterationPath = "System.IterationPath";

    public static IReadOnlyList<string> All { get; } = [Id, Title, Description, AcceptanceCriteria, Tags, State, WorkItemType, AssignedTo, IterationPath];
}

/// <summary>A work item as returned by the batch endpoint: id, revision and the requested fields.</summary>
public sealed record AdoWorkItem(int Id, int Rev, IReadOnlyDictionary<string, JsonElement> Fields)
{
    public string? GetString(string field) =>
        Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The display name of an identity field such as System.AssignedTo, or null.</summary>
    public string? GetIdentityName(string field) =>
        Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
}

public sealed record AdoProject(string Id, string Name);

public sealed record AdoRepository(string Id, string Name, string ProjectId, string? WebUrl, string? RemoteUrl, string? DefaultBranch);

public sealed record AdoPullRequest(
    int PullRequestId,
    string? WebUrl,
    bool IsDraft = true,
    string? Status = null,
    string? Title = null,
    string? Description = null,
    string? SourceBranch = null,
    string? TargetBranch = null);

/// <summary>One comment of a work item as Azure DevOps returns it (the text is HTML).</summary>
public sealed record AdoComment(int Id, string Html, string? Author, DateTimeOffset? CreatedAt);

/// <summary>One relation of a work item (<c>rel</c> such as <c>System.LinkTypes.Hierarchy-Forward</c> or <c>ArtifactLink</c>).</summary>
public sealed record AdoRelation(string Rel, string Url, string? Name);

/// <summary>One operation of a JSON Patch document for a work item.</summary>
public sealed record WorkItemPatchOp(string Op, string Path, object? Value);

/// <summary>One policy configuration from the Git policy configurations endpoint.</summary>
public sealed record AdoPolicyConfiguration(
    int Id,
    string TypeId,
    string? TypeDisplayName,
    bool IsEnabled,
    bool IsBlocking,
    bool IsDeleted,
    JsonElement Settings);

/// <summary>What the publisher sends when it creates a pull request. There is deliberately no auto-complete field.</summary>
public sealed record PullRequestCreate(
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    string? WorkItemId);

/// <summary>What to put on a new work item. Only the given parts are sent.</summary>
public sealed record WorkItemCreate(
    string Type,
    string Title,
    string? Description = null,
    string? AreaPath = null,
    string? IterationPath = null,
    IReadOnlyList<string>? Tags = null,
    string? AssignedTo = null);

/// <summary>What a write changed: the operation, the ids touched and the fields set.</summary>
public sealed record AdoChange(string Operation, IReadOnlyList<string> Ids, IReadOnlyList<string> Fields);

public sealed record AdoWorkItemChange(int Id, int Rev, string? Url, AdoChange Change);

public sealed record AdoPullRequestChange(AdoPullRequest PullRequest, AdoChange Change);

/// <summary>Filter for listing pull requests. Status is active, abandoned, completed or all.</summary>
public sealed record PullRequestQuery(string? CreatorId = null, string? Status = null, int Top = AzureDevOpsClient.DefaultTop);

public sealed record AdoThreadComment(int Id, string? Content, string? Author, bool IsDeleted);

public sealed record AdoThread(int Id, string? Status, string? FilePath, IReadOnlyList<AdoThreadComment> Comments);

public sealed record AdoBranch(string Name, string ObjectId);

public sealed record AdoBranchChange(string Name, string? OldObjectId, string? NewObjectId, AdoChange Change);

public sealed record AdoBranchComparison(int Ahead, int Behind, string? CommonCommit);

/// <summary>Permission bits of the Git Repositories security namespace (from Security Namespaces - Query).</summary>
public static class GitRepositoryPermissions
{
    public const string NamespaceId = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";

    public const int GenericRead = 2;
    public const int GenericContribute = 4;
    public const int ForcePush = 8;
    public const int CreateBranch = 16;
    public const int PolicyExempt = 128;
    public const int EditPolicies = 2048;
    public const int ManagePermissions = 8192;
    public const int PullRequestContribute = 16384;
    public const int PullRequestBypassPolicy = 32768;

    /// <summary>Security token for one repository: <c>repoV2/&lt;projectId&gt;/&lt;repositoryId&gt;</c>.</summary>
    public static string RepositoryToken(string projectId, string repositoryId) => $"repoV2/{projectId}/{repositoryId}";
}

/// <summary>Policy type ids used by the doctor.</summary>
public static class PolicyTypes
{
    /// <summary>"Minimum number of reviewers" (shown as "Minimum approval count" in the API).</summary>
    public const string MinimumReviewers = "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd";
}
