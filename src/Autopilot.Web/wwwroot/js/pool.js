import { get, post, del } from './api.js';
import { h, clear, put, toast, emptyState, ask } from './dom.js';
import * as unattended from './poolunattended.js';
import {
  STEPS, stepper, statePill, subChips, progressSegments, statLine, rowPill, queueWindow, eventView, eventWindow,
  readinessCounts, estimateAgentRuns, startedAt, EVENT_CAP,
} from './poolstatus.js';

const base = key => `/projects/${encodeURIComponent(key)}`;
const POLL_MS = 5000;

export { STEPS };

const SVG = 'http://www.w3.org/2000/svg';
function svg(attrs, ...shapes) {
  const el = document.createElementNS(SVG, 'svg');
  for (const [k, v] of Object.entries({ viewBox: '0 0 16 16', width: 16, height: 16, 'aria-hidden': 'true', ...attrs })) el.setAttribute(k, v);
  for (const [tag, a] of shapes) {
    const shape = document.createElementNS(SVG, tag);
    for (const [k, v] of Object.entries(a)) shape.setAttribute(k, v);
    el.append(shape);
  }
  return el;
}
const dotsIcon = () => svg({ fill: 'currentColor' }, ['circle', { cx: 8, cy: 3, r: 1.4 }], ['circle', { cx: 8, cy: 8, r: 1.4 }], ['circle', { cx: 8, cy: 13, r: 1.4 }]);
const clockIcon = () => svg({ viewBox: '0 0 28 28', width: 28, height: 28, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6 }, ['circle', { cx: 14, cy: 14, r: 11 }], ['path', { d: 'M14 8v6l4 2.5' }]);

const dot = tone => h('span', { class: 'pool-dot ' + tone, 'aria-hidden': 'true' });
const pill = ({ text, tone }) => h('span', { class: 'pool-pill ' + tone }, dot(tone), text);
const clock = iso => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

// ---- the "..." menu: one open at a time, arrow keys move, Escape closes and gives the focus back ----
let menuOpen = false;
let afterMenu = null;

function kebab(label, entries, { upward = false, id = null } = {}) {
  const button = h('button', { type: 'button', class: 'pool-kebab', 'aria-label': label, 'aria-haspopup': 'menu', 'aria-expanded': 'false', 'data-id': id }, dotsIcon());
  const wrap = h('span', { class: 'pool-menu-wrap' }, button);
  let menu = null;
  const outside = ev => { if (!wrap.contains(ev.target)) close(false); };
  const close = refocus => {
    if (!menu) return;
    menu.remove();
    menu = null;
    button.setAttribute('aria-expanded', 'false');
    document.removeEventListener('pointerdown', outside, true);
    menuOpen = false;
    if (refocus) button.focus();
    const later = afterMenu;
    afterMenu = null;
    later?.();
  };
  const open = () => {
    menu = h('div', { class: 'pool-menu' + (upward ? ' up' : ''), role: 'menu', 'aria-label': label },
      entries.map(e => e === '-' ? h('hr', { role: 'separator' }) : h('button', {
        type: 'button', role: 'menuitem', tabindex: '-1', disabled: e.disabled, class: 'pool-menu-item' + (e.danger ? ' danger' : ''),
        onclick: () => { close(false); e.run(); },
      }, e.label)));
    const items = () => [...menu.querySelectorAll('[role="menuitem"]:not(:disabled)')];
    menu.addEventListener('keydown', ev => {
      const list = items();
      const at = list.indexOf(document.activeElement);
      const go = i => { ev.preventDefault(); list[(i + list.length) % list.length]?.focus(); };
      if (ev.key === 'ArrowDown') go(at + 1);
      else if (ev.key === 'ArrowUp') go(at - 1);
      else if (ev.key === 'Home') go(0);
      else if (ev.key === 'End') go(list.length - 1);
      else if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); close(true); }
      else if (ev.key === 'Tab') close(false);
    });
    wrap.append(menu);
    button.setAttribute('aria-expanded', 'true');
    menuOpen = true;
    document.addEventListener('pointerdown', outside, true);
    items()[0]?.focus();
  };
  button.addEventListener('click', () => { if (menu) close(true); else open(); });
  return wrap;
}

// ---- pieces of the page ----

function stepperView(status) {
  return h('ol', { class: 'pool-steps', 'aria-label': 'Pool status' }, stepper(status).map(step => h('li', {
    class: 'pool-step ' + step.state, 'aria-current': step.state === 'current' ? 'step' : null,
  }, h('span', { class: 'pool-node', 'aria-hidden': 'true' }),
  h('span', { class: 'pool-step-name' }, step.name, step.count ? h('i', {}, step.count) : null),
  step.state === 'done' ? h('span', { class: 'sr-only' }, ' (done)') : null)));
}

function stripView(status) {
  const seg = progressSegments(status);
  const line = statLine(status);
  const bar = (n, tone) => n > 0 ? h('span', { class: 'pool-seg-' + tone, style: `flex:${n}` }) : null;
  const empty = seg.done + seg.running + seg.waiting + seg.queued === 0;
  const text = parts => parts.map((p, i) => i === 0 && !p.startsWith('Estimate') ? h('b', {}, p) : p);
  return h('div', { class: 'pool-strip' },
    h('div', { class: 'pool-bar', role: 'img', 'aria-label': `${seg.done} done, ${seg.running} running, ${seg.waiting} waiting for you, ${seg.queued} queued` },
      empty ? h('span', { class: 'pool-seg-queued', style: 'flex:1' }) : [bar(seg.done, 'done'), bar(seg.running, 'running'), bar(seg.waiting, 'waiting'), bar(seg.queued, 'queued')]),
    h('div', { class: 'pool-stat' }, h('span', {}, text(line.tasks)), h('span', {}, text(line.right))));
}

export async function render({ view, key }) {
  const holder = h('div');
  const heroCard = h('section', { class: 'card pool-hero', 'data-part': 'pool-status' });
  const queue = h('section', { class: 'card pool-queue', 'data-part': 'pool-queue' });
  const events = h('section', { class: 'card pool-events', 'data-part': 'pool-events' });
  view.append(holder);
  let extra = null;
  let timer = null;
  let stopped = false;
  let items = [];
  let status = null;
  let showAllRows = false;
  let showAllEvents = false;
  let focusAfter = null;
  let drawn = null;

  const redraw = () => { heroView(); queueView(); eventsView(); };

  // ----- hero: title, state, one primary action, the five steps, chips and the progress strip -----
  function heroView() {
    clear(heroCard);
    const p = statePill(status, status?.busy);
    const started = startedAt(status);
    const i = status?.integration;
    const branch = i?.enabled ? (i.branch ?? `new branch from ${i.baseBranch}`) : `no integration branch, PRs go to ${i?.baseBranch ?? 'the base branch'}`;
    const { lead, chips } = subChips(status);
    const action = status?.busy
      ? h('a', { class: 'btn', href: `#/p/${encodeURIComponent(key)}/runs` }, 'Follow the run')
      : h('button', { type: 'button', class: 'primary', disabled: !items.length, title: items.length ? null : 'Add tasks to the pool first.', onclick: () => runPool() }, 'Run pool');
    put(heroCard,
      h('div', { class: 'pool-title' },
        h('div', { class: 'pool-title-text' }, h('h1', {}, 'Pool'),
          h('div', { class: 'pool-sub' }, pill(p), started ? h('span', {}, `Started ${clock(started)}`) : h('span', {}, `${(status?.queue ?? items).length} ${(status?.queue ?? items).length === 1 ? 'task' : 'tasks'} in the pool`), h('code', { class: 'pool-branch' }, branch))),
        h('div', { class: 'pool-actions' }, action,
          kebab('More pool actions', [
            { label: 'Refresh', run: () => refresh(true) },
            { label: 'Open runs', run: () => { location.hash = `#/p/${encodeURIComponent(key)}/runs`; } },
            { label: 'Add tasks', run: () => { location.hash = `#/p/${encodeURIComponent(key)}/tasks`; } },
          ]))),
      status?.pause ? h('div', { class: 'pool-pause', role: 'status' },
        h('p', {}, h('b', {}, 'Paused. '), status.pause.reason, status.pause.retryAt ? ` It can start again after ${new Date(status.pause.retryAt).toLocaleString()}.` : ''),
        h('button', { type: 'button', class: 'sm', onclick: async () => { try { await post(`${base(key)}/pool/resume`); await refresh(true); } catch (e) { toast(e.message, true); } } }, 'Resume now')) : null,
      stepperView(status),
      h('div', { class: 'pool-chips' }, h('span', { class: 'pool-lead' }, lead), chips.map(c => h('span', { class: 'pool-chip ' + c.tone }, c.tone === 'run' ? dot('run') : null, c.text))),
      stripView(status));
  }

  // ----- queue: 44px rows, five at most until "Show more" -----
  const slotOf = id => (status?.slots ?? []).find(s => s.id === id);
  const position = id => items.findIndex(x => x.id === id);

  async function move(id, to) {
    focusAfter = id;
    try { await post(`${base(key)}/pool/${encodeURIComponent(id)}/move`, { position: to }); await refresh(true); }
    catch (e) { toast(e.message, true); }
  }

  function rowMenu(row, rank, total) {
    const at = position(row.id);
    const inPool = at >= 0;
    return kebab(`Actions for #${row.id}`, [
      { label: 'Move to top', disabled: !inPool || at === 0, run: () => move(row.id, items[0].position) },
      { label: 'Move up', disabled: !inPool || at === 0, run: () => move(row.id, items[at - 1].position) },
      { label: 'Move down', disabled: !inPool || at === items.length - 1, run: () => move(row.id, items[at + 1].position) },
      '-',
      { label: 'Open task', run: () => { location.hash = `#/p/${encodeURIComponent(key)}/tasks/${encodeURIComponent(row.id)}`; } },
      { label: 'Remove from pool', danger: true, disabled: !inPool, run: async () => {
        focusAfter = '';
        try { await del(`${base(key)}/pool/${encodeURIComponent(row.id)}`); await refresh(true); } catch (e) { toast(e.message, true); }
      } },
    ], { upward: total > 3 && rank >= total - 2, id: row.id });
  }

  function queueView() {
    clear(queue);
    const rows = status?.queue ?? [];
    const counts = readinessCounts(rows);
    const parts = [counts.ready && `${counts.ready} ready`, counts.waits && `${counts.waits} wait for another`, counts.needsAnswer && `${counts.needsAnswer} need an answer`, counts.notTriaged && `${counts.notTriaged} not triaged`, counts.parked && `${counts.parked} waiting for you`].filter(Boolean);
    put(queue, h('div', { class: 'pool-head' },
      h('div', {}, h('h2', {}, 'Queue ', h('span', { class: 'muted' }, `${rows.length} ${rows.length === 1 ? 'task' : 'tasks'}`)),
        h('p', { class: 'hint' }, parts.length ? `Worked in this order. ${parts.join(', ')}.` : 'Worked in this order. Every result is a draft that a person merges.')),
      h('a', { class: 'btn sm', href: `#/p/${encodeURIComponent(key)}/tasks` }, 'Add tasks')));
    if (!rows.length) {
      put(queue, emptyState('The pool is the queue the agent works through. Add triaged tasks to it from the Tasks page.', { label: 'Pick tasks', href: `#/p/${encodeURIComponent(key)}/tasks` }, 'The pool is empty'));
      return;
    }
    const win = queueWindow(rows, showAllRows);
    put(queue, h('ul', { class: 'pool-q-list' }, win.visible.map((row, rank) => {
      const slot = slotOf(row.id);
      return h('li', { class: 'pool-q', 'data-id': row.id },
        h('span', { class: 'pool-q-id' }, '#' + row.id),
        h('span', { class: 'pool-q-title', title: slot?.detail ? `${row.title}\n${slot.detail}` : row.title }, row.title),
        pill(rowPill(row, slot)),
        rowMenu(row, rank, win.visible.length));
    })));
    if (win.hidden) put(queue, h('div', { class: 'pool-more' }, h('button', { type: 'button', class: 'ghost sm', onclick: () => { showAllRows = true; queueView(); } }, `Show ${win.hidden} more`)));
    else if (win.canCollapse) put(queue, h('div', { class: 'pool-more' }, h('button', { type: 'button', class: 'ghost sm', onclick: () => { showAllRows = false; queueView(); } }, 'Show fewer')));
    if (focusAfter) { queue.querySelector(`.pool-kebab[data-id="${CSS.escape(focusAfter)}"]`)?.focus(); }
    focusAfter = null;
  }

  // ----- events: time, dot, text, newest first -----
  function eventsView() {
    clear(events);
    const win = eventWindow(status?.timeline, showAllEvents);
    const hasAny = win.rows.length > 0;
    put(events, h('div', { class: 'pool-head' }, h('h2', {}, 'Events'),
      (status?.timeline?.length ?? 0) > EVENT_CAP
        ? h('button', { type: 'button', class: 'ghost sm', onclick: () => { showAllEvents = !showAllEvents; eventsView(); } }, showAllEvents ? 'Fewer events' : `All events (${status.timeline.length})`) : null));
    if (!hasAny) {
      put(events, h('div', { class: 'pool-empty' }, clockIcon(), h('b', {}, 'Nothing has run yet'),
        h('span', {}, 'Starts, merges and questions for you will show up here as the pool works.')));
      return;
    }
    put(events, h('ol', { class: 'pool-ev-list' }, win.rows.map(e => {
      const v = eventView(e);
      return h('li', { class: 'pool-ev' }, h('time', { datetime: e.time, class: 'mono' }, clock(e.time)), dot(v.tone), h('span', {}, v.text));
    })));
  }

  const readiness = () => readinessCounts(status?.queue);

  /** Reads the real queue and the live parts of the page again. The settings fields are left alone. */
  async function refresh(loud = false) {
    try {
      const [next, nextStatus] = await Promise.all([get(`${base(key)}/pool${loud ? '?fresh=true' : ''}`), get(`${base(key)}/pool/status`), extra?.refreshLive()]);
      // Draw again only when something changed: a redraw drops the focus from the button a person tabbed to.
      const signature = JSON.stringify([next, nextStatus]);
      if (signature === drawn) return;
      drawn = signature;
      items = next;
      status = nextStatus;
      if (menuOpen) afterMenu = redraw; // never pull an open menu away from under the pointer
      else redraw();
    } catch (e) { if (loud) toast(e.message, true); }
  }

  // ----- the run confirmation -----
  async function runPool() {
    await refresh(true);
    if (!items.length) { toast('The pool is empty. Add tasks to it first.', true); return; }
    const s = status.settings;
    const c = readiness();
    const total = status.queue.length;
    const estimate = estimateAgentRuns(total);
    const used = status.agentRunsToday ?? 0;
    const i = status.integration;
    const limit = h('input', { type: 'number', min: 1, step: 1, placeholder: 'all', class: 'pool-num', 'aria-label': 'Most tasks to try, empty for all' });
    const words = [c.ready && `${c.ready} ready`, c.waits && `${c.waits} wait${c.waits === 1 ? 's' : ''} for another`, c.notTriaged && `${c.notTriaged} not triaged`, c.needsAnswer && `${c.needsAnswer} ${c.needsAnswer === 1 ? 'needs' : 'need'} an answer`, c.parked && `${c.parked} waiting for you`].filter(Boolean);
    const row = (label, ...content) => [h('dt', {}, label), h('dd', {}, ...content)];
    const chip = text => h('span', { class: 'pool-chip' }, text);
    const ok = await ask({
      title: 'Run this pool?', confirm: 'Confirm and run',
      body: [
        h('p', { class: 'muted' }, 'Nothing starts until you confirm.'),
        h('dl', { class: 'pool-sum' },
          row('Tasks', h('b', {}, `${total} in order.`), ` ${words.join(', ')}.`, c.needsAnswer || c.parked ? ' Those that need an answer will wait for you.' : null),
          row('Agent runs', `About `, h('b', {}, String(estimate)), s.maxAgentRuns ? ` of the ${s.maxAgentRuns} allowed per day` : '. No daily limit is set',
            used ? `; ${used} already used in the last 24 hours.` : '.', s.maxAgentRuns && estimate + used > s.maxAgentRuns ? ' The pool stops starting tasks at the limit.' : null),
          row('Integration branch', i.enabled
            ? [h('code', {}, i.branch ?? `new branch from ${i.baseBranch}`), h('br'), h('span', { class: 'muted' }, `One final draft PR into ${i.baseBranch}. A person merges it.`)]
            : [`None. Each task PR goes straight to ${i.baseBranch}.`, h('br'), h('span', { class: 'muted' }, 'A person merges every one.')]),
          row('Settings used', h('span', { class: 'pool-chips-plain' },
            chip(`${s.concurrency} ${s.concurrency === 1 ? 'agent' : 'agents'} at once`), chip(`Without a person: ${s.unattended ? 'on' : 'off'}`),
            chip(`Stop at ${s.stopAtUsagePercent}% of the limit`), chip(s.maxAgentRuns ? `Up to ${s.maxAgentRuns} agent runs per day` : 'No daily limit'))),
          row('Most tasks', limit, h('span', { class: 'hint' }, ' Leave empty to try all.')),
          row('Starts', 'Now.')),
      ],
    });
    if (!ok) return;
    try {
      await post(`${base(key)}/pool/run`, { limit: limit.value ? Number(limit.value) : null });
      toast('Pool run started. Follow it on the Runs page.');
      await load();
    } catch (e) { toast(e.message, true); }
  }

  async function load() {
    [items, status] = await Promise.all([get(`${base(key)}/pool`), get(`${base(key)}/pool/status`)]);
    drawn = JSON.stringify([items, status]);
    extra = await unattended.render({ key, reload: load });
    clear(holder);
    redraw();
    const details = extra.element.querySelector('details');
    // On a phone the settings start closed; from 900px up the panel is always open and its header does not toggle.
    const phone = window.matchMedia('(max-width: 900px)');
    if (phone.matches && details) details.open = false;
    details?.querySelector('summary')?.addEventListener('click', ev => { if (!phone.matches) ev.preventDefault(); });
    holder.append(
      h('div', { class: 'app-layout pool-layout' },
        h('div', { class: 'app-left pool-settings stack' }, extra.element),
        h('div', { class: 'app-right open stack pool-right' }, heroCard, h('div', { class: 'pool-main' }, queue, events), extra.right)));
  }

  await load();

  // The queue and the status follow what happens elsewhere (a run, the command line) while the page is open.
  const tick = () => {
    if (stopped) return;
    if (!document.hidden) refresh().finally(() => { if (!stopped) timer = setTimeout(tick, POLL_MS); });
    else timer = setTimeout(tick, POLL_MS);
  };
  timer = setTimeout(tick, POLL_MS);
  return () => { stopped = true; clearTimeout(timer); menuOpen = false; afterMenu = null; };
}
