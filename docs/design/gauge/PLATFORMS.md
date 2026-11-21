# Using Gauge across platforms

Gauge's source of truth is `tokens.json`. `theme.css` is one rendering of it. The rules below keep every other surface, such as window chrome, SVG, exports, and print, on the same palette.

## Apply the theme before first paint

A stored preference must be applied before the stylesheet renders. Otherwise, the page paints in the system theme and then flips. In the `<head>`, before the stylesheet links, add a blocking inline script:

```html
<script>
  try {
    const saved = localStorage.getItem('gauge.theme');
    if (saved === 'light' || saved === 'dark') document.documentElement.dataset.theme = saved;
  } catch {}
</script>
```

Store only `light` or `dark`. To follow the system, remove the key and the attribute, and the CSS media query takes over. If your code needs to know the resolved theme (for chrome, canvas, or charts), read `data-theme`, or fall back to `matchMedia('(prefers-color-scheme: light)')`. Listen for the media query's `change` event while the preference is System.

Server-rendered apps can read the preference from a cookie instead and set `data-theme` in the HTML they return.

## Native window chrome

Pass the resolved theme to the host whenever it changes, and colour the host from the same tokens:

| Host surface | Token |
| --- | --- |
| Window and web view background (shown before and between page paints) | `bg-0` |
| Custom title bar | `bg-2`, with a `line` bottom border |
| Caption button glyph | `fg-2` |
| Caption button glyph, inactive window | `fg-3` |
| Caption button hover background and glyph | `bg-3`, `fg-0` |
| Caption button pressed background and glyph | `bg-4`, `fg-0` |
| Close button hover | the platform's own red. Don't substitute `crit`. |
| Native dialogs and menus | the platform's light or dark theme, matching the resolved theme |

On Windows App SDK with WebView2, set `WebView2.DefaultBackgroundColor`, the root element's background and `RequestedTheme`, and `AppWindow.TitleBar` button colours. On Electron, set `BrowserWindow`'s `backgroundColor` and `titleBarOverlay.color` and `symbolColor`. On macOS, set the window's `appearance` to Aqua or Dark Aqua and its `backgroundColor`.

Native code can't read CSS variables, so generate a constants file from `tokens.json` or copy the hex values into it, and keep it next to the token source. Eight-digit hex is `#RRGGBBAA`. Windows `Color.FromArgb` takes alpha first.

## SVG, canvas, and charts

- **Inline SVG and HTML-rendered charts:** use `var(--gauge-*)` in `fill`, `stroke`, and `style` so they follow the theme with no extra code.
- **Canvas and libraries that need literal colours:** resolve with `getComputedStyle(document.documentElement).getPropertyValue('--gauge-accent')`, then redraw when the theme changes. Observe `data-theme` with a `MutationObserver`, and listen for the media query's `change` event.
- **Series colours:** Gauge has no categorical palette. Use the neutral scale and `accent` for one highlighted series, and the severity roles only when the data means severity. The `tm-1`…`tm-9` ramp is a sequential scale (for example, for a treemap ordered by size) with `tm-fg` as the label colour on every step.

## Exports

Anything that leaves the page, such as an HTML report, an SVG download, or an image, can't depend on the live stylesheet. Inline literal hex values taken from `tokens.json` for the theme the user chose. A shared document defaults to `light`, which reads well in email, on paper, and in viewers that ignore dark mode. Embed the font stacks as written. Don't embed font files unless you have a licence to.

## Print

`theme.css` switches to the light palette under `@media print`, whatever the screen theme. In your print stylesheet:

- Hide the navigation, status bar, scrims, menus, and the command palette.
- Expand scroll containers (`overflow: visible`, `height: auto`) so content isn't clipped.
- Keep severity words and selection edges, since browsers drop background fills by default. Don't rely on `sel` or `-dim` fills to carry meaning.
- Add `print-color-adjust: exact` only on elements where a fill is essential, such as a chart.

## High contrast and forced colours

In forced-colour modes, the system replaces Gauge's palette. `components.css` maps selection, current navigation, pressed toggles, and focus to system colours (see [COMPONENTS.md](COMPONENTS.md#forced-colours)). Native apps should switch to the platform's high-contrast resources instead of Gauge tokens when high contrast is on.

## Known drift in Dump Debugger

Gauge was extracted from Dump Debugger. When this kit was written, a few of the app's surfaces did not use its own palette. Treat Gauge as the baseline, not these values.

| Surface in Dump Debugger | Current value | Gauge value |
| --- | --- | --- |
| `MainWindow.xaml` root and WebView2 default background | `#0b0d10` | dark `bg-0` `#1e1e1e` |
| Dark window background in `MainWindow.ApplyTheme` | `#0b0d10` | dark `bg-0` `#1e1e1e` |
| Dark caption buttons in `MainWindow.ApplyTheme` | `#8b93a1`, `#5e6673`, `#1e232b`, `#eef0f3`, `#262c35` (an earlier blue-grey palette) | `fg-2`, `fg-3`, `bg-3`, `fg-0`, `bg-4` |
| Exported report print styles in `web/src/report/report.ts` | `#1a1d22` text, `#f1f3f5` code, `#d0d5dc` borders, `#fff7e6` notes | light `fg-0`, `bg-2`, `line-2`, `warn-dim` |
| Control borders (`.titlebar .search`, `.seg`, ghost buttons) | `line-2` (1.71:1 on light `bg-0`) | `line-ctl` |
| Dark selected row edge | `transparent` (fill alone, 1.80:1 on `bg-0`) | `sel-edge` `#7cc0ff` |
