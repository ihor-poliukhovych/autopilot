using System.Text.RegularExpressions;

namespace Autopilot.Web.Tests;

public sealed class NavigationLayoutTests
{
    private static string Wwwroot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative + " not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Tab_bar_is_exactly_Inbox_Tasks_Pipeline_Pool_Settings_in_order()
    {
        var js = Wwwroot("js/app.js");
        var block = Regex.Match(js, @"const TABS = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var labels = Regex.Matches(block, @"\['(\w+)', '([^']+)'").Select(m => m.Groups[2].Value).ToArray();
        Assert.Equal(new[] { "Inbox", "Tasks", "Pipeline", "Pool", "Settings" }, labels);
    }

    [Fact]
    public void A_project_address_without_a_tab_lands_on_Inbox()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("tab: parts[2] || 'inbox'", js);
        Assert.Contains("let lastTab = 'inbox'", js);
    }

    [Fact]
    public void Old_runs_run_and_needs_addresses_still_route_to_their_pages()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("const LEGACY = { runs: runs, run: runs, needs: needs }", js);
        Assert.Contains("r.tab === 'run' ? 'runs'", js);
    }

    [Fact]
    public void Inbox_lists_questions_and_parked_tasks_from_the_inbox_api()
    {
        var js = Wwwroot("js/inbox.js");
        Assert.Contains("/inbox`", js);
        Assert.Contains("needs.render(", js);
        Assert.Contains("/pool/parked/", js);
        Assert.Contains("'Unpark'", js);
        Assert.Contains("Nothing waits for you", js);
        Assert.Contains("class: 'app-layout'", js);
        Assert.DoesNotContain("content comes later", js);
        Assert.Contains("/inbox`", Wwwroot("js/app.js"));
    }

    [Fact]
    public void Inbox_cards_are_left_aligned_clamped_and_select_the_first_item()
    {
        var js = Wwwroot("js/inbox.js");
        var css = Wwwroot("app.css");
        Assert.Contains("class: 'inbox-id' }, `#${item.id}`", js);
        Assert.DoesNotContain("`#${item.id} ${item.title}`, item.kind", js);
        Assert.Contains("class: 'inbox-title' }, item.title", js);
        Assert.Contains("'aria-current': same(item, selected) ? 'true' : null", js);
        Assert.Contains("selected = items[0]", js);
        Assert.DoesNotContain("Pick an item on the left", js);
        Assert.Contains("Nothing waits for you", js);
        Assert.Contains(".inbox-card .inbox-title", css);
        Assert.Contains("-webkit-line-clamp: 2", css);
        Assert.Contains("-webkit-line-clamp: 3", css);
        Assert.Contains(".inbox-card.selected { border-left-color: var(--accent)", css);
        Assert.Contains("[data-part=\"inbox-detail\"].is-sticky { position: sticky", css);
    }

    [Fact]
    public void Css_defines_left_col_340px_and_the_shared_layout_class()
    {
        var css = Wwwroot("app.css");
        Assert.Matches(@"--left-col:\s*340px", css);
        Assert.Matches(@"\.app-layout\s*\{[^}]*grid-template-columns:\s*var\(--left-col\)\s+minmax\(0,\s*1fr\)", css);
        Assert.Matches(@"@media \(max-width: 760px\)\s*\{[^@]*\.app-right\s*\{[^}]*position: static", css);
    }

    [Fact]
    public void Shell_reserves_no_empty_left_column_and_pages_draw_their_own_layout()
    {
        var js = Wwwroot("js/app.js");
        Assert.DoesNotContain("LAYOUT", js);
        Assert.DoesNotContain("class: 'app-layout'", js);
        Assert.DoesNotContain("class: 'app-left'", js);
        Assert.Contains("view: stage", js);
        Assert.Contains("class: 'app-layout pool-layout'", Wwwroot("js/pool.js"));
        Assert.Contains("class: 'app-layout tasks-layout'", Wwwroot("js/tasks.js"));
    }

    [Fact]
    public void Tasks_tab_keeps_the_list_left_and_shows_the_selected_task_right()
    {
        var js = Wwwroot("js/tasks.js");
        var css = Wwwroot("app.css");
        Assert.Contains("h('div', { class: 'app-layout tasks-layout', 'data-page': 'tasks' }, left, right)", js);
        Assert.Contains("class: 'app-left stack', 'data-part': 'task-list'", js);
        Assert.Contains("class: 'app-right open', 'data-part': 'task-detail'", js);
        Assert.Contains("renderList({ ...ctx, view: left }", js);
        Assert.Contains("renderDetail({ ...ctx, view: right }", js);
        Assert.Contains("onclick: () => select(item.id)", js);
        Assert.Contains("sel.id = String(data.items[0].id)", js);
        Assert.DoesNotContain("All tasks", js);
        Assert.Matches(@"\.tasks-layout\s*\{[^}]*var\(--left-col\)\s+minmax\(0,\s*1fr\)", css);
    }

    [Fact]
    public void Pool_and_tasks_collapse_to_one_column_without_overflow_at_800px_and_fill_two_at_1400px()
    {
        var css = Wwwroot("app.css");
        Assert.Matches(@"@media \(max-width: 900px\)\s*\{[^@]*\.pool-layout,\s*\.tasks-layout\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\)", css);
        Assert.Matches(@"\.pool-layout \.app-right,\s*\.tasks-layout \.app-right\s*\{[^}]*position: static[^}]*transform: none", css);
        Assert.Contains("overflow-x: hidden", css);
        // Above 900px the left column is a fixed width and the right one takes all the rest, so no gap is left.
        Assert.Matches(@"\.tasks-layout\s*\{[^}]*var\(--left-col\)\s+minmax\(0,\s*1fr\)", css);
        Assert.Matches(@"\.pool-layout\s*\{[^}]*var\(--left-col\)\s+minmax\(0,\s*1fr\)", css);
    }

    [Fact]
    public void Pipeline_page_has_one_layout_with_chips_left_and_detail_or_hint_right()
    {
        var js = Wwwroot("js/pipeline.js");
        var css = Wwwroot("app.css");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(js, "class: 'app-layout'"));
        Assert.DoesNotContain("app-right open' }, h('div', { class: 'app-layout'", js);
        Assert.Contains("h('div', { class: 'pipeline-page' }, filterBar,", js);
        Assert.Contains("h('div', { class: 'app-left' }, list)", js);
        Assert.Contains("'Select a task'", js);
        Assert.DoesNotContain("Waiting for a person", js);
        Assert.DoesNotContain("waitingPanel", js);
        Assert.Contains("link(key, 'inbox') }, 'Inbox'", js);
        Assert.Contains("link(key, 'runs') }, 'Runs'", js);
        Assert.Contains("class: 'pipeline-filters'", js);
        Assert.Matches(@"\.pipeline-filters\s*\{[^}]*flex-wrap: wrap", css);
        Assert.DoesNotMatch(@"\.pipeline-filters\s*\{[^}]*overflow", css);
        Assert.DoesNotContain("pipeline-list\"]", css);
    }

    [Fact]
    public void Pages_scroll_as_one_document_with_no_inner_scroll_containers()
    {
        var css = Wwwroot("app.css");
        // Only code and log blocks (pre) may scroll inside themselves; no other rule may set overflow auto/scroll.
        foreach (var rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}").Select(m => (Sel: m.Groups[1].Value.Trim(), Body: m.Groups[2].Value)))
        {
            if (Regex.IsMatch(rule.Body, @"overflow(-x|-y)?:\s*(auto|scroll)"))
                Assert.True(rule.Sel == "pre", "inner scroll on: " + rule.Sel);
        }
        Assert.DoesNotMatch(@"max-height:\s*calc\(100vh", css);
        Assert.Matches(@"\.tabs\s*\{[^}]*flex-wrap: wrap", css);
        Assert.Matches(@"\.app-right > \.is-sticky\s*\{[^}]*position: sticky", css);
        Assert.Contains("keepStickyFit", Wwwroot("js/pipeline.js"));
    }

    [Fact]
    public void Left_column_is_filled_and_never_hidden_as_empty()
    {
        var js = Wwwroot("js/app.js");
        var css = Wwwroot("app.css");
        Assert.DoesNotContain("function leftColumn(", js);
        Assert.DoesNotContain("app-links", js);
        Assert.DoesNotContain("app-links", css);
        Assert.DoesNotContain(":empty", css);
    }

    [Fact]
    public void Phone_width_never_covers_the_page_with_a_drawer()
    {
        var css = Wwwroot("app.css");
        Assert.DoesNotMatch(@"\.app-right[^{]*\{[^}]*position: fixed", css);
        Assert.Matches(@"\.drawer-open,\s*\.drawer-close\s*\{\s*display:\s*none", css);
    }

    [Fact]
    public void Phone_block_keeps_topbar_on_one_row_and_tabs_scrollable()
    {
        var css = Wwwroot("app.css");
        var blocks = Regex.Matches(css, @"@media \(max-width: 640px\) \{(.*?)\n\}", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToArray();
        var block = Assert.Single(blocks, b => b.Contains(".topbar {"));
        Assert.Matches(@"\.topbar\s*\{[^}]*flex-wrap:\s*nowrap", block);
        Assert.Matches(@"\.topbar > \*\s*\{[^}]*flex-shrink:\s*0", block);
        Assert.Matches(@"\.tabs\s*\{[^}]*flex-wrap:\s*wrap", block);
        Assert.Matches(@"\.tabs a\s*\{[^}]*flex-shrink:\s*0[^}]*white-space:\s*nowrap", block);
    }

    [Fact]
    public void Legacy_lookup_ignores_inherited_object_keys()
    {
        var js = Wwwroot("js/app.js");
        Assert.Contains("Object.hasOwn(LEGACY, r.tab)", js);
        Assert.DoesNotContain("LEGACY[r.tab] ?", js);
        Assert.DoesNotContain("LEGACY[page] ?", js);
    }
}
