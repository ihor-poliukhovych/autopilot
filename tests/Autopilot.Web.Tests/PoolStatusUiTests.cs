namespace Autopilot.Web.Tests;

/// <summary>Reads files of the web project from the repository, found by walking up from the test folder.</summary>
internal static class WwwrootFiles
{
    public static string Read(string relative) => ReadSource(Path.Combine("wwwroot", relative));

    public static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative + " not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The full path of a file under wwwroot, for tools that need a path.</summary>
    public static string FullPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", relative);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}

/// <summary>What the Pool page is made of: the source of the page, checked for the parts the design asks for.</summary>
public sealed class PoolStatusUiTests
{
    private static string Wwwroot(string relative) => WwwrootFiles.Read(relative);

    [Fact]
    public void Stepper_has_the_five_steps_in_order_and_reads_status_and_sub_status()
    {
        var js = Wwwroot("js/poolstatus.js");
        var order = new[] { "'Planned'", "'Running'", "'Waiting for you'", "'Final PR'", "'Done'" };
        var at = js.IndexOf("export const STEPS = [", StringComparison.Ordinal);
        Assert.True(at >= 0);
        foreach (var step in order)
        {
            var next = js.IndexOf(step, at, StringComparison.Ordinal);
            Assert.True(next >= at, step);
            at = next;
        }

        Assert.Contains("status?.status", js);
        Assert.Contains("status?.subStatus", js);
        Assert.Contains("'None'", js);
        Assert.Contains("stepper(status)", Wwwroot("js/pool.js"));
        Assert.Contains("'aria-current'", Wwwroot("js/pool.js"));
    }

    [Fact]
    public void Strip_queue_and_events_use_the_status_fields()
    {
        var status = Wwwroot("js/poolstatus.js");
        var js = Wwwroot("js/pool.js");
        Assert.Contains("status?.progress?.done", status);
        Assert.Contains("status?.queue", status);
        Assert.Contains("status?.timeline", status);
        Assert.Contains("status?.agentRunsToday", status);
        Assert.Contains("entry?.taskId", status);
        Assert.Contains("rowPill(row, slot)", js);
        Assert.Contains("queueWindow(rows, showAllRows)", js);
        Assert.Contains("eventWindow(status?.timeline, showAllEvents)", js);
        Assert.Contains("/pool/status", js);
        Assert.Contains("Nothing has run yet", js);
    }

    [Fact]
    public void Status_is_read_again_by_the_existing_poll_and_an_open_menu_is_not_redrawn()
    {
        var js = Wwwroot("js/pool.js");
        Assert.Contains("POLL_MS = 5000", js);
        var refresh = js[js.IndexOf("async function refresh", StringComparison.Ordinal)..];
        refresh = refresh[..refresh.IndexOf("// ----- the run confirmation", StringComparison.Ordinal)];
        Assert.Contains("/pool/status", refresh);
        Assert.Contains("if (menuOpen) afterMenu = redraw", refresh);
    }

    [Fact]
    public void Queue_rows_have_one_pill_one_menu_with_the_five_actions_and_44px_targets()
    {
        var js = Wwwroot("js/pool.js");
        var css = Wwwroot("app.css");
        foreach (var label in new[] { "'Move to top'", "'Move up'", "'Move down'", "'Open task'", "'Remove from pool'" })
        {
            Assert.Contains(label, js);
        }

        Assert.Contains("'aria-haspopup': 'menu'", js);
        Assert.Contains("role: 'menuitem'", js);
        Assert.Contains("ev.key === 'Escape'", js);
        Assert.Contains("ev.key === 'ArrowDown'", js);
        Assert.Matches(@"\.pool-q\s*\{[^}]*height: 44px", css);
        Assert.Matches(@"button\.pool-kebab\s*\{[^}]*width: 44px; height: 44px", css);
    }

    [Fact]
    public void Run_dialog_uses_the_existing_start_request_with_a_trapped_focus()
    {
        var js = Wwwroot("js/pool.js");
        Assert.Contains("'Confirm and run'", js);
        Assert.Contains("/pool/run", js);
        Assert.Contains("Nothing starts until you confirm.", js);
        var dom = Wwwroot("js/dom.js");
        Assert.Contains("trapTab(dialog, ev)", dom);
        Assert.Contains("dialog.showModal()", dom);
        Assert.Contains("'cancel'", dom);
    }

    [Fact]
    public void Settings_sit_in_the_left_column_and_status_events_and_queue_in_the_right()
    {
        var js = Wwwroot("js/pool.js");
        var css = Wwwroot("app.css");
        var unattended = Wwwroot("js/poolunattended.js");
        Assert.Contains("app-left pool-settings stack' }, extra.element", js);
        Assert.Contains("heroCard, h('div', { class: 'pool-main' }, queue, events), extra.right", js);
        Assert.Matches(@"\.pool-layout\s*\{[^}]*var\(--left-col\)", css);
        Assert.Matches(@"--left-col:\s*340px", css);
        // The settings are one details element: closed on a phone, one line per setting.
        Assert.Contains("h('details', { class: 'card pool-panel', open: true", unattended);
        Assert.Contains("if (phone.matches && details) details.open = false", js);
        Assert.Contains("place(live, finalPrCard(key, status, reload), parkedCard(key, status, reload), decisionsCard(key, status, reload))", unattended);
        Assert.Contains("right: live", unattended);
    }

    [Fact]
    public void Phone_order_is_status_then_events_then_queue_then_settings()
    {
        var css = Wwwroot("app.css");
        var phone = css[css.IndexOf("/* Phone and tablet: status first", StringComparison.Ordinal)..];
        Assert.Matches(@"\.pool-hero\s*\{\s*order: 1", phone);
        Assert.Matches(@"\.pool-events\s*\{\s*order: 2", phone);
        Assert.Matches(@"\.pool-queue\s*\{\s*order: 3", phone);
        Assert.Matches(@"\.pool-layout \.app-left > \*\s*\{\s*order: 4", phone);
    }

    [Fact]
    public void Integration_block_shows_five_newest_collapsed_and_all_when_toggled()
    {
        var js = Wwwroot("js/poolintegration.js");
        Assert.Contains("COLLAPSED_COUNT = 5", js);
        Assert.Contains("sorted.slice(0, COLLAPSED_COUNT)", js);
        Assert.Contains("new Date(b.mergedAt) - new Date(a.mergedAt)", js);
        Assert.Contains("sorted.length > COLLAPSED_COUNT", js);
        Assert.Contains("`Show all ${sorted.length}`", js);
        Assert.Contains("'Show fewer'", js);
        Assert.Contains("tasks'} merged", js);
        Assert.Contains("#${m.workItemId} (${m.taskBranch})", js);
        Assert.Contains("mergesList(i.merges)", Wwwroot("js/poolunattended.js"));
        var css = Wwwroot("app.css");
        var block = css[css.IndexOf("/* Integration branch block", StringComparison.Ordinal)..css.IndexOf("/* Phone and tablet: status first", StringComparison.Ordinal)];
        Assert.DoesNotContain("max-height", block);
        Assert.DoesNotContain("overflow", block);
    }

    [Fact]
    public void Integration_block_groups_the_expanded_list_by_day()
    {
        var js = Wwwroot("js/poolintegration.js");
        Assert.Contains("'Today'", js);
        Assert.Contains("'Yesterday'", js);
        Assert.Contains("toLocaleDateString", js);
        Assert.Contains("groupByDay(sorted)", js);
        Assert.Contains("getFullYear(), d.getMonth(), d.getDate()", js);
    }

    [Fact]
    public void Integration_block_remembers_the_choice_and_defaults_to_collapsed()
    {
        var js = Wwwroot("js/poolintegration.js");
        Assert.Contains("localStorage.getItem(EXPANDED_KEY) === 'true'", js);
        Assert.Contains("localStorage.setItem(EXPANDED_KEY", js);
        Assert.Contains("catch { return false; }", js);
    }

    [Fact]
    public void An_override_marker_is_drawn_only_where_the_api_sends_the_project_default()
    {
        var unattended = Wwwroot("js/poolunattended.js");
        Assert.Contains("overrides(s)", unattended);
        Assert.Contains("Project default:", unattended);
        // The backend stores no project default for a pool setting, so it sends no `defaults` and nothing is marked.
        Assert.DoesNotContain("Defaults", WwwrootFiles.ReadSource("Operations.cs"));
    }
}
