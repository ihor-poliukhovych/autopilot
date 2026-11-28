using Autopilot.Core.Workspace;

namespace Autopilot.Web;

public sealed record TaskBody(string? Title, string? Body, string[]? Labels);

public sealed record TextBody(string? Text);

public sealed record IdBody(string? Id);

public sealed record ParentBody(int? ParentId);

public sealed record MoveBody(int Position);

public sealed record LimitBody(int? Limit);

public sealed record ScheduleBody(string? At, int? Limit, bool Confirmed, string? Repeat = null, string? Until = null, bool? Unattended = null);

public sealed record DecisionBody(string? Decision);

public sealed record StepBody(int Step, ProjectDraft Draft);

public sealed record PreviewBody(ProjectDraft Draft, int? Skip);

public sealed record ImportBody(bool DryRun, int? Limit);

public sealed record FolderBody(string? Folder, string? BaseBranch);

public sealed record ContextFileBody(string? Path, string? Text);

/// <summary>
/// Every route of the UI. Each handler calls one service method and returns its result; the rules live in
/// the services and in the core library.
/// </summary>
internal static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/state", (WebUiOptions o) =>
        {
            var projects = new ProjectRegistry(o.Workspace).List();
            return new { version = WebUi.Version, workspace = o.Workspace.Root, azureDevOps = o.AzureDevOps is not null, projects = projects.Select(Summary) };
        });

        // ----- feedback: builds the prefilled GitHub new-issue address; nothing is sent from here -----
        api.MapPost("/feedback", (FeedbackBody b) => Feedback.Build(b, WebUi.Version, Feedback.OperatingSystem, Feedback.LocalNames()));

        // ----- projects and the wizard -----
        api.MapGet("/projects", (WebUiOptions o) => new ProjectRegistry(o.Workspace).List().Select(Summary));
        api.MapPost("/wizard/defaults", (FolderBody b, SetupService s, CancellationToken ct) => s.DefaultsAsync(b.Folder ?? string.Empty, b.BaseBranch, ct));
        api.MapPost("/wizard/analyze", (FolderBody b, SetupService s, CancellationToken ct) => s.AnalyzeAsync(b.Folder ?? string.Empty, ct));
        api.MapPost("/wizard/check", (StepBody b, SetupService s, CancellationToken ct) => s.CheckAsync(b.Step, b.Draft, ct));
        api.MapPost("/wizard/azure-devops/preview", (PreviewBody b, SetupService s, CancellationToken ct) => s.PreviewAsync(b.Draft, b.Skip ?? 0, ct));
        api.MapPost("/projects", async (ProjectDraft draft, SetupService s, CancellationToken ct) =>
            Results.Created($"/api/projects/{draft.Key}", await s.CreateAsync(draft, ct).ConfigureAwait(false)));

        var project = api.MapGroup("/projects/{key}");
        project.MapGet("/", (string key, ProjectScopes p) => Summary(p.Get(key).Project));
        project.MapGet("/setup", (string key, ProjectScopes p, SetupService s) => s.Load(p.Get(key)));
        project.MapPut("/setup", (string key, ProjectDraft draft, ProjectScopes p, SetupService s) => s.Save(p.Get(key), draft with { Key = key }));
        project.MapPost("/setup/check", (string key, StepBody b, ProjectScopes p, SetupService s, CancellationToken ct) =>
            s.CheckAsync(b.Step, b.Draft with { Key = key, RepositoryFolder = p.Get(key).Project.RepositoryFolder }, ct));

        project.MapPost("/azure-devops/import", (string key, ImportBody b, ProjectScopes p, SetupService s, CancellationToken ct) =>
            s.ImportAsync(p.Get(key), b.DryRun, b.Limit, ct));

        // ----- tasks -----
        var tasks = project.MapGroup("/tasks");
        tasks.MapGet("/", (string key, string? state, string? q, int? page, int? pageSize, ProjectScopes p, TaskOperations t, CancellationToken ct) =>
            t.ListAsync(p.Get(key), ct, new TaskQuery(state, q, page ?? 1, pageSize)));
        tasks.MapPost("/", (string key, TaskBody b, ProjectScopes p, TaskOperations t) =>
            t.Add(p.Get(key), b.Title ?? string.Empty, b.Body, b.Labels));
        tasks.MapGet("/tree", (string key, ProjectScopes p, TaskOperations t, CancellationToken ct) => t.TreeAsync(p.Get(key), ct));
        tasks.MapPost("/{number:int}/parent", (string key, int number, ParentBody b, ProjectScopes p, TaskOperations t) =>
            t.SetParent(p.Get(key), number, b.ParentId));
        tasks.MapGet("/{number:int}", (string key, int number, ProjectScopes p, TaskOperations t, CancellationToken ct) => t.DetailAsync(p.Get(key), number, ct));
        tasks.MapPut("/{number:int}", (string key, int number, TaskBody b, ProjectScopes p, TaskOperations t, CancellationToken ct) =>
        {
            t.Update(p.Get(key), number, b.Title, b.Body, b.Labels);
            return t.DetailAsync(p.Get(key), number, ct);
        });
        tasks.MapPost("/{number:int}/comments", (string key, int number, TextBody b, ProjectScopes p, TaskOperations t) =>
            t.Comment(p.Get(key), number, b.Text ?? string.Empty));
        tasks.MapPost("/{number:int}/close", (string key, int number, ProjectScopes p, TaskOperations t) => new { changed = t.SetClosed(p.Get(key), number, true) });
        tasks.MapPost("/{number:int}/reopen", (string key, int number, ProjectScopes p, TaskOperations t) => new { changed = t.SetClosed(p.Get(key), number, false) });
        tasks.MapPost("/{id}/start", (string key, string id, ProjectScopes p, TaskOperations t) => t.Start(p.Get(key), id));
        tasks.MapPost("/{id}/triage", (string key, string id, ProjectScopes p, TaskOperations t) =>
            Results.Accepted(null, t.StartTriage(p.Get(key), id)));
        tasks.MapPost("/{number:int}/answer", (string key, int number, TextBody b, ProjectScopes p, TaskOperations t, CancellationToken ct) =>
        {
            t.Answer(p.Get(key), number, b.Text ?? string.Empty);
            return t.DetailAsync(p.Get(key), number, ct);
        });
        tasks.MapPost("/{number:int}/answer-and-continue", (string key, int number, TextBody b, ProjectScopes p, TaskOperations t, CancellationToken ct) =>
            t.AnswerAndContinueAsync(p.Get(key), number, b.Text ?? string.Empty, ct));
        project.MapGet("/needs-human", (string key, ProjectScopes p, TaskOperations t, CancellationToken ct) => t.NeedsHumanAsync(p.Get(key), ct));

        project.MapGet("/inbox", (string key, ProjectScopes p, TaskOperations t, CancellationToken ct) => t.InboxAsync(p.Get(key), ct));

        // ----- pool and the night run -----
        var pool = project.MapGroup("/pool");
        pool.MapGet("/", (string key, bool? fresh, ProjectScopes p, PoolOperations o, CancellationToken ct) => o.ListAsync(p.Get(key), ct, fresh ?? false));
        pool.MapPost("/", (string key, IdBody b, ProjectScopes p, PoolOperations o) => new { added = o.Add(p.Get(key), b.Id ?? string.Empty) });
        pool.MapDelete("/{id}", (string key, string id, ProjectScopes p, PoolOperations o) => new { removed = o.Remove(p.Get(key), id) });
        pool.MapPost("/{id}/move", (string key, string id, MoveBody b, ProjectScopes p, PoolOperations o) => new { moved = o.Move(p.Get(key), id, b.Position) });
        pool.MapPost("/run", (string key, LimitBody b, ProjectScopes p, PoolOperations o) => new { pid = o.RunNow(p.Get(key), b.Limit) });
        pool.MapGet("/schedule", (string key, ProjectScopes p, PoolOperations o, CancellationToken ct) => o.ShowAsync(p.Get(key), ct));
        pool.MapPost("/schedule/plan", (string key, ScheduleBody b, ProjectScopes p, PoolOperations o, CancellationToken ct) =>
            o.PlanAsync(p.Get(key), b, ct));
        pool.MapPost("/schedule", async (string key, ScheduleBody b, ProjectScopes p, PoolOperations o, CancellationToken ct) =>
        {
            var note = await o.SetAsync(p.Get(key), b, ct).ConfigureAwait(false);
            return await o.ShowAsync(p.Get(key), ct, note).ConfigureAwait(false);
        });
        pool.MapGet("/status", (string key, ProjectScopes p, PoolOperations o, CancellationToken ct) => o.StatusAsync(p.Get(key), ct));
        pool.MapPut("/settings", (string key, PoolSettingsBody b, ProjectScopes p, PoolOperations o) => o.SaveSettings(p.Get(key), b));
        pool.MapPut("/settings/override", (string key, PoolOverrideBody b, ProjectScopes p, PoolOperations o) => o.SaveOverride(p.Get(key), b));
        pool.MapDelete("/settings/override", (string key, ProjectScopes p, PoolOperations o) => o.ClearOverride(p.Get(key)));
        pool.MapPost("/decisions/{id:long}/revert", (string key, long id, ProjectScopes p, PoolOperations o) => new { reverted = o.RevertDecision(p.Get(key), id) });
        pool.MapPost("/parked/{id}/unpark", (string key, string id, ProjectScopes p, PoolOperations o) => new { unparked = o.Unpark(p.Get(key), id) });
        pool.MapPost("/pause", (string key, ProjectScopes p, PoolOperations o, CancellationToken ct) => o.PauseAsync(p.Get(key), ct));
        pool.MapPost("/resume", (string key, ProjectScopes p, PoolOperations o) => new { resumed = o.Resume(p.Get(key)) });
        pool.MapPost("/integration/pr", async (string key, ProjectScopes p, PoolOperations o, CancellationToken ct) =>
            new { url = await o.OpenFinalPrAsync(p.Get(key), ct).ConfigureAwait(false) });
        pool.MapDelete("/schedule", async (string key, ProjectScopes p, PoolOperations o, CancellationToken ct) =>
            new { removed = await o.RemoveScheduleAsync(p.Get(key), ct).ConfigureAwait(false) });

        // ----- runs and jobs -----
        project.MapGet("/runs", (string key, ProjectScopes p, RunOperations r, CancellationToken ct) => r.OverviewAsync(p.Get(key), ct));
        project.MapGet("/runs/{id:long}", (string key, long id, ProjectScopes p, RunOperations r, CancellationToken ct) => r.DetailAsync(p.Get(key), id, ct));
        project.MapPost("/runs/{id:long}/decision", (string key, long id, DecisionBody b, ProjectScopes p, RunOperations r) =>
            new { recorded = r.Decide(p.Get(key), id, b.Decision ?? string.Empty) });
        project.MapGet("/jobs/{id}", (string key, string id, ProjectScopes p, RunOperations r, CancellationToken ct) => r.JobAsync(p.Get(key), id, ct));
        project.MapPost("/jobs/{id}/stop", (string key, string id, ProjectScopes p, RunOperations r) => new { stopped = r.Stop(p.Get(key), id) });

        // ----- settings: context documents -----
        project.MapGet("/context", (string key, ProjectScopes p) => p.Get(key).Settings.ListContextFiles());
        project.MapGet("/context/file", (string key, string path, ProjectScopes p) =>
            new { path, text = p.Get(key).Settings.ReadContext(path) ?? string.Empty, exists = p.Get(key).Settings.ReadContext(path) is not null });
        project.MapPut("/context/file", (string key, ContextFileBody b, ProjectScopes p) =>
        {
            p.Get(key).Settings.WriteContext(b.Path ?? string.Empty, b.Text ?? string.Empty);
            return new { saved = true };
        });
    }

    private static object Summary(ProjectRecord p) => new
    {
        p.Key,
        p.DisplayName,
        p.RepositoryFolder,
        p.BaseBranch,
        p.TaskSource,
        p.CreatedAt,
    };
}
