import { get, post, put, del } from './api.js';
import { h, clear, put as place, toast, badge, field, ask, ago } from './dom.js';
import { overrides } from './poolstatus.js';
import { mergesList } from './poolintegration.js';

const base = key => `/projects/${encodeURIComponent(key)}/pool`;

const DAYS = [['mon', 'Mon'], ['tue', 'Tue'], ['wed', 'Wed'], ['thu', 'Thu'], ['fri', 'Fri'], ['sat', 'Sat'], ['sun', 'Sun']];

/**
 * The part of the Pool page for unattended runs: the settings panel (agents at once, the unattended switch, the
 * budget, the integration branch, the scheduled run) and, on the right, the final PR, parked tasks and the pool
 * master's decisions. `reload` redraws the whole page after a change.
 */
export async function render({ key, reload }) {
  const holder = h('div', { class: 'stack' });
  const live = h('div', { class: 'stack pool-live' });
  const [status, schedule] = await Promise.all([get(`${base(key)}/status`), get(`${base(key)}/schedule`)]);
  paint(holder, live, key, status, schedule, reload);
  // refreshLive redraws only the parts that change while a run is active; the settings the person may be typing in stay.
  const refreshLive = async () => {
    const next = await get(`${base(key)}/status`);
    paintLive(live, key, next, reload);
    return next;
  };
  // element: the left column (the settings panel). right: the wide right area (final PR, parked, decisions).
  return { element: holder, right: live, refreshLive };
}

function paintLive(live, key, status, reload) {
  clear(live);
  place(live, finalPrCard(key, status, reload), parkedCard(key, status, reload), decisionsCard(key, status, reload));
}

function paint(holder, live, key, status, schedule, reload) {
  clear(holder);
  const s = status.settings;
  const save = async change => {
    try { await put(`${base(key)}/settings`, change); await reload(); }
    catch (e) { toast(e.message, true); await reload(); }
  };

  paintLive(live, key, status, reload);
  place(holder, settingsPanel(key, s, status, schedule, save, reload));
}

/** One settings row: the label (and a short hint) on the left, the control on the right. */
function setting(id, label, hint, control, overridden = false) {
  return h('div', { class: 'pool-set' },
    h('div', { class: 'what' },
      h('label', { for: id, id: id + '-label' },
        overridden ? h('span', { class: 'pool-ov', role: 'img', 'aria-label': 'Changed from the project default' }) : null, label),
      hint ? h('p', { class: 'hint', id: id + '-hint' }, hint) : null),
    h('div', { class: 'control' }, control));
}

/** The settings panel: single-line rows. On a phone it is a closed details element under the queue. */
function settingsPanel(key, s, status, schedule, save, reload) {
  // A value counts as changed only where the API sends the project default next to it.
  const changed = overrides(s);
  const defaults = s.defaults ?? {};
  const isChanged = name => Object.hasOwn(changed, name);
  const defaultHint = (name, unit = '') => (isChanged(name) ? `Project default: ${defaults[name]}${unit}` : null);
  const changedCount = Object.keys(changed).length;

  const seg = h('div', { class: 'pool-seg', role: 'radiogroup', id: 'set-concurrency', 'aria-labelledby': 'set-concurrency-label', 'aria-describedby': 'set-concurrency-hint' },
    Array.from({ length: s.maxConcurrency }, (_, i) => i + 1).map(n => h('label', { class: 'pool-seg-item' },
      h('input', { type: 'radio', name: 'set-concurrency', value: n, checked: n === s.concurrency, 'aria-label': `${n} at once`, onchange: () => save({ concurrency: n }) }),
      h('span', { 'aria-hidden': 'true' }, String(n)))));

  const toggle = (id, on, change) => {
    const box = h('input', { id, type: 'checkbox', checked: on, role: 'switch', class: 'pool-switch', 'aria-describedby': id + '-hint', onchange: () => save(change(box.checked)) });
    return h('span', { class: 'pool-switch-hit' }, box);
  };

  // A number field saves when it loses focus or Enter is pressed, and says so if the value is not allowed.
  const number = (id, current, min, max, placeholder, allowEmpty, apply, unit) => {
    const input = h('input', { id, type: 'number', inputmode: 'numeric', min, max, step: 1, placeholder, class: 'pool-num', 'aria-describedby': id + '-hint' });
    input.value = current ?? '';
    const commit = () => {
      const text = input.value.trim();
      if (String(current ?? '') === text) return;
      const n = text === '' ? 0 : Number(text);
      const allowed = text === '' ? allowEmpty : Number.isInteger(n) && n >= min && n <= max;
      if (!allowed) { toast(`Enter a whole number from ${min} to ${max}.`, true); input.value = current ?? ''; return; }
      save(apply(n));
    };
    input.addEventListener('change', commit);
    input.addEventListener('keydown', ev => { if (ev.key === 'Enter') commit(); });
    // Every number field has the same box; a unit is a suffix inside it, right aligned.
    return h('span', { class: 'pool-numbox' }, input, unit ? h('span', { class: 'pool-unit', 'aria-hidden': 'true' }, unit) : null);
  };
  const maxRuns = number('set-max-runs', s.maxAgentRuns, 1, 100000, 'none', true, n => ({ maxAgentRuns: n }));
  const stopAt = number('set-stop-at', s.stopAtUsagePercent, 1, 100, '80', false, n => ({ stopAtUsagePercent: n }), '%');

  const i = status.integration;
  const integrationHint = !i.enabled
    ? `Off: each task PR goes straight to ${i.baseBranch}.`
    : i.branch ? [h('code', {}, i.branch), ` collects the tasks. One final draft PR into ${i.baseBranch}.`]
      : `Task PRs go to a new branch from ${i.baseBranch}. One final draft PR, merged by a person.`;

  const sched = scheduleBlock(key, s, schedule, reload);
  return h('details', { class: 'card pool-panel', open: true, 'data-part': 'pool-settings' },
    h('summary', { class: 'pool-panel-head' },
      h('span', { class: 'pool-panel-title' },
        h('h2', {}, 'Pool settings'),
        h('span', { class: 'hint' }, changedCount ? `${changedCount} changed from the project defaults` : 'A change applies to the next task. Working agents finish first.')),
      changedCount ? h('span', { class: 'pool-pill' }, h('span', { class: 'pool-ov' }), `${changedCount} changed`) : null,
      h('span', { class: 'pool-chev', 'aria-hidden': 'true' })),
    h('div', { class: 'pool-sets' },
      setting('set-concurrency', 'Agents at once', defaultHint('concurrency') ?? `Up to ${s.maxConcurrency}`, seg, isChanged('concurrency')),
      setting('set-unattended', 'Run without a person', defaultHint('unattended') ?? `Questions about ${s.alwaysAsk.join(', ')} always wait for you.`, toggle('set-unattended', s.unattended, v => ({ unattended: v })), isChanged('unattended')),
      setting('set-master', 'Pool master', 'Answers questions when running without a person.', toggle('set-master', s.masterEnabled, v => ({ masterEnabled: v }))),
      setting('set-max-runs', 'Most agent runs per day', defaultHint('maxAgentRuns') ?? 'Empty means no limit.', maxRuns, isChanged('maxAgentRuns')),
      setting('set-stop-at', 'Stop at usage limit', defaultHint('stopAtUsagePercent', '%') ?? 'Pauses at this share of the limit.',
        stopAt, isChanged('stopAtUsagePercent')),
      setting('set-integration', 'Integration branch', integrationHint, toggle('set-integration', s.integrationEnabled, v => ({ integrationEnabled: v }))),
      sched.row),
    sched.form);
}

function decisionsCard(key, status, reload) {
  const list = status.decisions;
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Decisions of the pool master'),
    h('p', { class: 'muted' }, `${status.revertedDecisions} undone by a person so far. Undo takes the answer out of the task and puts the task back in the pool.`),
    !list.length ? h('p', { class: 'muted' }, 'The pool master has not decided anything yet.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Task', 'Kind', 'Answer', 'Why', ''].map(t => h('th', {}, t)))),
      h('tbody', {}, list.map(d => h('tr', {},
        h('td', { 'data-label': 'Task' }, `#${d.workItemId}`),
        h('td', { 'data-label': 'Kind' }, d.kind),
        h('td', { 'data-label': 'Answer' }, d.answer, d.revertedAt ? [' ', badge('undone', 'warn')] : null),
        h('td', { 'data-label': 'Why' }, d.reason),
        h('td', { class: 'actions' }, d.revertedAt ? null : h('button', { type: 'button', onclick: async () => {
          const ok = await ask({ title: 'Undo this decision?', confirm: 'Undo and rebuild', body: h('p', {}, `The answer is removed from task #${d.workItemId}, which goes back in the pool. Triage it again before the next run.`) });
          if (!ok) return;
          try { await post(`${base(key)}/decisions/${d.id}/revert`); toast('Undone.'); await reload(); } catch (e) { toast(e.message, true); }
        } }, 'Undo and rebuild'))))))));
}

function parkedCard(key, status, reload) {
  const list = status.parked;
  return h('section', { class: 'card stack' },
    h('h2', {}, 'Parked for you'),
    !list.length ? h('p', { class: 'muted' }, 'Nothing is parked. Tasks end up here when the pool must not decide for you, or gave up.') : h('div', { class: 'table-wrap' }, h('table', { class: 'stack-sm' },
      h('thead', {}, h('tr', {}, ['Task', 'Topic', 'Why', 'Since', ''].map(t => h('th', {}, t)))),
      h('tbody', {}, list.map(p => h('tr', {},
        h('td', { 'data-label': 'Task' }, `#${p.workItemId}`),
        h('td', { 'data-label': 'Topic' }, p.category ? badge(p.category, 'warn') : ''),
        h('td', { 'data-label': 'Why' }, p.reason),
        h('td', { 'data-label': 'Since' }, ago(p.parkedAt)),
        h('td', { class: 'actions' }, h('button', { type: 'button', onclick: async () => {
          try { await post(`${base(key)}/parked/${encodeURIComponent(p.workItemId)}/unpark`); await reload(); } catch (e) { toast(e.message, true); }
        } }, 'Back to the pool'))))))));
}

/** Shown once the integration branch has merged tasks or a final PR: what it holds and the button to open the final PR. */
function finalPrCard(key, status, reload) {
  const i = status.integration;
  if (!i.enabled || !i.branch || (!i.merges.length && !i.finalPrUrl)) return null;
  return h('section', { class: 'card stack', 'data-part': 'final-pr' },
    h('h2', {}, 'Integration branch'),
    h('p', {}, h('code', {}, i.branch), ` collects the finished tasks, each merged after build and tests passed. You merge one final PR into ${i.baseBranch}.`),
    mergesList(i.merges),
    i.finalPrUrl ? h('p', {}, 'Final PR (draft): ', h('a', { href: i.finalPrUrl, target: '_blank', rel: 'noopener noreferrer' }, i.finalPrUrl)) : null,
    !i.finalPrUrl ? h('div', { class: 'row' }, h('button', { type: 'button', onclick: async () => {
      const ok = await ask({ title: 'Open the final PR?', confirm: 'Open as draft', body: [
        h('p', {}, `Pushes ${i.branch} and opens a draft PR into ${i.baseBranch} with the gh program.`),
        h('p', { class: 'muted small' }, 'It stays a draft. You mark it ready and merge it yourself.')] });
      if (!ok) return;
      try { const r = await post(`${base(key)}/integration/pr`); toast('Draft PR opened: ' + r.url); await reload(); } catch (e) { toast(e.message, true); }
    } }, 'Open the final PR (draft)…')) : null);
}

/** The schedule row (state and a button) and the form it opens below the rows. */
function scheduleBlock(key, s, schedule, reload) {
  const now = new Date();
  const pad = n => String(n).padStart(2, '0');
  const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  const date = h('input', { type: 'date', value: today, required: true });
  const time = h('input', { type: 'time', value: '02:00', required: true });
  const repeat = h('select', {}, [['none', 'Once'], ['daily', 'Every day'], ['weekdays', 'Weekdays'], ['days', 'On chosen days']].map(([v, t]) => h('option', { value: v }, t)));
  const days = DAYS.map(([v, t]) => ({ v, box: h('input', { type: 'checkbox', value: v }), t }));
  const dayRow = h('div', { class: 'pool-sched-days', hidden: true }, days.map(d => h('label', { class: 'row' }, d.box, d.t)));
  repeat.addEventListener('change', () => { dayRow.hidden = repeat.value !== 'days'; });
  const until = h('input', { type: 'time' });
  const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all' });
  const unattended = h('input', { type: 'checkbox', checked: s.unattended });
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;

  const body = () => {
    const chosen = days.filter(d => d.box.checked).map(d => d.v);
    const rep = repeat.value === 'days' ? chosen.join(',') : repeat.value;
    return {
      at: `${date.value} ${time.value}`,
      repeat: rep || null,
      until: until.value || null,
      limit: limit.value ? Number(limit.value) : null,
      unattended: unattended.checked,
    };
  };

  const cancelRun = () => h('button', { type: 'button', class: 'danger sm', onclick: async () => {
    const ok = await ask({ title: 'Cancel the scheduled run?', confirm: 'Cancel it', danger: true, body: h('p', {}, 'The job is removed from the system scheduler.') });
    if (!ok) return;
    try { await del(`${base(key)}/schedule`); toast('Cancelled.'); await reload(); } catch (e) { toast(e.message, true); }
  } }, 'Cancel the run');
  const form = h('div', { class: 'pool-sched', id: 'pool-schedule-form', hidden: true },
    schedule.set ? scheduleDetails(schedule, cancelRun) : null,
    h('div', { class: 'pool-sched-grid' },
      field('Date', date), field(`Time (${zone})`, time), field('Repeat', repeat), field('Stop starting tasks at (optional)', until),
      field('Most tasks (optional)', limit),
      h('label', { class: 'field' }, h('span', { class: 'lbl' }, 'Questions'), h('span', { class: 'pool-sched-check' }, unattended, h('span', {}, 'Run without a person')))),
    dayRow,
    h('p', { class: 'hint' }, 'It runs on this computer, as your user. Results are drafts; nothing is merged.'),
    h('div', { class: 'pool-sched-actions' }, h('button', { type: 'button', class: 'primary', onclick: async () => {
      try {
        const plan = await post(`${base(key)}/schedule/plan`, body());
        const ok = await ask({
          title: 'Install this scheduled run?', confirm: 'Install',
          body: [
            h('p', { class: 'prewrap small' }, plan.summary),
            h('p', { class: 'muted small' }, `Times are in ${plan.timeZone}. It runs on this computer, as your user. A run missed because the computer was off starts at the next sign-in while its stop time has not passed. Results are drafts; nothing is merged into the base branch.`),
          ],
        });
        if (!ok) return;
        const result = await post(`${base(key)}/schedule`, { ...body(), confirmed: true });
        toast('Scheduled.'); if (result.note) toast(result.note, true);
        await reload();
      } catch (e) { toast(e.message, true); }
    } }, 'Review and schedule'),
      h('button', { type: 'button', class: 'secondary', onclick: () => closeForm() }, 'Cancel')));

  const closeForm = () => { form.hidden = true; open.setAttribute('aria-expanded', 'false'); open.focus(); };
  const open = h('button', { type: 'button', class: 'sm', 'aria-expanded': 'false', 'aria-controls': 'pool-schedule-form',
    onclick: () => { form.hidden = !form.hidden; open.setAttribute('aria-expanded', String(!form.hidden)); } }, schedule.set ? 'Change…' : 'Set…');
  const row = h('div', { class: 'pool-set' },
    h('div', { class: 'what' }, h('label', { id: 'set-schedule-label' }, 'Schedule'), h('p', { class: 'hint', id: 'set-schedule-hint' }, scheduleSummaryText(schedule))),
    h('div', { class: 'control' }, open));
  return { row, form };
}

/** The one-line summary in the Schedule row: 'Not scheduled' or e.g. 'Every day 02:00, stops starting at 06:00'. */
export function scheduleSummaryText(schedule) {
  if (!schedule.set) return 'Not scheduled';
  const p = schedule.plan;
  const when = String(p.when ?? '');
  return `${when.charAt(0).toUpperCase()}${when.slice(1)}${p.until ? `, stops starting at ${p.until}` : ''}`;
}

/** Inside the open form, when a run is set: what is installed and the button that removes it. */
function scheduleDetails(s, cancelRun) {
  const p = s.plan;
  return h('div', { class: 'pool-sched-now' },
    h('p', { class: 'hint' }, `${p.limit ? `At most ${p.limit} tasks. ` : ''}${p.unattended ? 'Without a person. ' : ''}`,
      s.next ? `Next: ${new Date(s.next).toLocaleString()}. ` : 'No later run. ',
      s.installedInOs ? badge('installed', 'ok') : badge('missing in the system scheduler: schedule it again', 'warn')),
    cancelRun());
}
