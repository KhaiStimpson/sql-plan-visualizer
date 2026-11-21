# Gauge components

`components.css` implements these patterns for the web. Load it after `theme.css` and put `class="g-root"` on `body` or on the region that uses Gauge. [reference.html](reference.html) uses every component here. On other stacks, rebuild each pattern from the anatomy, sizes, states, and keyboard behaviour below.

Sizes are for desktop at 13px base type. On touch, raise every hit target to at least 44 by 44px (40px on dense desktop-class tablets) and keep the type sizes.

## Shared states

| State | Treatment |
| --- | --- |
| Hover | Neutral surfaces step up one level (`bg-2` → `bg-3` → `bg-4`). Text links and entities turn `accent`. |
| Keyboard focus | 2px `accent` outline, 2px offset, `r-3` corners. Inside a clipped container (segmented control, menu), use a -2px offset instead. Never remove it. |
| Pressed or current | `bg-4` fill, `fg-0` text, weight 600. Toggle buttons use an `accent` border and text. |
| Selected record | `sel` fill, `sel-fg` text, and a 2px inset leading edge in `sel-edge`. Use `sel-warn` and `sel-crit` for state text inside it. |
| Disabled | 45% opacity, default cursor, and no hover change. Explain what would enable it nearby, rather than in a tooltip. |
| Motion | 120ms colour transitions only. None when the system asks for reduced motion. |

## Button

`.g-btn`, `.g-btn--primary`, `.g-btn--ghost`

- **Size:** 26px minimum height, 3px × 11px padding, `r-2` corners, 8px gap to an icon or `.g-kbd`.
- **Secondary (default):** `bg-3` fill, `line-2` border, `fg-0` text. Hover uses `bg-4`.
- **Primary:** `accent` fill and border, `accent-ink` text, weight 600. Use at most one per view or dialog.
- **Ghost:** no fill and a `line-ctl` border, because the border is its only boundary. Use it in headers and toolbars.
- **Toggle:** a button with `aria-pressed="true"` gets an `accent` border and text.
- **Keyboard:** a native `<button>`, so Enter and Space activate it.
- Label it with a verb and an object, such as “Retry job”. Keep the button in place and disable it rather than hiding it when the action is unavailable.

## Field

`.g-field` on `input`, `select`, and `textarea`

- **Size:** 26px minimum height, 3px × 8px padding. A text area has a 64px minimum height and resizes vertically.
- `bg-0` fill and a `line-ctl` border, which meets 3:1 against every pane surface. Placeholder text uses `fg-3` and never replaces a label.
- Always pair it with a visible `<label>`. In a status bar, a compact variant with 20px height and 11.5px type is acceptable.

## Segmented control

`.g-seg` containing `<button aria-pressed>`

- For two to five short, mutually exclusive views or filters. Use a select for more options.
- **Size:** 22px minimum height, 2px × 10px padding, 12px type. The outer border is `line-ctl` and the dividers are `line-2`.
- **States:** off is `fg-2` with no fill. Hover uses `bg-3`. On uses `bg-4`, `fg-0`, and weight 600.
- **Semantics:** a `role="group"` with an `aria-label`. Each option is a toggle button, and exactly one is pressed.
- **Keyboard:** each option is a tab stop. Enter and Space select an option. The change applies immediately, with no confirm step.

## Entity link

`.g-entity`, `.g-entity--sans`

- Any named thing that opens details, such as a job, thread, object, or file. Entities are underlined text, never badges.
- 12px monospace in `fg-0`, with a `fg-3` underline at 3px offset. Hover turns the text and underline `accent`. Use `--sans` at 13px for human-readable names.
- **Semantics:** a `<button>` when it opens an in-app inspector, or an `<a>` when it navigates. After it selects a record, give it `aria-current="true"`.
- **Keyboard:** Enter activates it. If the entity has secondary actions (copy, pin, show elsewhere), offer them in a context menu that opens with right-click, Shift+F10, and the Menu key.

## Severity label

`.g-sev`, `.g-sev--crit`, `.g-sev--warn`, `.g-sev--ok`

- Always a word, such as Critical, Waiting, or Healthy. Colour reinforces the word and never replaces it.
- 12.5px, weight 600, no wrapping. Neutral states use `fg-2`. Only critical, warning, and confirmed-healthy states carry colour.
- A confidence qualifier follows as text, for example “Critical · high confidence”.

## Navigation rail

`.g-nav` containing links or buttons

- **Width:** 172px. **Items:** 30px tall, 0 × 9px padding, `r-3` corners, 2px apart, 15px stroke icons with a 9px gap.
- `bg-2` rail, `fg-2` items. Hover uses `bg-3` and `fg-0`. The current item uses `aria-current="page"`, `bg-4`, `fg-0`, and weight 600.
- **Counts:** `.g-count` is right-aligned 12px `fg-3`, switching to `fg-2` on hover and current items because `fg-3` falls below 4.5:1 on `bg-3` and `bg-4`. A count that needs attention uses `.g-count--crit`.
- Separate groups with an `hr`. Below 720px, the rail becomes a wrapping row above the content.

## Data table

`.g-table`, `.g-num`, `tr.is-selected`

- 6px × 14px cells, with an 8px horizontal padding below 720px. 12px `fg-2` headers on `bg-2` with weight 500. 1px `line` row separators.
- Right-align quantities with `.g-num`, which uses tabular numerals.
- **Hover:** `bg-2`. **Selected:** `.is-selected` (see shared states). The selected row's entity link carries `aria-current="true"`.
- **Keyboard:** each row's entity link is the tab stop, and Enter selects its row. Selecting a row updates the inspector, but focus stays in the table. Start with a relevant record selected.

## Pane header

`.g-pane-h` with `.g-facts` and `.g-actions`

- 8px × 14px padding and a 1px `line` bottom border. 14px weight-600 title.
- `.g-facts` holds inline counts and metadata in 12.5px `fg-2`, such as “1 failed · 1 waiting”. Prefer this over a row of stat cards.
- `.g-actions` pushes controls to the right with 8px gaps and wraps under the title when space runs out.

## Menu and context menu

`.g-menu` with `role="menu"` and `role="menuitem"`, `menuitemcheckbox`, or `menuitemradio`

- At least 190px wide, 4px padding, `bg-2` fill, `line-2` border, `r-3` corners, `0 8px 24px shadow`.
- **Items:** 6px × 10px padding, `fg-1`. Hover and focus use `bg-4` and `fg-0`. A checked item uses weight 600. Put a shortcut or hint in `.g-note` at 12px `fg-2`, right-aligned. Separate groups with an `hr`.
- **Menu button:** use `aria-haspopup="menu"` and `aria-expanded`. Enter, Space, or Down opens the menu and focuses the first item. Up opens it on the last item.
- **Inside the menu:** Up and Down move focus and wrap, and Home and End jump to the ends. Enter or Space runs the item, closes the menu, and returns focus to the menu button. Escape closes the menu and returns focus. Tab or a pointer press outside closes it without moving focus back.
- **Context menu:** the same component opened at the pointer, or at the focused element for Shift+F10 and the Menu key. Keep it inside the viewport.

## Command palette

`.g-scrim` and `.g-palette` (`role="dialog"`, `aria-modal="true"`)

- Up to 600px wide (with 12px margins on narrow screens), placed 70px from the top. `bg-2` fill, `line-2` border, 6px corners, `0 12px 32px shadow`. The scrim behind it is `scrim`.
- **Input:** 15px type, 14px × 16px padding, a `line` divider below, and `role="combobox"` with `aria-controls` pointing to the list.
- **List:** a `role="listbox"` of `role="option"`, grouped under 12px `fg-2` labels. The active option uses the selected-record treatment and is announced through `aria-activedescendant`. Focus stays in the input.
- **Keyboard:** Ctrl+K (Cmd+K on macOS) toggles the palette. Typing filters the list, and the first match becomes active. Up and Down move the active option and wrap. Enter runs it. Escape, or a click on the scrim, closes the palette. Closing returns focus to the control that opened it. If the command removed that control, for example by re-rendering a list, focus goes to the record the command selected. Tab does not leave the dialog.
- Offer a visible button that opens the palette too, with the shortcut shown in a `.g-kbd`.

## Smaller pieces

- **`.g-kbd`:** 11px monospace, `bg-2`, `line-2` border with a 2px bottom border, 0 × 5px padding.
- **`.g-term`:** a glossary term with a dotted `fg-3` underline and a help cursor. Its definition must also be available without hovering, for example in a glossary view.
- **`.g-callout`:** 12px × 16px padding, `bg-2`, `line-2` border, 18px vertical margin. Use `.g-callout--crit` for a blocking problem, and put the problem and next step in the text.
- **`.g-statusbar`:** 24px minimum height, `bg-2`, a `line` top border, 11.5px `fg-2` text, 16px gaps. It holds counts, background progress, and the theme control.
- **`.g-mono`:** 12px monospace for identifiers, code, and stack frames.
- **`.g-sr-only`:** hides text visually but keeps it available to screen readers.

## Forced colours

In Windows high contrast and other forced-colour modes, the system replaces Gauge's palette. `components.css` restores the cues that relied on fill alone. Selected rows, the active palette option, the current nav item, and pressed segments use `Highlight` and `HighlightText`, and control borders use `ButtonText`. Rebuild those mappings on other stacks, such as with `SystemColors` on Windows.
