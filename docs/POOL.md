# The pool, unattended runs and the integration branch

The pool is the queue of tasks the agent works through. By default it works one task at a time,
asks a person when a task needs a decision, and sends every task to its own draft PR. This page
describes the settings that change that: running without a person watching, several agents at
once, a pool master that answers questions, a budget, a schedule, and an integration branch.

Nothing here lets the agent or the pool merge into your base branch. A person always merges.

## Settings

They live in the project's `autopilot.json` under `pool`, in the Pool page of the web UI, and in
`autopilot pool settings`. The pool level wins over the project's `trust`, which stays the default.

```json
"pool": {
  "unattended": false,
  "master": { "enabled": true, "model": null, "maxDecisionsPerRun": 10 },
  "onFailure": { "retries": 1, "then": "park", "pauseMinutes": 30 },
  "alwaysAsk": ["secrets", "stored-data", "contracts", "paid", "settings", "rules"],
  "budget": { "maxAgentRuns": null, "stopAtUsagePercent": 80 },
  "retry": { "continue": true },
  "concurrency": 1,
  "integration": { "enabled": true, "branch": null, "baseBranch": null, "finalPr": "draft" }
}
```

| Setting | Meaning |
| --- | --- |
| `unattended` | The pool never waits for a person. `pool run --unattended` turns it on for one run. |
| `master` | The pool master: a read-only `claude -p` run that answers needs-human questions and classifies failures. Only used when the pool is unattended. |
| `master.maxDecisionsPerRun` | After this many answers in one run the master parks the rest. |
| `onFailure` | A task that failed for its own reasons is tried again `retries` times with the master's notes, then parked. `pauseMinutes` is how long an infrastructure failure pauses the pool. |
| `retry.continue` | `true` (default): a retry starts from the previous attempt's kept branch (`<prefix><id>-attempt-N`, the latest), merged with the current base, and its prompt lists the earlier reviewer and gate findings. If the merge conflicts the retry starts clean and the log says so. `false`: every attempt starts clean. |
| `alwaysAsk` | Topics the master never decides. A question about one of them parks the task. |
| `budget` | `maxAgentRuns` is the most agent runs (implement attempts and master calls) in a rolling 24 hours. `stopAtUsagePercent` stops the pool when the subscription usage is known and at or above it. |
| `concurrency` | 1 to 4 agents at once. |
| `integration` | See below. |

## Pool-level override of the project defaults

Five settings can also be stored per project in the workspace database (table `pool_overrides`,
migration 013): `unattended`, `concurrency`, `master.enabled`, `budget.stopAtUsagePercent` and
`integration.enabled`. Each one is optional. A value that is set wins over `autopilot.json` for that
project's pool only; a value that is not set (or a project with no override at all) uses the
`autopilot.json` value. Fields resolve one by one, so an override of `concurrency` leaves the other
four on the project's values. `autopilot.json` itself is never changed by an override.

The web API shows the result. `GET /api/projects/<key>/pool/status` has, under `settings`, the effective
values (`concurrency`, `unattended`, ...), `effective` (for each of the five: `value` and `source`,
which is `pool` or `project`) and `override` (the stored values, `null` when there is no row).
`PUT /api/projects/<key>/pool/settings/override` stores the override (a field left out or `null` is
cleared) and `DELETE` on the same path removes it. Both return the settings view.

Limit of this version: the web API and the resolver (`PoolSettingsResolver`) exist; the pool runner still
reads the project's `autopilot.json` values.

## What the pool master decides

When a task comes back as needs-human and the pool is unattended:

1. Code checks the topic that triage stored for the task (a structured field, `topic`). When it is
   in `alwaysAsk` the task is parked without asking the model, and the reason says so ("Triage says
   the question is about paid, which is always left to a person"). A word such as "settings" or
   "rules" in the task text is not a topic.
   A narrow safety net runs too, for real signals only: a secret value in the text (a private key
   block, a GitHub or AWS token, `password = <value>`), or a task whose expected write paths include
   a sensitive file (`.env*`, key and certificate files, `secrets/`, `migrations/`, `.autopilot/`,
   `CLAUDE.md`, `AGENTS.md`). The park reason names the match and never repeats the secret.
2. Otherwise the master gets the task, the question with its options, and the project's context
   files, and returns an option or an answer, a topic, and whether the answer is easy to undo.
3. Code checks the answer. It parks the task when the master's topic is in `alwaysAsk`, when the
   answer is not reversible, when the master did not decide, or when the per-run limit is used up.
4. A usable answer is written into the task as `Answer: option N (pool master)` with a
   `Reason:` line, the task is triaged again and, if it is agent-ready now, implemented.

A failed run is first checked by code for infrastructure signs (signed out, usage limit, a program
that cannot start). The pool pauses with the reason and a retry time. Other failures go to the master:

- **infrastructure**: the pool pauses, same as above;
- **task**: one more attempt with the master's notes (the kept branch is renamed to
  `<branch>-attempt-N`), then the task is parked;
- **defect in Autopilot**: a `bug` task with a log excerpt is created, the task is parked, the pool goes on.

A pause blocks new runs until its retry time or until `autopilot pool resume`.

A person can pause the pool too: `autopilot pool pause` (or `POST /pool/pause`). The run finishes the agent
steps already started and starts no new task, also when the pause comes while the run is going. The status
shows Paused with the reason "Paused by a person" and the sub-status "finishing current tasks" while tasks
still run; once none runs, the sub-status goes away. This pause has no retry time and never expires: only
`autopilot pool resume` (or the web resume) lifts it. Pausing twice changes nothing, and a budget or
infrastructure pause keeps its retry time.

A used-up budget is not a failure. When `maxAgentRuns` or the usage limit is reached, the pool stops,
the task that was about to run stays queued (no failure decision, no retry used, nothing parked), and
the Pool page shows "Budget used up, resets at ..." (the time the oldest counted run leaves the 24-hour
window). That message is only informational: the next run checks the budget again, so a raised limit
applies at once.

## A run that died

A running pool records a heartbeat every 30 seconds. If the process is killed, crashes or the machine
restarts, its slots and locks would stay forever. Instead, a run with no heartbeat for 3 minutes (or
whose process is gone) is dead. The next pool run, `autopilot pool status` and the Pool page release
what it owned: its project lock and task locks (even when the process id was reused), and its
running slots, which show as "interrupted" ("The run was interrupted ... The task is still in the pool
and starts again with the next run"). Before a task starts, the pool also cleans up what the dead run left
for it: the worktree is removed, an empty task branch is deleted, and a branch with commits is renamed
to `<branch>-attempt-N` so nothing is lost and the name is free again. The pool report lists both.

Every decision is stored (task, question, answer, reason, topic, time). `autopilot pool decisions`
lists them; `pool decisions revert <id>` (or the button in the UI) takes the answer out of the task,
puts the task back in the pool and counts the undo. That count will feed the trust levels.
Parked tasks: `autopilot pool parked`, `pool unpark <id>`.

Questions are answered only for tasks kept in the workspace. For a project whose tasks live in a
tracker the master cannot write the answer, so those tasks are parked.

`POOL-REPORT.md` lists what was done, rejected, parked, decided by the master, found as bugs, not
reached, and how many agent runs were used.

## Several agents at once

`concurrency` is read again before each task starts, so a change on the Pool page or from
`autopilot pool settings --concurrency 3` applies to the next task of a running pool; agents that
are working finish first. Rules:

- each agent has its own worktree and branch, and each task its own lock (a row in the workspace
  database; a lock whose process is gone is taken over);
- two tasks that may write the same files do not run together. A task's write paths are the globs in
  a `Paths:` line of its text (`Paths: src/Billing/**, tests/Billing.Tests/**`), else the expected
  write paths triage stored for it (`writePaths`, a structured field), else the project's
  `writablePaths`, which overlaps almost everything and so serializes the task. A task that is not
  triaged yet falls back to the project's paths until its triage finishes, then the pool looks again.
  Two globs overlap when the literal folder prefix of one contains the other's (a pattern without a
  wildcard names one file and overlaps only a pattern that matches it). Triage can guess wrong: the
  implementer may still write inside the project's writable paths, and two such tasks then meet at the
  integration branch, where a merge conflict or a red build parks the later one;
- a label `depends-on:<id>` holds a task back until that task is done; if that task is rejected or
  parked the dependent task is parked too;
- all agents share one budget, and an infrastructure failure stops the whole pool;
- the Pool page shows who works on what, and why a task waits ("all 2 slots are busy",
  "shares files with task 4 (both change src/Pool/**)", "waits for task 3 (depends-on)").

## The integration branch (on by default)

With `integration.enabled` (the default) the pool does not send each task to the base branch:

1. The first pool run creates `integration/<date>` (or `integration.branch`; `{date}` becomes
   `yyyyMMdd`) from the base branch and remembers it.
2. Task branches start from the integration branch, so a task can build on the one before it.
3. After a task passes the gate and the review, the pool merges it into the integration branch with
   a merge commit in a temporary worktree, then runs the project's build and test commands. If the
   merge conflicts or build or tests fail, the merge is undone, the task is parked and the reason is
   shown.
4. When you are ready, `autopilot pool integration pr` (or the button on the Pool page) pushes the
   integration branch and opens one **draft** PR into the base branch with the `gh` program. You
   mark it ready and merge it.

The pool refuses to use the base branch, `main` or `master` as the integration branch, and it never
marks a PR ready or merges one. `autopilot pool integration new` starts a new branch at the next run.
To send each task PR straight to the base branch instead, set `pool.integration.enabled` to
`false` in the pool settings.

## Scheduled runs

```
autopilot schedule set --at "2026-11-28 02:00" [--repeat none|daily|weekdays|mon,wed,fri]
                       [--until 06:00] [--limit 8] [--unattended] --yes
```

Without `--yes` only the plan is printed. `--at HH:mm` alone still means every day. With a date and
no `--repeat` the run happens once. The job is installed in Windows Task Scheduler or the user's
crontab, as the current user, and starts `pool run` with `--limit`, `--unattended` (when asked, or
when the pool is unattended), `--until` and a hidden `--scheduled`. `--until` stops the pool from
starting new tasks after that time of day. Times are local; the UI shows the time zone.

A one-time job is installed as a daily one that the command's `--only-on <date>` guard lets run on
that date only, and it removes itself afterwards (the date format of `schtasks /SD` depends on the
computer's region). A run missed because the computer was off is started by a second job that runs at
sign-in (`autopilot schedule catch-up`), if the stop time of that run has not passed (without
`--until`, within 12 hours of its start). If the second job cannot be installed (Task Scheduler may
need more rights for sign-in triggers) you get a note, and you can run `schedule catch-up` by hand.

The Pool page has the same: date, time, repeat, stop time, limit, a confirmation that describes
what will happen, the next run, and a button to cancel.

## Command line

```
autopilot pool run [--limit N] [--unattended] [--until HH:mm]
autopilot pool settings [--unattended true|false] [--concurrency N] [--master true|false]
                        [--max-agent-runs N] [--stop-at-usage N] [--integration true|false]
autopilot pool status | pause | resume | decisions [revert <id>] | parked | unpark <id>
autopilot pool integration [pr | new]
autopilot schedule set | show | remove | catch-up
```

## Limits of this version

- The subscription usage percentage is not available from `claude -p`, so `stopAtUsagePercent`
  only acts where a usage reading exists (the runner takes a provider; the command line has none yet).
  `budget.maxAgentRuns` always applies.
- Standalone `autopilot implement` and detached jobs still use the project-wide run lock; the
  per-task lock is taken by the pool.
- The final PR is opened with `gh` (GitHub). For Azure DevOps, open it from the pushed integration
  branch yourself.
