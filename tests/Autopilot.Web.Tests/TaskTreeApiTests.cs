using System.Net;
using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Local;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

/// <summary>The task tree with counts per group, and moving a task under another parent.</summary>
public sealed class TaskTreeApiTests
{
    private const string Tasks = "/api/projects/demo/tasks";

    private static async Task<UiFixture> WithTasksAsync(int count)
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        for (var i = 1; i <= count; i++)
        {
            await ui.PostAsync(Tasks, new { title = "Task " + i });
        }

        return ui;
    }

    private static JsonElement Node(JsonElement list, int id) =>
        list.EnumerateArray().Select(n => Find(n, id)).First(n => n is not null)!.Value;

    private static JsonElement? Find(JsonElement node, int id)
    {
        if (node.GetProperty("id").GetInt32() == id)
        {
            return node;
        }

        return node.GetProperty("children").EnumerateArray().Select(c => Find(c, id)).FirstOrDefault(n => n is not null);
    }

    private static int[] Counts(JsonElement node)
    {
        var c = node.GetProperty("counts");
        return [c.GetProperty("done").GetInt32(), c.GetProperty("inProgress").GetInt32(), c.GetProperty("needsYou").GetInt32(), c.GetProperty("notStarted").GetInt32()];
    }

    [Fact]
    public async Task A_three_level_tree_is_nested_and_each_group_counts_its_descendant_leaves_in_one_bucket_each()
    {
        // 1 root group -> 2 (group) -> 4, 5, 6 ; 1 -> 3 (leaf) ; 7 root leaf
        await using var ui = await WithTasksAsync(7);
        var tasks = new LocalTaskService(ui.Workspace, "demo");
        tasks.SetParent(2, 1);
        tasks.SetParent(3, 1);
        tasks.SetParent(4, 2);
        tasks.SetParent(5, 2);
        tasks.SetParent(6, 2);
        tasks.Close(4);                                  // done
        await ui.PostAsync(Tasks + "/5/start");          // in progress (active implement job)
        await new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo")).SaveAsync(
            LocalTaskWorkItemSource.ToItem(tasks.Get(3)!),
            new TriageResult(TriageLabel.NeedsHuman, "Which one?", [], RiskLevel.Low, [], 0), CancellationToken.None); // needs you

        var (status, tree) = await ui.GetAsync(Tasks + "/tree");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([1, 7], tree.EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));
        var root = Node(tree, 1);
        Assert.Equal([2, 3], root.GetProperty("children").EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));
        Assert.Equal([1, 1, 1, 1], Counts(root));
        var middle = Node(tree, 2);
        Assert.Equal(1, middle.GetProperty("parentId").GetInt32());
        Assert.Equal([4, 5, 6], middle.GetProperty("children").EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));
        Assert.Equal([1, 1, 0, 1], Counts(middle));
        var leaf = Node(tree, 6);
        Assert.False(leaf.GetProperty("closed").GetBoolean());
        Assert.True(Node(tree, 4).GetProperty("closed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, leaf.GetProperty("counts").ValueKind);
        Assert.Equal(JsonValueKind.Null, Node(tree, 7).GetProperty("parentId").ValueKind);
        Assert.Equal("done", Node(tree, 4).GetProperty("status").GetString());
        Assert.Equal("in progress", Node(tree, 5).GetProperty("status").GetString());
        Assert.Equal("needs you", Node(tree, 3).GetProperty("status").GetString());
        Assert.Equal("not started", leaf.GetProperty("status").GetString());
        Assert.Equal("in progress", root.GetProperty("status").GetString());
    }

    [Fact]
    public void Tasks_tab_renders_the_tree_with_chevrons_progress_and_tokens_only()
    {
        var dir = AppContext.BaseDirectory;
        var root = dir[..dir.IndexOf("tests", StringComparison.Ordinal)];
        var js = File.ReadAllText(Path.Combine(root, "src", "Autopilot.Web", "wwwroot", "js", "tasks.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "Autopilot.Web", "wwwroot", "app.css"));
        Assert.Contains("/tasks/tree", js);
        Assert.Contains("TASKS AND GROUPS / PROGRESS", js);
        Assert.Contains("tree-chevron", js);
        Assert.Contains("`${c.done} of ${total}`", js);
        Assert.Contains("expanded.set(node.id", js);
        Assert.Contains("node.status", js);
        Assert.Matches(@"\.tasks-layout\s*\{\s*--left-col:\s*420px", css);
        Assert.Matches(@"\.tree-progress-fill\s*\{[^}]*var\(--ok\)", css);
        var treeCss = string.Join("\n", css.Split('\n').Where(l => l.Contains(".tree-") || l.Contains(".task-tree")));
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,6}\b", treeCss);
    }

    [Fact]
    public void Tasks_tab_has_row_checkboxes_a_selection_bar_bulk_move_pool_add_and_drag_to_group()
    {
        var dir = AppContext.BaseDirectory;
        var root = dir[..dir.IndexOf("tests", StringComparison.Ordinal)];
        var js = File.ReadAllText(Path.Combine(root, "src", "Autopilot.Web", "wwwroot", "js", "tasks.js"));
        var css = File.ReadAllText(Path.Combine(root, "src", "Autopilot.Web", "wwwroot", "app.css"));
        Assert.Contains("type: 'checkbox', class: 'tree-check'", js);
        Assert.Contains("ev.stopPropagation(); bulk.toggle", js);
        Assert.Contains("`${selected.size} selected`", js);
        Assert.Contains("'Move to group...'", js);
        Assert.Contains("'Add to the pool'", js);
        Assert.Contains("'Close'", js);
        Assert.Contains("bar.hidden = selected.size === 0", js);
        Assert.Contains("/parent`, { parentId }", js);
        Assert.Contains("/pool`, { id: String(id) }", js);
        Assert.DoesNotContain("{ id: Number(id) }", js);
        Assert.Contains("groups.map(g => h('option'", js);
        Assert.Contains("draggable: 'true'", js);
        Assert.Contains("ondrop:", js);
        Assert.Contains("bulk.drop(", js);
        Assert.Contains("[hidden]", css);
    }

    [Fact]
    public async Task Moving_a_task_shows_it_under_the_new_parent_and_null_makes_it_a_root()
    {
        await using var ui = await WithTasksAsync(3);

        var (status, _) = await ui.PostAsync(Tasks + "/3/parent", new { parentId = 1 });
        Assert.Equal(HttpStatusCode.OK, status);
        var (_, tree) = await ui.GetAsync(Tasks + "/tree");
        Assert.Equal([3], Node(tree, 1).GetProperty("children").EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));

        await ui.PostAsync(Tasks + "/3/parent", new { parentId = 2 });
        (_, tree) = await ui.GetAsync(Tasks + "/tree");
        Assert.Empty(Node(tree, 1).GetProperty("children").EnumerateArray());
        Assert.Equal(2, Node(tree, 3).GetProperty("parentId").GetInt32());

        await ui.PostAsync(Tasks + "/3/parent", new { parentId = (int?)null });
        (_, tree) = await ui.GetAsync(Tasks + "/tree");
        Assert.Equal([1, 2, 3], tree.EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task A_task_as_its_own_parent_or_under_a_descendant_is_rejected_and_the_parent_is_kept()
    {
        await using var ui = await WithTasksAsync(3);
        var tasks = new LocalTaskService(ui.Workspace, "demo");
        tasks.SetParent(2, 1);
        tasks.SetParent(3, 2);

        var (self, _) = await ui.PostAsync(Tasks + "/1/parent", new { parentId = 1 });
        var (child, _) = await ui.PostAsync(Tasks + "/1/parent", new { parentId = 2 });
        var (grandchild, _) = await ui.PostAsync(Tasks + "/1/parent", new { parentId = 3 });

        foreach (var status in new[] { self, child, grandchild })
        {
            Assert.InRange((int)status, 400, 499);
        }

        Assert.Null(tasks.Get(1)!.ParentNumber);
        Assert.Equal(1, tasks.Get(2)!.ParentNumber);
        Assert.Equal(2, tasks.Get(3)!.ParentNumber);
    }

    [Fact]
    public async Task An_unknown_task_or_parent_is_rejected_and_changes_nothing()
    {
        await using var ui = await WithTasksAsync(2);
        var tasks = new LocalTaskService(ui.Workspace, "demo");
        tasks.SetParent(2, 1);

        var (unknownTask, _) = await ui.PostAsync(Tasks + "/99/parent", new { parentId = 1 });
        var (unknownParent, _) = await ui.PostAsync(Tasks + "/2/parent", new { parentId = 99 });

        Assert.Contains(unknownTask, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        Assert.Contains(unknownParent, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        Assert.Equal(1, tasks.Get(2)!.ParentNumber);
        Assert.Null(tasks.Get(99));
    }

    [Fact]
    public async Task A_tracker_project_gets_the_same_conflict_as_other_task_writes()
    {
        await using var ui = new UiFixture();
        var (created, body) = await ui.PostAsync("/api/projects", ui.Draft("azure", "azure-devops"));
        Assert.True(created == HttpStatusCode.Created, body.ToString());

        var (status, _) = await ui.PostAsync("/api/projects/azure/tasks/1/parent", new { parentId = (int?)null });
        var (write, _) = await ui.PostAsync("/api/projects/azure/tasks", new { title = "x" });

        Assert.Equal(HttpStatusCode.Conflict, write);
        Assert.Equal(HttpStatusCode.Conflict, status);
    }
}
