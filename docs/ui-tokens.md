# UI design tokens

Source of truth: the token blocks at the top of `src/Autopilot.Web/wwwroot/app.css`
(`:root` for light, `@media (prefers-color-scheme: dark)` and `:root[data-theme="dark"]` for dark).
`tests/Autopilot.Web.Tests/PaletteTests.cs` checks the values, the contrast and that every `var(--x)` resolves.

The look is compact and warm: tight spacing, small controls and a paper-like palette.

## Colour

| Token | Dark | Light | Use |
|---|---|---|---|
| `--bg` | `#161412` | `#f3f0ea` | page background |
| `--surface` | `#1d1b18` | `#fbfaf7` | cards, top bar, inputs |
| `--surface-2` | `#25221f` | `#eeeae3` | hover, chips, inset areas |
| `--surface-3` | `#2f2b26` | `#e4dfd6` | pressed, deepest inset |
| `--border` | `#38342e` | `#d9d3c8` | hairlines |
| `--border-strong` | `#4a453d` | `#bdb5a6` | control borders |
| `--text` | `#ebe6de` | `#24201b` | body text |
| `--text-2` | `#cbc4b9` | `#453e35` | secondary text |
| `--muted` | `#a29a8e` | `#665d51` | hints, labels |
| `--accent` | `#6ba4de` | `#2b6cb0` | primary action, focus |
| `--accent-text` | `#161412` | `#ffffff` | text on `--accent` |
| `--accent-soft` | `#1f2a38` | `#eaf1f9` | selected, info background |
| `--link` | `#86b6e8` | `#2b6cb0` | links |
| `--ok` / `--ok-soft` | `#62b57f` / `#1c2a21` | `#22703c` / `#dfeee3` | success |
| `--warn` / `--warn-soft` | `#e2ac58` / `#33291a` | `#85550a` / `#f5e9cd` | attention |
| `--bad` / `--bad-soft` | `#e2685d` / `#36211e` | `#ad2f27` / `#f8e1de` | failure |
| `--run` / `--run-soft` | `#6ba4de` / `#1f2c3d` | `#2b6cb0` / `#dfe9f5` | running |
| `--code-bg` | `#110f0d` | `#e4dfd6` | code and log blocks |
| `--shadow` | `none` | warm two-layer shadow | cards |

The light values for `--border-strong`, `--text-2`, `--link` and the soft/run/code colours are
not part of the brief; they are our own picks. Dark `--text-2`, `--muted`, `--link` and the others in the brief are as given.

## Spacing

Scale: `--sp-2` 2px, `--sp-4` 4px, `--sp-6` 6px, `--sp-8` 8px, `--sp-10` 10px, `--sp-12` 12px, `--sp-16` 16px, `--sp-20` 20px.

Old names stay: `--s1` = 4px, `--s2` = 8px, `--s3` = 12px, `--s4` = 16px, `--s5` = 20px, `--s6` = 32px.

`--gutter` 10px (gap between panels). `--left-col` 340px (left column of the two-column pages).

## Radius

`--radius-panel` 8px (cards, panels), `--radius-control` 6px (buttons, inputs).
Old names: `--radius` = panel, `--radius-sm` = control, `--radius-pill` 999px.

## Type

| Token | Value | Note |
|---|---|---|
| `--fs-body` | 13px | body text; `--fs-md` follows it |
| `--fs-label` | 10.5px | caps labels (table heads); `--fs-xs` follows it |
| `--fs-sm` | 12px | |
| `--fs-lg` | 15px | |
| `--fs-xl` | 20px | |
| `--fs-stat` | 24px | |

## Sizes

| Token | Desktop | Touch / phone | Applied to |
|---|---|---|---|
| `--h-control` | 32px | 44px | buttons, inputs, selects, range |
| `--h-row-control` | 24px | 44px | `button.sm` inside rows |
| `--h-hit` | 32px | 44px | hit area of `button.sm` (invisible `::after`) |
| `--h-chip` | 28px | 32px | `.chip` |
| `--h-topbar` | 40px | 44px | `.topbar` |
| `--h-tab` | 34px | 44px | `.tabs a` |
| `--h-touch` | 44px | 44px | the minimum touch target |

## Touch rules

`@media (pointer: coarse), (max-width: 640px)` (either a coarse pointer or a phone width) sets
`--fs-body` to 14px and raises `--h-control`, `--h-row-control`, `--h-hit`, `--h-topbar` and `--h-tab` to 44px
(`--h-chip` to 32px). Controls therefore keep a 44px minimum height there.

## Contrast (WCAG 2.x, AA needs 4.5:1)

| Pair | Dark on `--bg` | Dark on `--surface` | Light on `--bg` | Light on `--surface` |
|---|---|---|---|---|
| `--text` | 14.79 | 13.83 | 14.23 | 15.51 |
| `--text-2` | 10.62 | 9.93 | 9.26 | 10.09 |
| `--muted` | 6.61 | 6.18 | 5.68 | 6.19 |
| `--accent` | 6.99 | 6.54 | 4.77 | 5.19 |
| `--link` | 8.64 | 8.08 | 4.77 | 5.19 |
| `--ok` | 7.38 | 6.90 | 5.35 | 5.83 |
| `--warn` | 8.99 | 8.41 | 5.59 | 6.09 |
| `--bad` | 5.58 | 5.21 | 5.75 | 6.26 |

Badges and buttons:

| Pair | Dark | Light |
|---|---|---|
| `--ok` on `--ok-soft` | 6.01 | 5.06 |
| `--warn` on `--warn-soft` | 6.98 | 5.28 |
| `--bad` on `--bad-soft` | 4.57 | 5.23 |
| `--accent` on `--accent-soft` | 5.52 | about 4.8 (tested >= 4.5) |
| `--accent-text` on `--accent` | 6.99 | 5.42 |
