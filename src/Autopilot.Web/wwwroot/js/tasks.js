import { get, post, put } from './api.js';
import { h, clear, toast, badge, ago, field, emptyState, busy, ask } from './dom.js';

const base = key => `/projects/${encodeURIComponent(key)}`;

/**
 * Master-detail: the task list is always in the left column, the selected task is on the right.
 * #/p/key/tasks/ID selects that task; otherwise the first task of the list is shown.
 */
export async function render(ctx) {
  const right = h('div', { class: 'app-right open', 'data-part': 'task-detail' });
  const left = h('div', { class: 'app-left stack', 'data-part': 'task-list' });
  const sel = { id: ctx.rest[0] ? String(ctx.rest[0]) : null, auto: !ctx.rest[0], paintList: () => {}, loadDetail: async () => {} };
  ctx.view.append(h('div', { class: 'app-layout tasks-layout', 'data-page': 'tasks' }, left, right));

  const select = async id => {
    sel.id = String(id);
    sel.auto = false;
    sel.paintList();
    await sel.loadDetail();
  };
  sel.loadDetail = async () => {
    clear(right);
    if (!sel.id) { right.append(h('div', { class: 'card' }, emptyState('Pick a task in the list to see its text, triage and comments here.', null, 'Select a task'))); return; }
    await renderDetail({ ...ctx, view: right }, sel.id, () => sel.reloadList?.());
  };
  const stop = await renderList({ ...ctx, view: left }, sel, select);
  await sel.loadDetail();
  return stop;
}

function triageBadges(item) {
  if (!item.triage) return [badge('not triaged')];
  const t = item.triage;
  return [
    badge(t.label, t.label === 'agent-ready' ? 'ok' : 'warn'),
    badge(`${t.risk} risk`, t.risk === 'high' ? 'bad' : ''),
    t.stale ? badge('stale: text changed since triage', 'warn') : null,
  ];
}

function lastRun(item) {
  const r = item.lastRun;
  if (!r) return h('span', { class: 'muted' }, 'never');
  return h('a', { href: `#/p/${encodeURIComponent(r.projectKey)}/runs/${r.id}` }, `${r.outcome}${r.gateVerdict ? ', gate ' + r.gateVerdict : ''}`);
}

const STATES = [['', 'All', 'all'], ['needs-human', 'Needs a person', 'needsHuman'], ['agent-ready', 'Agent-ready', 'agentReady'], ['not-triaged', 'Not triaged', 'notTriaged'], ['stale', 'Stale triage', 'stale']];
const PAGE_SIZE = 25;
const TREE_PAGE_SIZE = 200;

/** One tree node and, when it is an expanded group, its children. A group shows chevron, kind chip, progress bar and "N of M". */
function treeRows(node, depth, byId, data, expanded, select, repaint, bulk) {
  const frag = document.createDocumentFragment();
  const indent = `padding-left:calc(var(--sp-8) + ${depth} * var(--sp-16))`;
  if (node.counts) {
    const c = node.counts;
    const total = c.done + c.inProgress + c.needsYou + c.notStarted;
    const open = expanded.get(node.id) !== false;
    const chevron = h('button', {
      type: 'button', class: 'tree-chevron', 'aria-expanded': String(open), 'aria-label': `${open ? 'Collapse' : 'Expand'} #${node.id}`,
      onclick: ev => { ev.stopPropagation(); expanded.set(node.id, !open); repaint(); },
    }, open ? '▾' : '▸');
    frag.append(h('div', { class: 'tree-row tree-group', role: 'treeitem', 'data-id': String(node.id), 'aria-expanded': String(open), style: indent, onclick: () => select(node.id),
      ondragover: ev => { ev.preventDefault(); ev.currentTarget.classList.add('drop-target'); },
      ondragleave: ev => ev.currentTarget.classList.remove('drop-target'),
      ondrop: ev => { ev.preventDefault(); ev.currentTarget.classList.remove('drop-target'); bulk.drop(ev.dataTransfer.getData('text/plain'), node.id); } },
      chevron,
      h('span', { class: 'chip tree-kind' }, node.kind || 'group'),
      h('span', { class: 'tree-title' }, `#${node.id} ${node.title}`),
      h('span', { class: 'tree-progress', role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': String(total), 'aria-valuenow': String(c.done) },
        h('span', { class: 'tree-progress-fill', style: `width:${total ? Math.round(100 * c.done / total) : 0}%` })),
      h('span', { class: 'tree-count muted small' }, `${c.done} of ${total}`)));
    if (open) for (const child of node.children) frag.append(treeRows(child, depth + 1, byId, data, expanded, select, repaint, bulk));
    return frag;
  }
  const item = byId[String(node.id)];
  const check = h('input', {
    type: 'checkbox', class: 'tree-check', 'aria-label': `Select #${node.id}`,
    onclick: ev => { ev.stopPropagation(); bulk.toggle(node.id, ev.currentTarget.checked); },
  });
  check.checked = bulk.selected.has(String(node.id));
  frag.append(h('div', {
    class: 'tree-row tree-task', role: 'treeitem', 'data-id': String(node.id), style: indent, draggable: 'true', onclick: () => select(node.id),
    ondragstart: ev => { ev.dataTransfer.setData('text/plain', String(node.id)); ev.dataTransfer.effectAllowed = 'move'; },
  },
  check,
  h('span', { class: 'inbox-id' }, `#${node.id}`),
    h('span', { class: 'tree-title' }, node.title),
    item ? triageBadges(item) : null,
    h('span', { class: `tree-status status-${node.status.replace(/ /g, '-')}` }, node.status)));
  return frag;
}

async function renderList(ctx, sel, select) {
  const { view, key } = ctx;
  const holder = h('div', { class: 'stack' });
  view.append(holder);
  let timer = null;
  const query = { state: '', q: '', page: 1 };
  const selected = new Set(); // ids of the checked task rows; survives a list reload
  const expanded = new Map(); // group id -> false when collapsed; kept here so it survives a list refresh

  async function load() {
    const filtered = !!(query.state || query.q);
    const params = new URLSearchParams({ pageSize: String(filtered ? PAGE_SIZE : TREE_PAGE_SIZE), page: String(filtered ? query.page : 1) });
    if (query.state) params.set('state', query.state);
    if (query.q) params.set('q', query.q);
    const [data, tree] = await Promise.all([get(`${base(key)}/tasks?${params}`), filtered ? null : get(`${base(key)}/tasks/tree`)]);
    query.page = data.page;
    clear(holder);
    holder.append(
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', {}, 'Tasks'), h('p', {}, 'Open work items with their triage, last run and pool state.')),
        h('div', { class: 'row' },
          h('button', { type: 'button', onclick: () => load().catch(e => toast(e.message, true)) }, 'Refresh'),
          data.localTasks ? h('button', { class: 'primary', type: 'button', onclick: () => showAddForm(ctx, holder, load) }, 'Add a task') : null)));
    if (data.legacyTaskFiles) {
      holder.append(h('div', { class: 'banner' }, 'This project still has task files in its folder. Run ', h('code', {}, 'autopilot task import-files'), ' to move them into the workspace so you can edit them here.'));
    }
    if (data.status.busy) {
      holder.append(h('div', { class: 'banner info' }, `A run is active (${data.status.owner}). `, h('a', { href: `#/p/${encodeURIComponent(key)}/runs` }, 'Follow it on the Runs page.')));
    }
    if (data.counts.all > 0) holder.append(filterBar(data, query, load));
    if (data.counts.all > 0 && !data.items.length) {
      holder.append(h('div', { class: 'card' }, emptyState('No task matches this filter.', { label: 'Clear the filter', onclick: () => { query.state = ''; query.q = ''; query.page = 1; load().catch(e => toast(e.message, true)); } }, 'Nothing to show')));
      return data;
    }
    if (!data.items.length) {
      holder.append(h('div', { class: 'card' }, emptyState(data.localTasks ? 'Write down one thing you want done. It does not need to be perfect: triage tells you what is missing.' : 'No open work items were found in the tracker.', data.localTasks ? { label: 'Add a task', onclick: () => showAddForm(ctx, holder, load) } : { label: 'Check the task source', href: `#/p/${encodeURIComponent(key)}/settings` }, data.localTasks ? 'No tasks yet' : 'Nothing to show')));
      return data;
    }
    if (tree && tree.length) {
      if (sel.auto || !sel.id) { sel.id = String(tree[0].id); sel.auto = false; }
      const box = h('div', { class: 'task-tree card', 'data-part': 'task-tree', role: 'tree' },
        h('div', { class: 'tree-head' }, 'TASKS AND GROUPS / PROGRESS'));
      const byId = Object.fromEntries(data.items.map(i => [String(i.id), i]));
      const groups = [];
      const collect = ns => { for (const n of ns) if (n.counts) { groups.push(n); collect(n.children); } };
      collect(tree);
      const bar = h('div', { class: 'card row between bulk-bar', 'data-part': 'selection-bar', hidden: true });
      const moveTo = async (ids, parentId) => {
        try {
          for (const id of ids) await post(`${base(key)}/tasks/${encodeURIComponent(id)}/parent`, { parentId });
          selected.clear();
          toast(ids.length === 1 ? 'Task moved.' : `${ids.length} tasks moved.`);
          await load();
        } catch (e) { toast(e.message, true); await load().catch(() => {}); }
      };
      const bulk = {
        selected,
        toggle: (id, on) => { if (on) selected.add(String(id)); else selected.delete(String(id)); paintBar(); },
        drop: (id, groupId) => { if (id) moveTo([id], groupId); },
      };
      function paintBar() {
        clear(bar);
        bar.hidden = selected.size === 0;
        if (!selected.size) return;
        const picker = h('select', { 'aria-label': 'Move to group', hidden: true },
          h('option', { value: '' }, 'Pick a group'),
          groups.map(g => h('option', { value: String(g.id) }, `#${g.id} ${g.title}`)));
        picker.addEventListener('change', () => { if (picker.value) moveTo([...selected], Number(picker.value)); });
        bar.append(
          h('span', {}, `${selected.size} selected`),
          h('div', { class: 'row' },
            h('button', { type: 'button', onclick: () => { picker.hidden = !picker.hidden; } }, 'Move to group...'),
            picker,
            h('button', { type: 'button', onclick: async () => {
              const ids = [...selected];
              try {
                for (const id of ids) await post(`${base(key)}/pool`, { id: Number(id) });
                toast(ids.length === 1 ? 'Added to the pool.' : `${ids.length} tasks added to the pool.`);
              } catch (e) { toast(e.message, true); }
            } }, 'Add to the pool'),
            h('button', { type: 'button', class: 'ghost', onclick: () => { selected.clear(); paint(); } }, 'Close')));
      }
      const paint = () => { clear(box); box.append(h('div', { class: 'tree-head' }, 'TASKS AND GROUPS / PROGRESS'), ...tree.map(n => treeRows(n, 0, byId, data, expanded, select, paint, bulk))); paintBar(); sel.paintList(); };
      sel.paintList = () => {
        for (const r of box.querySelectorAll('.tree-row')) {
          const on = r.dataset.id === sel.id;
          r.classList.toggle('selected', on);
          if (on) r.setAttribute('aria-current', 'true'); else r.removeAttribute('aria-current');
        }
      };
      paint();
      holder.append(bar, box);
      return data;
    }
    if (sel.auto || !sel.id) { sel.id = String(data.items[0].id); sel.auto = false; }
    const cards = h('div', { class: 'stack', 'data-part': 'task-cards' });
    sel.paintList = () => {
      for (const c of cards.children) {
        const on = c.dataset.id === sel.id;
        c.classList.toggle('selected', on);
        if (on) c.setAttribute('aria-current', 'true'); else c.removeAttribute('aria-current');
      }
    };
    for (const item of data.items) {
      const running = data.activeJobs[item.id];
      const triaging = data.triageJobs[item.id];
      cards.append(h('button', {
        type: 'button', class: 'card inbox-card task-card', 'data-id': String(item.id),
        onclick: () => select(item.id),
      },
      h('div', { class: 'inbox-head' },
        h('span', { class: 'inbox-id' }, `#${item.id}`),
        running ? badge('running', 'info') : null,
        item.inPool ? badge(`pool #${item.poolPosition}`, 'info') : null),
      h('div', { class: 'inbox-title' }, item.title),
      h('div', { class: 'row', style: 'margin-top:.25rem' }, triageBadges(item)),
      triageJobBadge(triaging)));
    }
    holder.append(cards);
    sel.paintList();
    holder.append(pager(data, query, load));
    return data;
  }

  sel.reloadList = () => load().catch(() => {});
  const data = await load();
  if (data.status.busy || Object.values(data.triageJobs).some(j => j.isActive)) timer = setInterval(() => load().catch(() => {}), 5000);
  return () => { if (timer) clearInterval(timer); };
}

async function start(button, { key }, item, reload) {
  const ok = await ask({
    title: `Start task ${item.id}?`,
    body: [h('p', {}, item.title), h('p', { class: 'muted' }, 'The agent works in a separate checkout and ends with a draft. Nothing is merged: a person decides.')],
    confirm: 'Start',
  });
  if (!ok) return;
  await busy(button, async () => {
    try { await post(`${base(key)}/tasks/${encodeURIComponent(item.id)}/start`); toast('Started. Follow it on the Runs page.'); await reload(); }
    catch (e) { toast(e.message, true); }
  });
}

async function toPool(button, { key }, item, reload) {
  await busy(button, async () => {
    try { await post(`${base(key)}/pool`, { id: item.id }); toast('Added to the pool.'); await reload(); }
    catch (e) { toast(e.message, true); }
  });
}

function triageJobBadge(job) {
  if (!job) return null;
  if (job.isActive) return h('div', { class: 'row', style: 'margin-top:.25rem' }, badge('triage running', 'info'));
  if (job.status === 'failed') return h('div', { class: 'small', style: 'margin-top:.25rem' }, badge('last triage failed', 'bad'), ' ', h('span', { class: 'muted' }, job.message || 'See the job log.'));
  return null;
}

function filterBar(data, query, load) {
  const go = () => load().catch(e => toast(e.message, true));
  const search = h('input', { type: 'search', placeholder: 'Search by title or number', value: query.q, 'aria-label': 'Search tasks', maxlength: 100 });
  search.addEventListener('keydown', ev => { if (ev.key === 'Enter') { query.q = search.value.trim(); query.page = 1; go(); } });
  return h('div', { class: 'card stack' },
    h('div', { class: 'row' }, STATES.map(([value, label, count]) => h('button', {
      type: 'button', class: query.state === value ? 'primary' : 'ghost', 'aria-pressed': String(query.state === value),
      onclick: () => { query.state = value; query.page = 1; go(); },
    }, `${label} (${data.counts[count]})`))),
    h('div', { class: 'row' }, search, h('button', { type: 'button', onclick: () => { query.q = search.value.trim(); query.page = 1; go(); } }, 'Search')));
}

function pager(data, query, load) {
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
  const go = page => { query.page = page; load().catch(e => toast(e.message, true)); };
  const first = (data.page - 1) * data.pageSize + 1;
  return h('div', { class: 'row between' },
    h('span', { class: 'muted small' }, `${first}-${Math.min(data.total, first + data.items.length - 1)} of ${data.total}`),
    pages > 1 ? h('div', { class: 'row' },
      h('button', { type: 'button', disabled: data.page <= 1, onclick: () => go(data.page - 1) }, 'Previous'),
      h('span', { class: 'muted small' }, `Page ${data.page} of ${pages}`),
      h('button', { type: 'button', disabled: data.page >= pages, onclick: () => go(data.page + 1) }, 'Next')) : null);
}

/**
 * Starts the triage of one task and follows its job until it ends, then tells the person how it went:
 * the error text when it failed. Used by the task list, the task page and Needs a person.
 */
export async function startTriage(button, key, id, after) {
  const label = button.textContent;
  await busy(button, async () => {
    try {
      let job = await post(`${base(key)}/tasks/${encodeURIComponent(id)}/triage`);
      toast('Triage is running. This takes a minute or two.');
      button.textContent = 'Triaging…';
      while (job.isActive) {
        await new Promise(r => setTimeout(r, 2000));
        job = (await get(`${base(key)}/jobs/${encodeURIComponent(job.id)}`)).job;
      }
      if (job.status === 'succeeded') toast('Triage done.');
      else toast(`Triage failed: ${job.message || 'see the job log'}`, true);
    } catch (e) { toast(e.message, true); }
    finally { button.textContent = label; }
    if (after) await after();
  });
}

function triage(button, { key }, item, reload) {
  return startTriage(button, key, item.id, reload);
}

function showAddForm(ctx, holder, reload) {
  const title = h('input', { type: 'text', maxlength: 300, required: true });
  const body = h('textarea', { rows: 6, placeholder: 'What should change, and how will you know it works?' });
  const labels = h('input', { type: 'text', placeholder: 'optional, comma separated' });
  const form = h('form', { class: 'card stack' },
    h('h2', {}, 'New task'),
    field('Title', title),
    field('Description', body, 'The agent reads this text. Say what done looks like: it makes triage and the gate far more reliable.'),
    field('Labels', labels),
    h('div', { class: 'row' },
      h('button', { class: 'primary', type: 'submit' }, 'Add task'),
      h('button', { type: 'button', onclick: () => form.remove() }, 'Cancel')));
  form.addEventListener('submit', async ev => {
    ev.preventDefault();
    try {
      await post(`${base(ctx.key)}/tasks`, { title: title.value, body: body.value, labels: labels.value.split(',').map(s => s.trim()).filter(Boolean) });
      toast('Task added.');
      await reload();
    } catch (e) { toast(e.message, true); }
  });
  holder.children[0].after(form);
  title.focus();
}

function triageSection(key, task, detail, reload) {
  const tr = detail.triage;
  const job = detail.triageJob;
  const failed = job && job.status === 'failed' && (!tr || new Date(job.createdAt) > new Date(tr.triagedAt));
  return h('section', { class: 'card stack' },
    h('div', { class: 'row between' }, h('h2', {}, 'Triage'),
      task.closed ? null : h('button', { type: 'button', class: tr && !tr.stale ? 'ghost' : '', disabled: job?.isActive, onclick: ev => startTriage(ev.currentTarget, key, task.id, reload) }, tr ? 'Triage again' : 'Triage')),
    job?.isActive ? h('p', {}, badge('triage running', 'info'), ' Reload in a moment to see the result.') : null,
    failed ? h('div', { class: 'banner bad' }, `The last triage failed: ${job.message || 'see the job log'}`) : null,
    tr
      ? [h('div', { class: 'row' },
        badge(tr.label, tr.label === 'agent-ready' ? 'ok' : 'warn'),
        badge(`${tr.risk} risk`, tr.risk === 'high' ? 'bad' : ''),
        tr.stale ? badge('stale: text changed since triage', 'warn') : null,
        h('span', { class: 'muted small' }, `triaged ${ago(tr.triagedAt)}`)),
      h('p', { class: 'prewrap' }, tr.reason)]
      : h('p', { class: 'muted' }, 'Not triaged yet. Triage tells you whether the agent can do this alone or a person must decide first.'));
}

async function renderDetail(ctx, id, changed) {
  const { view, key } = ctx;
  const holder = h('div', { class: 'stack' });
  view.append(holder);
  const number = Number(id);

  async function load() {
    let detail;
    try { detail = await get(`${base(key)}/tasks/${number}`); }
    catch (e) { clear(holder); holder.append(h('div', { class: 'banner bad' }, e.message)); return; }
    const t = detail.task;
    const title = h('input', { type: 'text', value: t.title, maxlength: 300 });
    const body = h('textarea', { rows: 8 }); body.value = t.body;
    const labels = h('input', { type: 'text', value: t.labels.join(', ') });
    const comment = h('textarea', { rows: 3, placeholder: 'Write a comment' });
    clear(holder);
    holder.append(
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', {}, `#${t.number} ${t.title}`),
          h('p', {}, badge(t.state, t.closed ? '' : 'ok'), ' ', `added by ${t.author} ${ago(t.createdAt)}`)),
        h('div', { class: 'row' },
          t.closed ? null : h('button', { type: 'button', onclick: async ev => { await busy(ev.currentTarget, async () => { try { await post(`${base(key)}/tasks/${encodeURIComponent(t.number)}/start`); toast('Started.'); } catch (e) { toast(e.message, true); } }); } }, 'Start'),
          t.closed ? null : h('button', { type: 'button', onclick: async () => { try { await post(`${base(key)}/pool`, { id: t.id }); toast('Added to the pool.'); } catch (e) { toast(e.message, true); } } }, 'Add to pool'),
          h('button', { type: 'button', class: t.closed ? '' : 'danger', onclick: async () => {
            try { await post(`${base(key)}/tasks/${t.number}/${t.closed ? 'reopen' : 'close'}`); await load(); } catch (e) { toast(e.message, true); }
          } }, t.closed ? 'Reopen' : 'Close task'))),
      triageSection(key, t, detail, load),
      h('form', { class: 'card stack', onsubmit: async ev => {
        ev.preventDefault();
        try { await put(`${base(key)}/tasks/${t.number}`, { title: title.value, body: body.value, labels: labels.value.split(',').map(s => s.trim()).filter(Boolean) }); toast('Saved. Triage is now stale: triage again.'); await load(); }
        catch (e) { toast(e.message, true); }
      } },
        h('h2', {}, 'Text'), field('Title', title), field('Description', body), field('Labels', labels),
        h('div', {}, h('button', { class: 'primary', type: 'submit' }, 'Save changes'))),
      h('section', { class: 'card' }, h('h2', {}, 'Comments'),
        detail.comments.length ? detail.comments.map(c => h('div', { class: 'comment' }, h('div', { class: 'who' }, `${c.author} · ${ago(c.createdAt)}`), h('div', { class: 'body' }, c.body))) : h('p', { class: 'muted' }, 'No comments yet.'),
        h('div', { class: 'stack' }, comment, h('button', { type: 'button', onclick: async () => {
          try { await post(`${base(key)}/tasks/${t.number}/comments`, { text: comment.value }); await load(); } catch (e) { toast(e.message, true); }
        } }, 'Add comment'))),
      h('section', { class: 'card' }, h('h2', {}, 'History'),
        detail.history.length ? h('ul', { class: 'stack', style: 'padding-left:1.1rem;margin:0' }, detail.history.map(e => h('li', {}, `${e.summary} `, h('span', { class: 'muted small' }, ago(e.createdAt)), e.detail ? h('div', { class: 'muted small prewrap' }, e.detail) : null))) : h('p', { class: 'muted' }, 'Nothing yet.')));
    if (changed) changed();
  }
  await load();
}
