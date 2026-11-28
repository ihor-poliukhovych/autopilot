using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps.Tests;

public class AzureDevOpsOperationsTests
{
    private static readonly string Prs = $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests";
    private static readonly string Refs = $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/refs";
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Zero = "0000000000000000000000000000000000000000";

    private static JsonElement Body(FakeHandler handler, int index = 0) => JsonDocument.Parse(handler.Requests[index].Body).RootElement;

    [Fact]
    public async Task Create_work_item_sends_a_patch_with_every_given_field()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitems/$Bug",
            "{\"id\":310,\"rev\":1,\"url\":\"" + Ado.Org + "/_apis/wit/workItems/310\"}");

        var result = await Ado.Client(handler).CreateWorkItemAsync(
            new WorkItemCreate("Bug", "Crash", "<p>d</p>", "Shop\\Api", "Shop\\Sprint 1", ["a", "b"], "dev@example.com"), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/workitems/$Bug?api-version=7.1", request.Uri.AbsoluteUri);
        Assert.Equal("application/json-patch+json", request.ContentType);
        var ops = Body(handler).EnumerateArray().ToDictionary(o => o.GetProperty("path").GetString()!, o => o.GetProperty("value").GetString());
        Assert.Equal("Crash", ops["/fields/System.Title"]);
        Assert.Equal("<p>d</p>", ops["/fields/System.Description"]);
        Assert.Equal("Shop\\Api", ops["/fields/System.AreaPath"]);
        Assert.Equal("Shop\\Sprint 1", ops["/fields/System.IterationPath"]);
        Assert.Equal("a; b", ops["/fields/System.Tags"]);
        Assert.Equal("dev@example.com", ops["/fields/System.AssignedTo"]);
        Assert.Equal(310, result.Id);
        Assert.Equal(["310"], result.Change.Ids);
        Assert.Equal(6, result.Change.Fields.Count);
    }

    [Fact]
    public async Task Create_work_item_leaves_out_unset_fields()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitems/$Task", "{\"id\":5,\"rev\":1}");
        var result = await Ado.Client(handler).CreateWorkItemAsync(new WorkItemCreate("Task", "T"), default);

        Assert.Single(Body(handler).EnumerateArray());
        Assert.Equal(["System.Title"], result.Change.Fields);
    }

    [Fact]
    public async Task Link_work_item_to_pull_request_adds_an_artifact_link()
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/42", "{\"id\":42,\"rev\":9}");

        var result = await Ado.Client(handler).LinkWorkItemToPullRequestAsync(42, Ado.ProjectId, Ado.RepoId, 22, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/workitems/42?api-version=7.1", request.Uri.AbsoluteUri);
        var op = Body(handler)[0];
        Assert.Equal("add", op.GetProperty("op").GetString());
        Assert.Equal("/relations/-", op.GetProperty("path").GetString());
        Assert.Equal("ArtifactLink", op.GetProperty("value").GetProperty("rel").GetString());
        Assert.Equal($"vstfs:///Git/PullRequestId/{Ado.ProjectId}%2F{Ado.RepoId}%2F22", op.GetProperty("value").GetProperty("url").GetString());
        Assert.Equal(9, result.Rev);
        Assert.Equal(["42", "22"], result.Change.Ids);
    }

    [Fact]
    public async Task Retarget_sends_the_target_ref_and_reports_it()
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, $"{Prs}/22", AzureDevOpsClientTests.PullRequestJson);

        var result = await Ado.Client(handler).UpdatePullRequestTargetBranchAsync(Ado.RepoId, 22, "release/1", default);

        Assert.Equal($"{Prs}/22?api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("refs/heads/release/1", Body(handler).GetProperty("targetRefName").GetString());
        Assert.Equal(22, result.PullRequest.PullRequestId);
        Assert.Equal(["22"], result.Change.Ids);
        Assert.Equal(["targetRefName"], result.Change.Fields);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Set_draft_sends_is_draft(bool draft)
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, $"{Prs}/22", AzureDevOpsClientTests.PullRequestJson);

        var result = await Ado.Client(handler).SetPullRequestDraftAsync(Ado.RepoId, 22, draft, default);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal(draft, Body(handler).GetProperty("isDraft").GetBoolean());
        Assert.Equal(["isDraft"], result.Change.Fields);
    }

    [Fact]
    public async Task Abandon_sends_status_abandoned()
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, $"{Prs}/22", AzureDevOpsClientTests.PullRequestJson.Replace("\"active\"", "\"abandoned\""));

        var result = await Ado.Client(handler).AbandonPullRequestAsync(Ado.RepoId, 22, default);

        Assert.Equal("abandoned", Body(handler).GetProperty("status").GetString());
        Assert.Equal("abandoned", result.PullRequest.Status);
        Assert.Equal(["status"], result.Change.Fields);
        Assert.Equal(["22"], result.Change.Ids);
    }

    [Fact]
    public async Task List_pull_requests_filters_by_creator_and_status()
    {
        var handler = new FakeHandler().On(HttpMethod.Get, Prs, "{\"count\":1,\"value\":[" + AzureDevOpsClientTests.PullRequestJson + "]}");

        var list = await Ado.Client(handler).ListPullRequestsAsync(Ado.RepoId, new PullRequestQuery("abc-123", "active", 10), default);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal($"{Prs}?searchCriteria.creatorId=abc-123&searchCriteria.status=active&$top=10&api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(22, Assert.Single(list).PullRequestId);
        Assert.Equal("autopilot/42", list[0].SourceBranch);
    }

    [Fact]
    public async Task Threads_are_read_with_comments_and_file()
    {
        const string json = """
            { "count": 1, "value": [ { "id": 7, "status": "active",
              "threadContext": { "filePath": "/src/a.cs" },
              "comments": [ { "id": 1, "content": "Please fix", "author": { "displayName": "Pat" }, "isDeleted": false } ] } ] }
            """;
        var handler = new FakeHandler().On(HttpMethod.Get, $"{Prs}/22/threads", json);

        var threads = await Ado.Client(handler).GetPullRequestThreadsAsync(Ado.RepoId, 22, default);

        Assert.Equal($"{Prs}/22/threads?api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        var thread = Assert.Single(threads);
        Assert.Equal(7, thread.Id);
        Assert.Equal("active", thread.Status);
        Assert.Equal("/src/a.cs", thread.FilePath);
        var comment = Assert.Single(thread.Comments);
        Assert.Equal("Please fix", comment.Content);
        Assert.Equal("Pat", comment.Author);
    }

    [Fact]
    public async Task List_branches_filters_by_pattern()
    {
        var json = "{\"count\":2,\"value\":[{\"name\":\"refs/heads/autopilot/1\",\"objectId\":\"" + Sha + "\"},{\"name\":\"refs/heads/autopilot/2\",\"objectId\":\"" + Sha + "\"}]}";
        var handler = new FakeHandler().On(HttpMethod.Get, Refs, json);

        var branches = await Ado.Client(handler).ListBranchesAsync(Ado.RepoId, "autopilot/", default);

        Assert.Equal($"{Refs}?filter=heads%2Fautopilot%2F&api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(["autopilot/1", "autopilot/2"], branches.Select(b => b.Name));
        Assert.Equal(Sha, branches[0].ObjectId);
    }

    [Fact]
    public async Task Create_branch_posts_a_ref_update_from_zero()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, Refs, "{\"value\":[{\"success\":true,\"updateStatus\":\"succeeded\"}]}");

        var result = await Ado.Client(handler).CreateBranchAsync(Ado.RepoId, "autopilot/9", Sha, default);

        Assert.Equal($"{Refs}?api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        var update = Body(handler)[0];
        Assert.Equal("refs/heads/autopilot/9", update.GetProperty("name").GetString());
        Assert.Equal(Zero, update.GetProperty("oldObjectId").GetString());
        Assert.Equal(Sha, update.GetProperty("newObjectId").GetString());
        Assert.Equal(["autopilot/9"], result.Change.Ids);
        Assert.Equal(Sha, result.NewObjectId);
    }

    [Fact]
    public async Task Delete_branch_posts_a_ref_update_to_zero()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, Refs, "{\"value\":[{\"success\":true,\"updateStatus\":\"succeeded\"}]}");

        var result = await Ado.Client(handler).DeleteBranchAsync(Ado.RepoId, "autopilot/9", Sha, default);

        var update = Body(handler)[0];
        Assert.Equal(Sha, update.GetProperty("oldObjectId").GetString());
        Assert.Equal(Zero, update.GetProperty("newObjectId").GetString());
        Assert.Equal("delete branch", result.Change.Operation);
    }

    [Fact]
    public async Task A_ref_update_rejected_by_policy_is_a_distinct_error()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, Refs, "{\"value\":[{\"success\":false,\"updateStatus\":\"rejectedByPolicy\"}]}");

        await Assert.ThrowsAsync<AzureDevOpsPolicyRejectedException>(() => Ado.Client(handler).DeleteBranchAsync(Ado.RepoId, "main", Sha, default));
    }

    [Fact]
    public async Task A_failed_ref_update_without_policy_is_a_plain_error()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, Refs, "{\"value\":[{\"success\":false,\"updateStatus\":\"staleOldObjectId\"}]}");

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler).DeleteBranchAsync(Ado.RepoId, "x", Sha, default));

        Assert.IsNotType<AzureDevOpsPolicyRejectedException>(ex);
        Assert.Contains("staleOldObjectId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_branches_reads_ahead_and_behind()
    {
        var handler = new FakeHandler().On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/diffs/commits",
            "{\"aheadCount\":3,\"behindCount\":1,\"commonCommit\":\"" + Sha + "\"}");

        var result = await Ado.Client(handler).CompareBranchesAsync(Ado.RepoId, "main", "autopilot/9", default);

        Assert.Equal(
            $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/diffs/commits?baseVersion=main&baseVersionType=branch&targetVersion=autopilot%2F9&targetVersionType=branch&api-version=7.1",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(3, result.Ahead);
        Assert.Equal(1, result.Behind);
        Assert.Equal(Sha, result.CommonCommit);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task New_operations_map_401_403_404_to_the_exception_with_the_status(HttpStatusCode status)
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, Prs, "{\"message\":\"nope\"}", status);

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler).AbandonPullRequestAsync(Ado.RepoId, 22, default));

        Assert.Equal(status, ex.StatusCode);
        Assert.IsNotType<AzureDevOpsPolicyRejectedException>(ex);
    }

    [Fact]
    public async Task A_branch_policy_rejection_maps_to_a_distinct_error()
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, Prs, "{\"typeKey\":\"GitPullRequestPolicyViolationException\",\"message\":\"TF402455: not allowed\"}", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<AzureDevOpsPolicyRejectedException>(() => Ado.Client(handler).SetPullRequestDraftAsync(Ado.RepoId, 22, false, default));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("policy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }
}
