using System.Net;
using Autopilot.Core.Pool;

namespace Autopilot.Web.Tests;

public sealed class PoolPauseWebTests
{
    private const string Base = "/api/projects/demo/pool";

    private static async Task<UiFixture> WithProjectAsync()
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        return ui;
    }

    [Fact]
    public async Task Post_pause_sets_the_person_pause_returns_the_status_shape_and_is_idempotent()
    {
        await using var ui = await WithProjectAsync();

        var (status, body) = await ui.PostAsync($"{Base}/pause");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Paused", body.GetProperty("status").GetString());
        Assert.Equal("None", body.GetProperty("subStatus").GetString());
        Assert.Equal("Paused by a person", body.GetProperty("pause").GetProperty("reason").GetString());
        Assert.True(body.TryGetProperty("settings", out _));
        Assert.True(body.TryGetProperty("slots", out _));

        var created = body.GetProperty("pause").GetProperty("createdAt").GetDateTimeOffset();
        var (_, again) = await ui.PostAsync($"{Base}/pause");
        Assert.Equal("Paused", again.GetProperty("status").GetString());
        Assert.Equal(created, again.GetProperty("pause").GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, again.GetProperty("pause").GetProperty("retryAt").ValueKind);
    }

    [Fact]
    public async Task The_paused_status_shows_finishing_current_tasks_only_while_a_task_runs()
    {
        await using var ui = await WithProjectAsync();
        var state = new PoolStateStore(ui.Workspace, "demo");
        state.Beat(Environment.ProcessId, DateTimeOffset.UtcNow); // a live run, so its slots are not released
        state.SetSlot("1", "running", "slot 1", DateTimeOffset.UtcNow);

        var (_, paused) = await ui.PostAsync($"{Base}/pause");
        Assert.Equal("Paused", paused.GetProperty("status").GetString());
        Assert.Equal("finishing current tasks", paused.GetProperty("subStatus").GetString());

        state.ClearSlot("1");
        var (_, idle) = await ui.GetAsync($"{Base}/status");
        Assert.Equal("Paused", idle.GetProperty("status").GetString());
        Assert.Equal("None", idle.GetProperty("subStatus").GetString());
    }

    [Fact]
    public async Task Resume_lifts_the_person_pause()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync($"{Base}/pause");

        var (_, resumed) = await ui.PostAsync($"{Base}/resume");
        Assert.True(resumed.GetProperty("resumed").GetBoolean());

        var (_, after) = await ui.GetAsync($"{Base}/status");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, after.GetProperty("pause").ValueKind);
        Assert.NotEqual("Paused", after.GetProperty("status").GetString());
    }

    [Fact]
    public void Status_derivation_is_only_for_a_person_pause()
    {
        var now = DateTimeOffset.UtcNow;
        var running = new[] { new PoolSlot("1", "running", null, now) };
        var waiting = new[] { new PoolSlot("1", "waiting", "x", now) };
        var person = new PoolPause("Paused by a person", null, now);

        Assert.Equal(("Paused", "finishing current tasks"), PoolOperations.PersonPauseStatus(person, running));
        Assert.Equal(("Paused", "None"), PoolOperations.PersonPauseStatus(person, waiting));
        Assert.Null(PoolOperations.PersonPauseStatus(new PoolPause("Budget used up", now.AddHours(1), now), running));
        Assert.Null(PoolOperations.PersonPauseStatus(null, running));
    }
}
