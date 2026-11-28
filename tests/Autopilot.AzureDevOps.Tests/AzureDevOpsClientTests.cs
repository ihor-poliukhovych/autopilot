using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps.Tests;

public class AzureDevOpsClientTests
{
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Get = HttpMethod.Get;

    [Fact]
    public async Task Wiql_posts_the_query_with_top_and_api_version()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(300, 299));
        var ids = await Ado.Client(handler).QueryWorkItemIdsAsync("SELECT [System.Id] FROM WorkItems", 50, default);

        Assert.Equal([300, 299], ids);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Post, request.Method);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/wiql?$top=50&api-version=7.1", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("SELECT [System.Id] FROM WorkItems", JsonDocument.Parse(request.Body).RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Every_request_carries_basic_auth_with_empty_user_name()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());
        await Ado.Client(handler).QueryWorkItemIdsAsync("q", 50, default);

        Assert.Equal("Basic " + Ado.TokenBase64, handler.Requests[0].Authorization);
        Assert.DoesNotContain(Ado.Token, handler.Requests[0].Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bearer_tokens_are_sent_as_bearer()
    {
        var handler = new FakeHandler().On(Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson);
        await Ado.Client(handler, tokens: new FixedTokenProvider("eyJ0eXAiOiJKV1Qi.dummy.sig", bearer: true)).GetProjectAsync(default);

        Assert.Equal("Bearer eyJ0eXAiOiJKV1Qi.dummy.sig", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Batch_asks_for_the_module_fields_and_keeps_the_id_order()
    {
        const string json = """
            {
              "count": 2,
              "value": [
                { "id": 297, "rev": 1, "fields": { "System.Id": 297, "System.Title": "First" }, "url": "https://dev.azure.com/contoso/_apis/wit/workItems/297" },
                { "id": 299, "rev": 7, "fields": { "System.Id": 299, "System.Title": "Second" }, "url": "https://dev.azure.com/contoso/_apis/wit/workItems/299" }
              ]
            }
            """;
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", json);
        var items = await Ado.Client(handler).GetWorkItemsAsync([299, 297, 1000], default);

        Assert.Equal([299, 297], items.Select(i => i.Id));
        Assert.Equal(7, items[0].Rev);
        Assert.Equal("Second", items[0].GetString("System.Title"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/workitemsbatch?api-version=7.1", request.Uri.AbsoluteUri);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal([299, 297, 1000], body.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(
            ["System.Id", "System.Title", "System.Description", "Microsoft.VSTS.Common.AcceptanceCriteria", "System.Tags", "System.State", "System.WorkItemType", "System.AssignedTo", "System.IterationPath"],
            body.GetProperty("fields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("omit", body.GetProperty("errorPolicy").GetString());
    }

    [Fact]
    public async Task Batch_splits_more_than_200_ids()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":0,\"value\":[]}");
        await Ado.Client(handler).GetWorkItemsAsync(Enumerable.Range(1, 450).ToList(), default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(200, JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("ids").GetArrayLength());
        Assert.Equal(50, JsonDocument.Parse(handler.Requests[2].Body).RootElement.GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public async Task Update_tags_sends_a_json_patch_that_tests_the_revision()
    {
        var handler = new FakeHandler().On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/299", "{\"id\":299,\"rev\":8,\"fields\":{}}");
        await Ado.Client(handler).UpdateTagsAsync(299, 7, "autopilot; autopilot:agent-ready", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/workitems/299?api-version=7.1", request.Uri.AbsoluteUri);
        Assert.Equal("application/json-patch+json", request.ContentType);
        var ops = JsonDocument.Parse(request.Body).RootElement.EnumerateArray().ToList();
        Assert.Equal(2, ops.Count);
        Assert.Equal("test", ops[0].GetProperty("op").GetString());
        Assert.Equal("/rev", ops[0].GetProperty("path").GetString());
        Assert.Equal(7, ops[0].GetProperty("value").GetInt32());
        Assert.Equal("add", ops[1].GetProperty("op").GetString());
        Assert.Equal("/fields/System.Tags", ops[1].GetProperty("path").GetString());
        Assert.Equal("autopilot; autopilot:agent-ready", ops[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Comment_uses_the_preview_api_version()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/workItems/299/comments", "{\"workItemId\":299,\"id\":50,\"version\":1,\"text\":\"x\"}");
        await Ado.Client(handler).AddCommentAsync(299, "<p>hello</p>", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Ado.Org}/Shop/_apis/wit/workItems/299/comments?api-version=7.1-preview.4", request.Uri.AbsoluteUri);
        Assert.Equal("<p>hello</p>", JsonDocument.Parse(request.Body).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Project_and_repository_are_read_with_their_ids()
    {
        var handler = new FakeHandler()
            .On(Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson)
            .On(Get, $"{Ado.Org}/Shop/_apis/git/repositories/shop-api", Ado.RepositoryJson);
        var client = Ado.Client(handler);

        var project = await client.GetProjectAsync(default);
        var repo = await client.GetRepositoryAsync("shop-api", default);

        Assert.Equal(Ado.ProjectId, project.Id);
        Assert.Equal(Ado.RepoId, repo.Id);
        Assert.Equal(Ado.ProjectId, repo.ProjectId);
        Assert.Equal($"{Ado.Org}/Shop/_git/shop-api", repo.WebUrl);
        Assert.Equal($"{Ado.Org}/_apis/projects/Shop?api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal($"{Ado.Org}/Shop/_apis/git/repositories/shop-api?api-version=7.1", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Names_with_spaces_are_escaped_in_the_path()
    {
        var handler = new FakeHandler().On(Get, $"{Ado.Org}/_apis/projects/", Ado.ProjectJson);
        var client = new AzureDevOpsClient(new HttpClient(handler), new Uri(Ado.Org), "Shop Online", new FixedTokenProvider(Ado.Token));
        await client.GetProjectAsync(default);

        Assert.Equal($"{Ado.Org}/_apis/projects/Shop%20Online?api-version=7.1", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Draft_pull_request_body_is_a_draft_without_auto_complete()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests", PullRequestJson, HttpStatusCode.Created);
        var pr = await Ado.Client(handler).CreateDraftPullRequestAsync(
            Ado.RepoId, new PullRequestCreate("autopilot/42", "main", "[autopilot] Fix", "Body", "42"), default);

        Assert.Equal(22, pr.PullRequestId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests?api-version=7.1", request.Uri.AbsoluteUri);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("refs/heads/autopilot/42", body.GetProperty("sourceRefName").GetString());
        Assert.Equal("refs/heads/main", body.GetProperty("targetRefName").GetString());
        Assert.Equal("[autopilot] Fix", body.GetProperty("title").GetString());
        Assert.Equal("Body", body.GetProperty("description").GetString());
        Assert.True(body.GetProperty("isDraft").GetBoolean());
        Assert.Equal("42", body.GetProperty("workItemRefs")[0].GetProperty("id").GetString());
        Assert.False(body.TryGetProperty("autoCompleteSetBy", out _));
        Assert.False(body.TryGetProperty("completionOptions", out _));
        Assert.False(body.TryGetProperty("reviewers", out _));
        Assert.False(body.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task Branch_policies_are_filtered_by_repository_ref_and_type_and_follow_continuation()
    {
        var first = FakeHandler.JsonResponse(PolicyJson(1, enabled: true, blocking: true, approvers: 1, creatorVoteCounts: false));
        first.Headers.Add("x-ms-continuationtoken", "2");
        var handler = new FakeHandler().OnSequence(Get, $"{Ado.Org}/Shop/_apis/git/policy/configurations",
            () => first,
            () => FakeHandler.JsonResponse(PolicyJson(2, enabled: false, blocking: true, approvers: 2, creatorVoteCounts: true)));

        var policies = await Ado.Client(handler).GetBranchPoliciesAsync(Ado.RepoId, "refs/heads/main", PolicyTypes.MinimumReviewers, default);

        Assert.Equal([1, 2], policies.Select(p => p.Id));
        Assert.Equal(
            $"{Ado.Org}/Shop/_apis/git/policy/configurations?repositoryId={Ado.RepoId}&refName=refs%2Fheads%2Fmain&policyType={PolicyTypes.MinimumReviewers}&api-version=7.1",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Contains("continuationToken=2", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Equal(PolicyTypes.MinimumReviewers, policies[0].TypeId);
        Assert.True(policies[0].IsBlocking);
        Assert.Equal(1, policies[0].Settings.GetProperty("minimumApproverCount").GetInt32());
    }

    [Fact]
    public async Task Permission_check_uses_the_git_namespace_bit_and_repository_token()
    {
        var handler = new FakeHandler().On(Get, $"{Ado.Org}/_apis/permissions/", "{\"count\":1,\"value\":[true]}");
        var token = GitRepositoryPermissions.RepositoryToken(Ado.ProjectId, Ado.RepoId);

        var has = await Ado.Client(handler).HasPermissionAsync(GitRepositoryPermissions.NamespaceId, GitRepositoryPermissions.ForcePush, token, default);

        Assert.True(has);
        Assert.Equal(
            $"{Ado.Org}/_apis/permissions/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87/8?tokens=repoV2%2F{Ado.ProjectId}%2F{Ado.RepoId}&alwaysAllowAdministrators=false&api-version=7.1",
            handler.Requests[0].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401", "valid, unexpired token")]
    [InlineData(HttpStatusCode.Forbidden, "403", "Work Items (read & write)")]
    [InlineData(HttpStatusCode.NotFound, "404", "\"organizationUrl\", \"project\" and \"repository\"")]
    public async Task Errors_say_what_to_fix_and_never_contain_the_token(HttpStatusCode status, string code, string advice)
    {
        // The server message echoes the token on purpose, to prove it is redacted.
        var handler = new FakeHandler().On(Get, $"{Ado.Org}/_apis/projects/Shop",
            "{\"message\":\"TF400813: token " + Ado.Token + " / " + Ado.TokenBase64 + " rejected\"}", status);

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler).GetProjectAsync(default));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(code, ex.Message, StringComparison.Ordinal);
        Assert.Contains(advice, ex.Message, StringComparison.Ordinal);
        Assert.Contains("TF400813", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.TokenBase64, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.Token, ex.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests); // 4xx is never retried
    }

    [Fact]
    public async Task A_sign_in_page_instead_of_json_is_treated_as_401()
    {
        var handler = new FakeHandler().OnSequence(Get, $"{Ado.Org}/_apis/projects/Shop", () =>
            new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new System.Net.Http.StringContent("<html>Sign in</html>", System.Text.Encoding.UTF8, "text/html"),
            });

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler).GetProjectAsync(default));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_is_retried_after_the_retry_after_delay()
    {
        var limited = FakeHandler.JsonResponse("{\"message\":\"slow down\"}", HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var handler = new FakeHandler().OnSequence(Get, $"{Ado.Org}/_apis/projects/Shop",
            () => limited,
            () => FakeHandler.JsonResponse(Ado.ProjectJson));
        var delays = new List<TimeSpan>();

        var project = await Ado.Client(handler, delays).GetProjectAsync(default);

        Assert.Equal("Shop", project.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
        Assert.All(handler.Requests, r => Assert.Equal("Basic " + Ado.TokenBase64, r.Authorization));
    }

    [Fact]
    public async Task Server_errors_are_retried_with_backoff_then_reported()
    {
        var handler = new FakeHandler().On(Get, $"{Ado.Org}/_apis/projects/Shop", "{\"message\":\"boom\"}", HttpStatusCode.ServiceUnavailable);
        var delays = new List<TimeSpan>();

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler, delays).GetProjectAsync(default));

        Assert.Equal(AzureDevOpsClient.MaxAttempts, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Contains("server error", ex.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_that_never_ends_gives_up_with_advice()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/wiql", "{}", HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler, []).QueryWorkItemIdsAsync("q", 50, default));

        Assert.Equal(AzureDevOpsClient.MaxAttempts, handler.Requests.Count);
        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
        // The body is sent again on every attempt.
        Assert.All(handler.Requests, r => Assert.Contains("\"query\":\"q\"", r.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_request_is_not_retried()
    {
        var handler = new FakeHandler().On(Post, $"{Ado.Org}/Shop/_apis/wit/wiql", "{\"message\":\"TF51005: bad query\"}", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => Ado.Client(handler, []).QueryWorkItemIdsAsync("q", 50, default));

        Assert.Single(handler.Requests);
        Assert.Contains("TF51005", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_to_string_never_shows_the_token()
    {
        var credential = AzureDevOpsCredential.FromPat(Ado.Token);

        Assert.Equal("Basic ***", credential.ToString());
        Assert.Equal("Authorization: Basic " + Ado.TokenBase64, credential.ToGitExtraHeader());
        Assert.Equal("a *** b ***", credential.Redact($"a {Ado.Token} b {Ado.TokenBase64}"));
    }

    internal const string PullRequestJson = """
        {
          "repository": {
            "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6",
            "name": "shop-api",
            "url": "https://dev.azure.com/contoso/_apis/git/repositories/5febef5a-833d-4e14-b9c0-14cb638f91e6",
            "project": { "id": "6ce954b1-ce1f-45d1-b94d-e6bf2464ba2c", "name": "Shop", "state": "wellFormed" },
            "remoteUrl": "https://dev.azure.com/contoso/Shop/_git/shop-api"
          },
          "pullRequestId": 22,
          "codeReviewId": 22,
          "status": "active",
          "isDraft": true,
          "title": "[autopilot] Fix",
          "sourceRefName": "refs/heads/autopilot/42",
          "targetRefName": "refs/heads/main",
          "mergeStatus": "queued",
          "url": "https://dev.azure.com/contoso/_apis/git/repositories/5febef5a-833d-4e14-b9c0-14cb638f91e6/pullRequests/22",
          "supportsIterations": true
        }
        """;

    internal static string PolicyJson(int id, bool enabled, bool blocking, int approvers, bool? creatorVoteCounts, string type = PolicyTypes.MinimumReviewers) =>
        $$"""
        {
          "count": 1,
          "value": [
            {
              "createdDate": "2026-02-23T12:51:06.8887894Z",
              "isEnabled": {{(enabled ? "true" : "false")}},
              "isBlocking": {{(blocking ? "true" : "false")}},
              "isDeleted": false,
              "settings": {
                "minimumApproverCount": {{approvers}},
                {{(creatorVoteCounts is null ? string.Empty : $"\"creatorVoteCounts\": {(creatorVoteCounts.Value ? "true" : "false")},")}}
                "scope": [ { "refName": "refs/heads/main", "matchKind": "Exact", "repositoryId": "5febef5a-833d-4e14-b9c0-14cb638f91e6" } ]
              },
              "revision": 1,
              "id": {{id}},
              "url": "https://dev.azure.com/contoso/_apis/policy/configurations/{{id}}",
              "type": {
                "id": "{{type}}",
                "url": "https://dev.azure.com/contoso/_apis/policy/types/{{type}}",
                "displayName": "Minimum approval count"
              }
            }
          ]
        }
        """;
}
