// Tiny helpers. Text is always set as text, never as HTML.
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (key === 'style') el.style.cssText = value; // set through the CSSOM: the content policy blocks style attributes
    else if (key === 'value') el.value = value;
    else if (key === 'checked' || key === 'disabled' || key === 'selected' || key === 'open') el[key] = !!value;
    else el.setAttribute(key, value === true ? '' : String(value));
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

/** Like el.append, but skips null, undefined and false (native append would print "null"). */
export function put(el, ...children) { return append(el, children); }

export function clear(el) { el.replaceChildren(); return el; }

export function toast(message, bad = false) {
  const box = document.getElementById('toasts');
  const item = h('div', { class: 'toast' + (bad ? ' bad' : '') }, message);
  box.append(item);
  setTimeout(() => item.remove(), bad ? 8000 : 3500);
}

export function ask({ title, body, confirm = 'Confirm', danger = false, cancel = 'Cancel' }) {
  const dialog = document.getElementById('dialog');
  return new Promise(resolve => {
    const done = value => { dialog.close(); resolve(value); };
    clear(dialog);
    dialog.append(
      h('h2', {}, title), h('div', { class: 'stack' }, body),
      h('div', { class: 'row end' },
        h('button', { type: 'button', onclick: () => done(false) }, cancel),
        h('button', { type: 'button', class: danger ? 'danger' : 'primary', onclick: () => done(true) }, confirm)));
    dialog.addEventListener('cancel', () => resolve(false), { once: true });
    dialog.onkeydown = ev => trapTab(dialog, ev);
    dialog.showModal();
  });
}

/** Keeps Tab and Shift+Tab inside the dialog: from the last control back to the first, and the other way round. */
function trapTab(dialog, ev) {
  if (ev.key !== 'Tab') return;
  const items = [...dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])')].filter(el => !el.disabled && !el.hidden);
  if (!items.length) return;
  const first = items[0];
  const last = items[items.length - 1];
  if (ev.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) { ev.preventDefault(); last.focus(); }
  else if (!ev.shiftKey && document.activeElement === last) { ev.preventDefault(); first.focus(); }
}

export function ago(iso) {
  if (!iso) return '';
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'just now';
  for (const [size, name] of [[86400, 'd'], [3600, 'h'], [60, 'min']]) {
    if (seconds >= size) return `${Math.floor(seconds / size)} ${name} ago`;
  }
  return '';
}

export function badge(text, kind = '') { return h('span', { class: 'badge ' + kind }, text); }

export function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', { class: 'lbl' }, label), input, hint ? h('p', { class: 'hint' }, hint) : null);
}

/** A newcomer-friendly empty state. `action` is { label, href } or { label, onclick }: the next step as a button. */
export function emptyState(text, action, title) {
  const button = action && (action.href
    ? h('a', { class: 'btn primary', href: action.href }, action.label)
    : h('button', { type: 'button', class: 'primary', onclick: action.onclick }, action.label));
  return h('div', { class: 'empty' }, h('div', { class: 'icon', 'aria-hidden': 'true' }, '+'), title ? h('h2', {}, title) : null, h('p', {}, text), button);
}

const pad2 = n => String(n).padStart(2, '0');
export function clock(date = new Date()) { return `${pad2(date.getHours())}:${pad2(date.getMinutes())}:${pad2(date.getSeconds())}`; }

/**
 * The reusable stats strip: counts on the left, "updated HH:MM:SS every 10 s" and a Refresh button on the right.
 * `counts` is a list of [label, value]; `onRefresh` may return new counts. Returns the element with
 * `update(counts)` to redraw the counts and `stop()` to end the 10 s timer.
 */
export function statsStrip({ counts = [], onRefresh, every = 10000 } = {}) {
  const left = h('div', { class: 'stats-counts' });
  const stamp = h('span', { class: 'stats-updated' });
  const el = h('div', { class: 'stats-strip', role: 'group', 'aria-label': 'Counts' });
  const draw = list => { left.replaceChildren(); for (const [label, value] of list) left.append(h('span', { class: 'stat' }, h('b', {}, String(value)), ' ' + label)); };
  const touch = () => { stamp.textContent = `updated ${clock()} every ${Math.round(every / 1000)} s`; };
  const run = async () => {
    try { const next = onRefresh ? await onRefresh() : null; if (Array.isArray(next)) draw(next); }
    finally { touch(); }
  };
  const button = h('button', { type: 'button', class: 'sm', onclick: () => run().catch(e => toast(e.message, true)) }, 'Refresh');
  draw(counts); touch();
  el.append(left, h('div', { class: 'stats-right' }, stamp, button));
  const timer = every > 0 ? setInterval(() => { if (!document.hidden && el.isConnected) run().catch(() => {}); }, every) : null;
  el.update = draw;
  el.stop = () => { if (timer) clearInterval(timer); };
  return el;
}

export function loading(text = 'Loading…') {
  return h('div', { class: 'loading', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), text);
}

export async function busy(button, work) {
  button.disabled = true;
  try { return await work(); } finally { button.disabled = false; }
}

// The detail panel sticks while a long list scrolls, but only when it fits the viewport; otherwise it scrolls with the page.
export function keepStickyFit(el) {
  const fit = () => el.classList.toggle('is-sticky', el.offsetHeight + 32 <= window.innerHeight);
  if (!el.isConnected) return;
  if (!el._stickyWatch && typeof ResizeObserver === 'function') { el._stickyWatch = new ResizeObserver(fit); el._stickyWatch.observe(el); }
  fit();
}
