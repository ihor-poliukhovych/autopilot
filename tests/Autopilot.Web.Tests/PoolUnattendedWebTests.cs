using System.Net;
using Autopilot.Core.Pool;

namespace Autopilot.Web.Tests;

public sealed class PoolUnattendedWebTests
{
    private static string Pool(string path = "") => "/api/projects/demo/pool" + path;

    private static async Task<UiFixture> WithProjectAsync()
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        return ui;
    }

    [Fact]
    public async Task The_status_shows_the_defaults_and_the_slider_is_saved_and_checked()
    {
        await using var ui = await WithProjectAsync();

        var (status, body) = await ui.GetAsync(Pool("/status"));
        Assert.Equal(HttpStatusCode.OK, status);
        var settings = body.GetProperty("settings");
        Assert.False(settings.GetProperty("unattended").GetBoolean());
        Assert.Equal(1, settings.GetProperty("concurrency").GetInt32());
        Assert.Equal(4, settings.GetProperty("maxConcurrency").GetInt32());
        Assert.True(settings.GetProperty("integrationEnabled").GetBoolean());
        Assert.Empty(body.GetProperty("slots").EnumerateArray());
        Assert.False(body.GetProperty("integration").GetProperty("branch").ValueKind == System.Text.Json.JsonValueKind.String);

        var (saved, view) = await ui.SendAsync(HttpMethod.Put, Pool("/settings"), new { concurrency = 3, unattended = true });
        Assert.Equal(HttpStatusCode.OK, saved);
        Assert.Equal(3, view.GetProperty("concurrency").GetInt32());
        Assert.True(view.GetProperty("unattended").GetBoolean());
        var (_, again) = await ui.GetAsync(Pool("/status"));
        Assert.Equal(3, again.GetProperty("settings").GetProperty("concurrency").GetInt32());

        var (refused, error) = await ui.SendAsync(HttpMethod.Put, Pool("/settings"), new { concurrency = 7 });
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("pool.concurrency", error.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_without_an_override_row_gets_its_autopilot_json_defaults_with_source_project()
    {
        await using var ui = await WithProjectAsync();

        var (_, body) = await ui.GetAsync(Pool("/status"));
        var settings = body.GetProperty("settings");
        var effective = settings.GetProperty("effective");
        foreach (var name in new[] { "unattended", "concurrency", "masterEnabled", "budgetStopPercent", "integrationEnabled" })
        {
            Assert.Equal("project", effective.GetProperty(name).GetProperty("source").GetString());
        }

        Assert.False(effective.GetProperty("unattended").GetProperty("value").GetBoolean());
        Assert.Equal(1, effective.GetProperty("concurrency").GetProperty("value").GetInt32());
        Assert.Equal(80, effective.GetProperty("budgetStopPercent").GetProperty("value").GetInt32());
        Assert.True(effective.GetProperty("integrationEnabled").GetProperty("value").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, settings.GetProperty("override").ValueKind);
    }

    [Fact]
    public async Task The_pool_override_is_saved_shown_with_its_source_and_cleared()
    {
        await using var ui = await WithProjectAsync();

        var (saved, view) = await ui.SendAsync(HttpMethod.Put, Pool("/settings/override"), new { concurrency = 3, budgetStopPercent = 55 });
        Assert.Equal(HttpStatusCode.OK, saved);
        Assert.Equal(3, view.GetProperty("concurrency").GetInt32());
        var effective = view.GetProperty("effective");
        Assert.Equal("pool", effective.GetProperty("concurrency").GetProperty("source").GetString());
        Assert.Equal(55, effective.GetProperty("budgetStopPercent").GetProperty("value").GetInt32());
        Assert.Equal("project", effective.GetProperty("unattended").GetProperty("source").GetString());

        var (_, status) = await ui.GetAsync(Pool("/status"));
        Assert.Equal("pool", status.GetProperty("settings").GetProperty("effective").GetProperty("concurrency").GetProperty("source").GetString());

        var (refused, _) = await ui.SendAsync(HttpMethod.Put, Pool("/settings/override"), new { concurrency = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, refused);

        var (cleared, back) = await ui.SendAsync(HttpMethod.Delete, Pool("/settings/override"), null);
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.Equal(1, back.GetProperty("concurrency").GetInt32());
        Assert.Equal("project", back.GetProperty("effective").GetProperty("concurrency").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Decisions_and_parked_tasks_are_listed_and_a_decision_can_be_undone()
    {
        await using var ui = await WithProjectAsync();
        var state = new PoolStateStore(ui.Workspace, "demo");
        var decision = state.AddDecision("1", DecisionKinds.Answer, "Which order?", "By name.", "Recommended.", "other", DateTimeOffset.UtcNow);
        state.Park("2", "paid", "It costs money.", DateTimeOffset.UtcNow);
        state.Pause("No network.", DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow);

        var (_, status) = await ui.GetAsync(Pool("/status"));
        Assert.Equal("By name.", status.GetProperty("decisions")[0].GetProperty("answer").GetString());
        Assert.Equal("paid", status.GetProperty("parked")[0].GetProperty("category").GetString());
        Assert.Equal("No network.", status.GetProperty("pause").GetProperty("reason").GetString());

        var (_, reverted) = await ui.PostAsync(Pool($"/decisions/{decision.Id}/revert"));
        Assert.True(reverted.GetProperty("reverted").GetBoolean());
        var (_, twice) = await ui.PostAsync(Pool($"/decisions/{decision.Id}/revert"));
        Assert.False(twice.GetProperty("reverted").GetBoolean());
        Assert.Equal(1, state.RevertedCount());

        var (_, unparked) = await ui.PostAsync(Pool("/parked/2/unpark"));
        Assert.True(unparked.GetProperty("unparked").GetBoolean());
        Assert.Equal(["1", "2"], new PoolService(ui.Workspace, "demo").List().Select(e => e.WorkItemId));
        var (_, resumed) = await ui.PostAsync(Pool("/resume"));
        Assert.True(resumed.GetProperty("resumed").GetBoolean());
    }

    [Fact]
    public async Task A_dated_schedule_is_planned_confirmed_shown_and_cancelled()
    {
        await using var ui = await WithProjectAsync();
        var path = Pool("/schedule");
        var body = new { at = "2099-01-05 02:00", repeat = "none", until = "06:00", limit = 5, unattended = true };

        var (planned, plan) = await ui.PostAsync(path + "/plan", body);
        Assert.Equal(HttpStatusCode.OK, planned);
        Assert.Contains("on 2099-01-05 at 02:00", plan.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains("stops at 80%", plan.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, plan.GetProperty("timeZone").GetString());
        Assert.Empty(ui.Scheduler.Jobs);

        var (unconfirmed, _) = await ui.PostAsync(path, body);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed);

        var (_, shown) = await ui.PostAsync(path, new { at = "2099-01-05 02:00", repeat = "none", until = "06:00", limit = 5, unattended = true, confirmed = true });
        Assert.True(shown.GetProperty("set").GetBoolean());
        Assert.Equal("none", shown.GetProperty("plan").GetProperty("repeat").GetString());
        Assert.Equal("06:00", shown.GetProperty("plan").GetProperty("until").GetString());
        Assert.True(shown.GetProperty("plan").GetProperty("unattended").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, shown.GetProperty("next").ValueKind);
        Assert.Equal(2, ui.Scheduler.Jobs.Count); // the run and its catch-up

        var (_, removed) = await ui.SendAsync(HttpMethod.Delete, path);
        Assert.True(removed.GetProperty("removed").GetBoolean());
        Assert.Empty(ui.Scheduler.Jobs);
    }
}
