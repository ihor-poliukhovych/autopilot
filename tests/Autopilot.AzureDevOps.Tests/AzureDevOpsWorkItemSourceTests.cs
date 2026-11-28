using System.Text.Json;
using Autopilot.Core;

namespace Autopilot.AzureDevOps.Tests;

public class AzureDevOpsWorkItemSourceTests
{
    private const string BatchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 42,
              "rev": 5,
              "fields": {
                "System.Id": 42,
                "System.Title": "Return 404 for unknown products",
                "System.Description": "<div>The endpoint returns <b>500</b>.</div><ul><li>Seen in the logs</li></ul>",
                "Microsoft.VSTS.Common.AcceptanceCriteria": "<ol><li>GET /products/999 returns 404</li></ol>",
                "System.Tags": "autopilot; backend; autopilot:needs-human",
                "System.State": "New",
                "System.WorkItemType": "Bug"
              },
              "url": "https://dev.azure.com/contoso/_apis/wit/workItems/42"
            }
          ]
        }
        """;

    private static FakeHandler Handler() => new FakeHandler()
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(42))
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", BatchJson)
        .On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/42", "{\"id\":42,\"rev\":6,\"fields\":{}}")
        .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/wit/workItems/42/comments", "{\"count\":0,\"comments\":[]}")
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workItems/42/comments", "{\"workItemId\":42,\"id\":1,\"version\":1,\"text\":\"x\"}");

    [Fact]
    public async Task List_runs_the_query_and_maps_fields_to_work_items()
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        var item = Assert.Single(await source.ListOpenAsync(default));

        Assert.Equal("42", item.Id);
        Assert.Equal("Return 404 for unknown products", item.Title);
        Assert.Equal("The endpoint returns 500.\n\n- Seen in the logs\n\n## Acceptance criteria\n\n1. GET /products/999 returns 404", item.Body);
        Assert.Equal(["autopilot", "backend", "autopilot:needs-human"], item.Labels);
        var query = JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("query").GetString();
        Assert.Equal(AzureDevOpsSettings.DefaultQuery, query);
        Assert.Contains("$top=50", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_query_result_makes_no_batch_call()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        Assert.Empty(await source.ListOpenAsync(default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Get_with_a_non_number_returns_null_without_a_request(string id)
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        Assert.Null(await source.GetAsync(id, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_of_a_missing_item_returns_null()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":1,\"value\":[null]}");
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        Assert.Null(await source.GetAsync("7", default));
    }

    [Fact]
    public async Task Triage_replaces_the_prefixed_tag_keeps_others_and_adds_one_comment()
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());
        var result = new TriageResult(TriageLabel.AgentReady, "Clear bug with <one> fix & a test.", ["GET /products/999 returns 404", "Existing tests pass"], RiskLevel.Low);

        await source.RecordTriageAsync("42", result, default);

        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var ops = JsonDocument.Parse(patch.Body).RootElement.EnumerateArray().ToList();
        Assert.Equal(5, ops[0].GetProperty("value").GetInt32());
        Assert.Equal("autopilot; backend; autopilot:agent-ready", ops[1].GetProperty("value").GetString());

        var comment = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal));
        var text = JsonDocument.Parse(comment.Body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Written by Autopilot", text, StringComparison.Ordinal);
        Assert.Contains("agent-ready", text, StringComparison.Ordinal);
        Assert.Contains("Clear bug with &lt;one&gt; fix &amp; a test.", text, StringComparison.Ordinal);
        Assert.Contains("<li>GET /products/999 returns 404</li>", text, StringComparison.Ordinal);
        Assert.Contains("<li>Existing tests pass</li>", text, StringComparison.Ordinal);
        Assert.Contains("<b>Risk:</b> low", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_with_the_same_tag_skips_the_patch_but_still_comments()
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        await source.RecordTriageAsync("42", new TriageResult(TriageLabel.NeedsHuman, "Needs a product decision.", [], RiskLevel.Medium), default);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        var comment = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal));
        Assert.Contains("needs-human", comment.Body, StringComparison.Ordinal);
        Assert.Contains("(none)", comment.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "autopilot:agent-ready")]
    [InlineData("", "autopilot:agent-ready")]
    [InlineData("autopilot", "autopilot; autopilot:agent-ready")]
    [InlineData("Autopilot:needs-human; ui; autopilot:old", "ui; autopilot:agent-ready")]
    [InlineData("a;b ;  c", "a; b; c; autopilot:agent-ready")]
    public void Replace_triage_tag(string? tags, string expected) =>
        Assert.Equal(expected, AzureDevOpsWorkItemSource.ReplaceTriageTag(tags, "autopilot:", TriageLabel.AgentReady));

    [Fact]
    public void Custom_prefix_is_used_for_the_new_tag() =>
        Assert.Equal("ai/needs-human", AzureDevOpsWorkItemSource.ReplaceTriageTag("ai/agent-ready", "ai/", TriageLabel.NeedsHuman));

    [Fact]
    public void Body_without_description_is_only_the_criteria()
    {
        var item = new AdoWorkItem(5, 1, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = JsonDocument.Parse("\"T\"").RootElement,
            ["Microsoft.VSTS.Common.AcceptanceCriteria"] = JsonDocument.Parse("\"<p>Works</p>\"").RootElement,
        });

        var mapped = AzureDevOpsWorkItemSource.ToWorkItem(item);

        Assert.Equal("## Acceptance criteria\n\nWorks", mapped.Body);
        Assert.Empty(mapped.Labels);
        Assert.Null(mapped.Iteration);
    }

    [Fact]
    public void Iteration_path_is_carried_to_the_work_item()
    {
        var item = new AdoWorkItem(6, 1, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = JsonDocument.Parse("\"T\"").RootElement,
            ["System.IterationPath"] = JsonDocument.Parse("\"Go-OPS\\\\2026\\\\7.14\"").RootElement,
        });

        Assert.Equal("Go-OPS\\2026\\7.14", AzureDevOpsWorkItemSource.ToWorkItem(item).Iteration);
    }
}
