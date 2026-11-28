import { h, ago } from './dom.js';

/** How many merged tasks the collapsed Integration branch block shows. */
export const COLLAPSED_COUNT = 5;
export const EXPANDED_KEY = 'autopilot-integration-expanded';

export function readExpanded() {
  try { return localStorage.getItem(EXPANDED_KEY) === 'true'; } catch { return false; }
}

function saveExpanded(value) {
  try { localStorage.setItem(EXPANDED_KEY, value ? 'true' : 'false'); } catch { /* the choice just is not remembered */ }
}

/** Newest merge first. */
export function newestFirst(merges) {
  return [...merges].sort((a, b) => new Date(b.mergedAt) - new Date(a.mergedAt));
}

const dayStart = d => new Date(d.getFullYear(), d.getMonth(), d.getDate());

/** 'Today', 'Yesterday' or a date, in the browser's local time. */
export function dayLabel(when, now = new Date()) {
  const diff = Math.round((dayStart(now) - dayStart(new Date(when))) / 86400000);
  if (diff === 0) return 'Today';
  if (diff === 1) return 'Yesterday';
  return new Date(when).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** Groups merges (already newest first) by local day: [{ label, merges }]. */
export function groupByDay(merges, now = new Date()) {
  const groups = [];
  for (const m of merges) {
    const label = dayLabel(m.mergedAt, now);
    const last = groups[groups.length - 1];
    if (last && last.label === label) last.merges.push(m); else groups.push({ label, merges: [m] });
  }
  return groups;
}

const mergeRow = m => h('li', { class: 'integration-row' }, `#${m.workItemId} (${m.taskBranch}) `, h('span', { class: 'muted' }, ago(m.mergedAt)));

/** The merged-task list: 5 newest when collapsed, every merge grouped by day when expanded. */
export function mergesList(merges) {
  const holder = h('div', { class: 'integration-merges', 'data-part': 'integration-merges' });
  if (!merges.length) return holder;
  const sorted = newestFirst(merges);
  let expanded = readExpanded() && sorted.length > COLLAPSED_COUNT;
  const draw = () => {
    holder.replaceChildren(
      h('p', { class: 'muted small', 'data-part': 'integration-count' }, `${sorted.length} ${sorted.length === 1 ? 'task' : 'tasks'} merged`),
      expanded
        ? h('div', {}, groupByDay(sorted).map(g => h('div', { class: 'integration-day' },
            h('h3', { class: 'integration-day-heading' }, g.label),
            h('ul', { class: 'integration-list' }, g.merges.map(mergeRow)))))
        : h('ul', { class: 'integration-list' }, sorted.slice(0, COLLAPSED_COUNT).map(mergeRow)),
      sorted.length > COLLAPSED_COUNT
        ? h('button', { type: 'button', class: 'link', 'data-part': 'integration-toggle', onclick: () => { expanded = !expanded; saveExpanded(expanded); draw(); } },
            expanded ? 'Show fewer' : `Show all ${sorted.length}`)
        : null);
  };
  draw();
  return holder;
}
