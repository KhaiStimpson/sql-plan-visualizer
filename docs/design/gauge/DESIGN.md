# Gauge design guide

Gauge is a quiet, dense interface for inspection tools. Neutral greys do the structural work. One accent covers links, focus, and the primary action. Colour is reserved for meaning, and that meaning is always also written as text. Component specs are in [COMPONENTS.md](COMPONENTS.md). How to carry the theme into native chrome, SVG, exports, and print is in [PLATFORMS.md](PLATFORMS.md).

## Surfaces and hierarchy

Start with a quiet working surface (`bg-0`). Use `bg-1` for an adjacent reading pane, `bg-2` for chrome and table headers, `bg-3` for controls, and `bg-4` for an active navigation item or a hovered control. A single 1px `line` separates neighbouring regions. Use `line-2` for dividers inside a control and for borders on filled controls. Use `line-ctl` wherever a border is a control's only boundary, such as fields, ghost buttons, and the outside of a segmented control. Both themes preserve these roles.

Use the strongest text (`fg-0`) for headings and values, `fg-1` for body copy, `fg-2` for labels, and `fg-3` for quieter metadata. Prefer a heading followed by a row of related facts over a dashboard of individual cards.

## Contrast

Every pairing below meets WCAG 2.2 AA in both themes: 4.5:1 for text, 3:1 for boundaries and indicators. `python check-contrast.py` verifies them after any token change.

| Role | Allowed on |
| --- | --- |
| `fg-0`, `fg-1`, `fg-2` | any surface, including `sel` |
| `code` | `bg-0` to `bg-4` |
| `accent`, `crit`, `warn`, `info` (as text) | `bg-0` to `bg-3` |
| `fg-3`, `ok`, `val-str`, `val-num` | `bg-0` to `bg-2` only |
| `sel-fg`, `sel-warn`, `sel-crit` | `sel` |
| `accent-ink` | `accent` |
| `tm-fg` | `tm-1` to `tm-9` |
| `line-ctl`, `sel-edge` (boundaries) | `bg-0` to `bg-2` |

When quiet text lands on a raised surface, such as a count in a hovered or current nav item, step it up to `fg-2`. `line` and `line-2` are decorative separators. They don't meet 3:1 and must never be the only thing that identifies a control.

## Type and density

The desktop baseline is 13px system sans at 1.45 line height. Use weight 600 for headings and important states. Set identifiers, code, and stack frames in 12px monospace. Right-align comparable quantities and use tabular numerals. Keep prose and navigation in sans. Use compact 3–4px radii, 8px between related controls, and about 14px of horizontal pane padding. Increase hit areas for touch, and respect system text scaling.

## Interaction

Use the accent for links, keyboard focus, and the primary action. Pair a filled accent button with `accent-ink`. Secondary buttons use a neutral surface and border. An entity that opens details is an underlined text button. Keep a visible 2px accent keyboard focus ring with a 2px offset. Hover can change a neutral surface by one step. Keep any transition brief (about 120ms) and honour reduced motion.

Use text labels for severity. Critical uses `crit`, warning uses `warn`, and confirmed healthy states can use `ok`. A neutral condition needs only text. On a selected row, use `sel`, `sel-fg`, `sel-warn`, and `sel-crit` together, with a 2px inset leading edge in `sel-edge`. Dark selection uses white text on deep blue with a light-blue edge. Light selection uses a pale blue fill, a blue edge, and dark red or amber state text. The fill alone is too close to the page to identify the selection, so always draw the edge. The written state stays legible when selection changes its colour.

## Optional workbench

For inspection tools, put navigation on the left, the working list in the centre, and the selected record in a docked inspector alongside it. A command palette (Ctrl+K) reaches every view and record. A status bar can show record counts and hold the theme control. Start with a relevant record selected. Selecting another updates the inspector while keyboard focus stays on the initiating control. At narrow widths, the navigation becomes a row and the inspector stacks beneath the list. Other products can use the same roles in their own navigation and layouts.

## Differences from Dump Debugger

Gauge fixes three accessibility gaps the app had when this kit was extracted. It adds `line-ctl` for control borders, sets the dark `sel-edge` to the accent instead of transparent, and limits `fg-3` to pane surfaces. [PLATFORMS.md](PLATFORMS.md#known-drift-in-dump-debugger) lists the app's surfaces that drifted from its own palette.

## Check an adaptation

1. Run `python check-contrast.py` if you changed tokens.
2. Open the reference offline. Select a waiting record and compare its label, fill, and edge in both themes.
3. Tab to a different record before activating it. The focused control and the selected record should be separately identifiable.
4. Open the command palette with Ctrl+K, move with the arrow keys, and press Escape. Focus should return where it was.
5. Open the More menu from the keyboard, move through it, and press Escape. Focus should return to the button.
6. Check that critical and waiting remain distinguishable by their words, and that everything fits at 320px wide.
7. Turn on Windows contrast themes, or emulate `forced-colors: active`. Selection, the current nav item, and focus should still be visible.
