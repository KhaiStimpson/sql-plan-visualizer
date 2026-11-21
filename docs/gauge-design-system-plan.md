# Gauge design system — plan

Status: in progress · Branch: `claude/gauge-design-system` · Written: 2026-11-21

## Goal

The app looks and behaves like the approved mock-up (`docs/design/gauge-mockup.html`, screenshots
`docs/design/gauge-mockup-{main,empty,connect,run}-{dark,light}.png`) in both themes. That means
Gauge tokens drive every surface, the plan canvas and SQL editor, there is one left view rail, the
inspector shows dense finding rows, the dialogs are restyled, and there is a Ctrl+K command palette.
Done means a side-by-side of the running app and each mock-up screenshot shows no structural
difference.

## Ground rules

- **Build:** `dotnet build src/SqlPlanViz/SqlPlanViz.csproj`
- **Tests:** do not write tests; the build plus a manual run is the gate. This matches every
  other plan in `docs/` (no committed test project; personal single-user tool). Decided, not
  re-asked.
- **Execution:** one continuous session, top to bottom; no flow/loop tooling.
- **Branching:** one branch, `claude/gauge-design-system`, off `main`. One commit per task,
  directly on that branch. Merge to `main` only after the whole effort is reviewed. This matches
  the repo's flat `claude/*` convention, so there is no integration branch.
- **Sources of truth:** the mock-up HTML and its screenshots are the visual spec. Gauge's
  `docs/design/gauge/DESIGN.md`, `COMPONENTS.md` and `PLATFORMS.md` are the rules, and
  `tokens.json` holds the values. When the two disagree, the mock-up wins on layout and Gauge wins
  on colour, contrast and state treatment.
- **UI changes:** after each visible task, run
  `dotnet run --project src/SqlPlanViz -- samples/nested-loop-lookup-storm.sqlplan` and compare
  against the matching mock-up screenshot in **both** themes. Capture the window (1440×960) with
  `PrintWindow` (PW_RENDERFULLCONTENT = 2; a plain screen copy picks up whatever window is on top)
  into `docs/screenshots/gauge-design-system/<phase>-<task>-{dark,light}.png` and commit them with
  the change. This is a desktop-only app, so there are no mobile screenshots.
- **Out of scope:** new features or behaviour beyond what the mock-up shows. Replacing the Win2D
  editor or canvas (they stay native, per `docs/tdd.md`). Persisting the view mode across sessions.
  Localisation. A touch/tablet layout.
- **Already decided, do not re-litigate:**
  - Layout is the mock-up's. The left rail has Plan tree / Ranked by self time / Query Store
    history / Anti-pattern library, with the session plans list under it (moved from the bottom
    strip). Compare, Re-run and Baseline stay in the command bar (per
    `docs/command-strip-mode-rail-plan.md`, which this effort absorbs and supersedes).
  - Canvas controls live in the centre pane header: a Weigh-by `ComboBox` for 7 metrics (Gauge
    caps segmented controls at 5), plus Blame toggle, Detail cycle and a Tree/Flame segmented
    control.
  - Terse/Detailed is a segmented control in the Diagnosis header, not the command bar.
  - One accent-filled primary per view: **Re-plan** on the workbench, **Open a .sqlplan file**
    on the empty state, **Connect** / **Run and capture** in the dialogs. Open plan is secondary.
  - Severity is always a word (Critical / Warning / OK) plus colour, never colour alone. Node
    cards are neutral with a coloured left edge, not filled red.
  - Segmented controls are implemented as a `RadioButton` style (`GaugeSegmentItem`) inside a
    bordered horizontal panel, which gives exactly-one-selected for free. Do not restyle
    `SelectorBar` for this.
  - The Mica backdrop goes away. The root background is `bg-0` and the title bar is `bg-2` with a
    `line` border (PLATFORMS.md).
  - Theme preference is System / Light / Dark, stored in local settings and applied to the root
    element's `RequestedTheme` before the window is shown.
  - Win2D colours are read from the same token source (a generated `GaugeTokens.cs`), never
    hand-copied hex in `PlanPalette` / `SqlSyntaxTheme`.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — Tokens and theme foundation

Everything later references these resources, so they land first and change nothing structural.

- [x] Branch `claude/gauge-design-system` off `main`. Commit the mock-up (`docs/design/gauge-mockup*`)
      and vendor the Gauge kit (`tokens.json`, `DESIGN.md`, `COMPONENTS.md`, `PLATFORMS.md`,
      `reference.html`, `theme.css`, `components.css` from
      https://github.com/KhaiStimpson/gauge-design-kit) into `docs/design/gauge/`.
- [x] Add `Themes/GaugeTheme.xaml` with `ThemeDictionaries` (Default = dark, Light, HighContrast
      placeholder) holding a `Color` and a `SolidColorBrush` for every token role (`GaugeBg0Brush`
      … `GaugeTm9Brush`), plus fonts (`GaugeSans`, `GaugeMono`) and radii. Merge it in `App.xaml`.
- [x] Add `Themes/GaugeTokens.cs` with the same values as `static` `Color`s per theme, for Win2D.
      Add a header comment saying it mirrors `docs/design/gauge/tokens.json`. The values must
      match the XAML exactly.
- [x] Override the Fluent theme resources that drive stock controls (`TextFillColor*`,
      `ControlFillColor*`, `ControlStroke*`, `AccentFillColor*`, `CardBackground*`,
      `LayerFillColor*`, `SolidBackgroundFillColor*`, `SystemAccentColor*`) to Gauge roles, so
      untouched controls already read as Gauge.
- [x] Add `ThemeService` (System/Light/Dark in `ApplicationData.Current.LocalSettings`) and apply
      it before `MainWindow` activates. Remove Mica, set the root background to `bg-0`, and colour
      the title bar and caption buttons per PLATFORMS.md, updating on theme change.
- [x] Set the app font to Gauge sans at 13px and mono at 12px. Update `SectionHeader`,
      `FieldLabel`, `FieldValue`, `CodeText` and `CardBorder` in `App.xaml` to Gauge roles and the
      3–4px radii. Run the app and screenshot both themes.

## Phase 2 — Control vocabulary

The Gauge components as reusable XAML styles, so later phases only compose them.

- [x] Button styles: default secondary (`bg-3` / `line-2` / `fg-0`), `GaugePrimaryButton`
      (accent / accent-ink / 600), `GaugeGhostButton` (`line-ctl` border, no fill),
      `GaugeIconButton`, and a toggle style (`aria-pressed` equivalent: accent border + text when
      checked). 26px min height, 3×11 padding, `r-2`, with hover/pressed/disabled states per
      COMPONENTS.md.
- [x] Field styles for `TextBox`, `ComboBox`, `AutoSuggestBox`, `NumberBox`, `PasswordBox`,
      `CheckBox`: `bg-0` fill, `line-ctl` border, 26px, 3×8 padding, `fg-3` placeholder.
- [x] `GaugeSegmentItem` `RadioButton` style plus a `GaugeSegmented` border container: 22px,
      2×10 padding, 12px type, `line-ctl` outer border, `line-2` dividers, off `fg-2`, hover
      `bg-3`, on `bg-4` / `fg-0` / 600.
- [x] Text styles: `GaugeSevCrit` / `GaugeSevWarn` / `GaugeSevOk` / `GaugeSevNeutral` (12.5px 600),
      `GaugeEntityLink` (`HyperlinkButton` style, underlined, `fg-3` underline, accent on hover;
      mono and sans variants), `GaugeKbd`, `GaugeCount`.
- [x] Layout styles: `GaugePaneHeader` (8×14 padding, `line` bottom border, 14px 600 title, facts
      in 12.5px `fg-2`), `GaugeCallout` and `GaugeCalloutCrit`, `GaugeStatusBar` (24px, `bg-2`,
      11.5px `fg-2`), `GaugeNavItem` (30px, 0×9, `r-3`, current = `bg-4` / `fg-0` / 600).
- [x] Focus visuals: app-wide `FocusVisualPrimaryBrush` = accent, 2px, 2px margin. HighContrast
      theme dictionary maps selection, current nav, pressed segments and control borders to
      `SystemColorHighlight*` / `SystemColorButtonText`. Add `EditorScratchPage`-style gallery
      section (or reuse `EditorScratchPage`) showing every style, and screenshot it in both themes.

## Phase 3 — View rail and layout skeleton

The structural change from the mock-up: one view mode, a left rail, session plans in the rail.

- [x] Add a `ViewMode` enum (`Tree`, `Ranked`, `History`, `Library`) and an `[ObservableProperty]`
      on `MainViewModel`. Add `SetViewMode` in `MainPage.xaml.cs` as the single setter of
      `Canvas` / `OperatorListPane` / `QueryStorePane` / `AntiPatternPane` visibility. Route
      `OnToggleOperatorList`, `OnToggleQueryStore`, `OnToggleAntiPatterns`, the "Back to tree"
      buttons, `OnJumpToRankedOperator` and `OnOpenQueryStorePlan` through it.
- [x] Restructure the content grid into three columns (184px rail / centre / inspector
      `minmax(340, 29%)`) matching the mock-up. Rail background `bg-2` with a `line` right border.
- [x] Add the four rail items using `GaugeNavItem` with Segoe Fluent Icons glyphs,
      `ToolTipService.ToolTip` and `AutomationProperties.Name`; current item follows `ViewMode`.
      Show the ranked operator count with `GaugeCount`.
- [x] Move the session plans list from the bottom strip into the rail under a "Session plans"
      group label (name + time · fingerprint), current plan highlighted, `OnSelectSessionPlan`
      unchanged. Delete the bottom strip.
- [x] Remove the `OperatorListButton`, History and Library buttons from the command strip. Mark
      `docs/command-strip-mode-rail-plan.md` as superseded by this plan. Run, cycle every view,
      and screenshot.

## Phase 4 — Command bar, centre header, status bar

The chrome around the canvas, matching `gauge-mockup-main-*.png`.

- [x] Rebuild the custom title bar: 32px, `bg-2`, `line` border, app glyph in accent, name + file
      in `fg-2`, and a ghost "Find a command or operator Ctrl K" button (wired in Phase 9; disabled
      until then is fine).
- [x] Rebuild the command bar (`bg-2`, `line` border, 6×12 padding): Open plan (secondary),
      Paste icon, separator, connection cluster (`GaugeSevOk` "Connected" + mono
      `server · database` + auth in `fg-2` + ghost Disconnect, or "Not connected" + Connect),
      separator, ghost Compare / Re-run / Baseline (menu), then right-aligned search, playback,
      zoom −/+, fit and the collapse menu as `GaugeIconButton`s.
- [x] Add the centre pane header: "Statement N of M" title (keep the statement picker behaviour),
      facts, then a Weigh-by `ComboBox` replacing the metric `SelectorBar` (same 7 metrics, same
      `OnMetricChanged` effect), Blame toggle, Detail cycle button, and a Tree/Flame
      `GaugeSegmented` that replaces `FlameViewButton`.
- [x] Replace the footer with `GaugeStatusBar`: operator count, cost, elapsed, CPU and plan kind
      on the left, the canvas hint and a Theme `ComboBox` (System/Light/Dark → `ThemeService`)
      on the right.
- [x] Restyle the canvas focus breadcrumb, busy overlay and InfoBars to Gauge roles. Run and
      screenshot against `gauge-mockup-main-*.png`.

## Phase 5 — Plan canvas and flame view

Win2D drawing driven by `GaugeTokens`, matching the mock-up's node cards and edges.

- [x] Rebuild `PlanPalette` dark/light from `GaugeTokens`: surface `bg-0`, dotted grid in `line`,
      node fill `bg-1`, border `line-ctl`, text `fg-0`/`fg-2`, object line `code`, selection
      `sel` / `sel-fg` / `sel-edge`, crit/warn/ok, accent. Keep the `PlanPalette` API stable.
- [x] Node cards: neutral fill, 3px crit/warn left edge, severity word top-right, a mono object
      line, the finding headline line (Full detail), rows · estimate-off and cost % line, self-time
      line, and a 4px cost bar in accent on `bg-3`. Keep the LabelDetail density rules.
- [x] Metric heat and Blame mode: express heat through the cost bar and, for Blame, the severity
      edge and word, instead of fill colour. Estimate-skew keeps its diverging meaning through
      the edge colour plus the word "over" or "under". Delta recolouring uses `ok` / `crit`
      edges with "better" or "worse" words.
- [x] Selected node: `sel` fill, `sel-fg` text, `sel-crit` / `sel-warn` state words and a 2px
      `sel-edge` inset. Collapse handles: 16px `bg-3` circle with a `line-ctl` border.
- [x] Edges: neutral `line-ctl`, hot path in accent and thicker, dashed when the estimate is off
      by 10× or more, row labels in 11px mono `fg-2`. Minimap: `bg-2`, `line-2` border, nodes in
      `line-ctl`, viewport in accent. Add the legend chip (Hot path / Rows (width) /
      Estimate off ≥10×) bottom-left.
- [x] `FlameView`: bars on the `tm-1…tm-9` ramp with `tm-fg` labels, hot or selected frame in
      accent with a `sel-edge`, header and empty state as a `GaugePaneHeader` / callout. Run and
      screenshot both views in both themes.

## Phase 6 — Editor pane and parameters

The bottom pane, matching the mock-up's baseline bar, SQL header, code and parameter grid.

- [x] Map `SqlSyntaxTheme` / `SqlSyntaxPalette` to Gauge: keyword accent, identifiers `fg-0`,
      variables/parameters `code`, numbers `val-num`, strings `val-str`, comments `fg-3` italic,
      operators `fg-1`. Background `bg-0`, line numbers `fg-3`.
- [x] Editor chrome in `SqlEditorControl`: clause highlight `accent-dim` with a 2px accent left
      edge, change-gutter mark in `warn`, end-of-line annotations in 11.5px sans `fg-2`, bracket
      match outlined in `line-ctl`, caret `fg-0`, selection `sel`, find matches `warn-dim` with
      the current match in `accent-dim`. Restyle the find/replace overlay and `CompletionPopup`
      as Gauge menus (`bg-2`, `line-2`, active row = selected-record treatment).
- [ ] Splitter grip in `line-ctl` on a `bg-2` 7px band (accent on hover). Cost bar as a `bg-2`
      pane header: verdict text 600 (`ok` "Better" / `crit` "Worse" / neutral), facts in `fg-2`,
      ghost Pin current as baseline / Refresh schema and the Annotations toggle.
- [ ] SQL header as a `GaugePaneHeader`: "SQL", facts (likely clause + entity link to the operator
      + Ln/Col), ghost Format, Copy icon, secondary "Run for actual…", Cancel (when busy), and
      **Re-plan** as the workbench's single `GaugePrimaryButton` with a `Ctrl ↵` kbd.
- [ ] `ParameterStrip`: header row (chevron toggle, "Parameters" + count, note, ghost "Reset to
      plan values"), a Name / Type / Value / NULL grid with `fg-2` headers, mono `code` names,
      Gauge fields, right-aligned numeric values, and a TVP grid in the `GaugeTable` treatment.
      Drop the alternating tint. Run and screenshot.

## Phase 7 — Inspector

The right pane, matching the mock-up's Diagnosis column. Seven tasks; it does not split cleanly
because every task shares the inspector's header and list templates.

- [ ] Diagnosis `GaugePaneHeader`: title, `GaugeSevCrit` / `GaugeSevWarn` counts, Terse/Detailed
      `GaugeSegmented` (replaces `VerbosityButton`), copy-as-Markdown icon button.
- [ ] Narrative as a `GaugeCallout` with entity links for operator names, bold metrics and a bold
      "Next step:" lead.
- [ ] Replace the inspector `SelectorBar` with a `GaugeSegmented` (Findings N / Operator /
      Indexes N / Warnings N / Delta) driving the same panes.
- [ ] Findings as dense rows: severity word, title 600, impact right-aligned tabular, description,
      entity link to the node, confidence, triage state word on the right. Selected row uses the
      `sel` treatment with `sel-crit` / `sel-warn`. Remove the card and the Expander.
- [ ] Selected finding expands inline on `bg-0`: "Why it matters", "Fixes, most effective first",
      the approximate what-if box (`line-2` border), Show in plan / Open in library, the triage
      `GaugeSegmented` (Tried / No help / Fixed / Reset), and the "saved beside" note.
- [ ] Operator pane: `GaugePaneHeader` per section with key-value grids (labels `fg-2`, values
      `fg-0` right-aligned tabular), estimated vs actual rows, the CPU/IO bar in accent, mono
      predicates, Annotation and Live object context as plain collapsible sections.
- [ ] Indexes, Warnings and Delta panes: `GaugeTable` rows with severity words, mono DDL in a
      `bg-0` block with a Copy ghost button, and the Delta sort `ComboBox` in a pane header. Run
      and screenshot every tab in both themes.

## Phase 8 — Empty state, dialogs and secondary views

Matches `gauge-mockup-empty-*`, `-connect-*` and `-run-*`.

- [ ] Empty state: 18px "No plan loaded" heading, body copy, **Open a .sqlplan file** primary,
      Paste XML / Capture from server secondary, then "Or connect to a saved profile" as a
      `GaugeTable` (Profile entity link / mono server · database / authentication). The inspector
      shows its empty sentence. The editor pane is hidden.
- [ ] A global `ContentDialog` style: `bg-2`, `line-2` border, 6px radius, shadow, `scrim`
      overlay, header with a 15px title and a `fg-2` subtitle, footer on `bg-1` with a `line` top
      border. Primary uses `GaugePrimaryButton`.
- [ ] `ConnectView`: profile row (ComboBox + ghost Rename/Delete), Connection input and
      Authentication as `GaugeSegmented`, two-column Server/Database and Login/Password, the
      recent-server suggestion list as a Gauge menu, a checkbox row with ghost "Forget saved
      password", the InfoBar as a `GaugeCallout`, Save as profile, and a footer with
      Test connection + `GaugeSevOk` / `GaugeSevCrit` result + Cancel / **Connect**.
- [ ] `ConfirmRunDialog`: "Run for an actual plan?" with the server and login in mono, a
      `GaugeSevCrit` "N statements will modify rows" list (Ln + mono statement), the
      estimated-plan note with a `Ctrl ↵` kbd, and Cancel / **Run and capture**.
- [ ] Ranked operators, Query Store history and Anti-pattern library panes: `GaugePaneHeader`
      (drop "Back to tree", since the rail owns navigation) and `GaugeTable` lists with selected-row
      treatment. Library entries collapse as plain sections. Run and screenshot all four views.

## Phase 9 — Command palette and finish

- [ ] `CommandPalette` control: `scrim` overlay, 600px panel 70px from the top, 15px input,
      grouped list with 12px `fg-2` group labels, active option in the selected-record treatment.
      Ctrl+K toggles. Typing filters, Up/Down wrap, Enter runs, Escape or a scrim click closes,
      and focus returns to the opener.
- [ ] Command registry: Plan (open, paste, re-plan, run for actual, compare, re-run, pin baseline,
      save/check baseline), Connection (connect, disconnect, connect to each saved profile), View
      (four rail views, flame, focus hot path, collapse cheap / all, expand all, theme),
      Operators (every node in the current statement, which selects it). Wire the title bar button.
- [ ] Accessibility pass: tab order across rail, command bar, canvas, editor and inspector. A
      visible 2px accent focus everywhere. Windows contrast themes show selection, current nav
      and focus. Fix anything found.
- [ ] Replace `docs/app-overview.png`, `docs/operator-details.png` and `docs/index-suggestions.png`
      with fresh captures, and update README wording that names removed controls (bottom strip,
      toolbar History/Library).
- [ ] Final side-by-side: capture each mock-up state from the running app in both themes into
      `docs/screenshots/gauge-design-system/final-*`, list any remaining differences in the
      handoff, and fix or explicitly defer each one.

## Open questions

None blocking. The layout, the primaries, the segmented-control implementation and the token
pipeline were decided above against the approved mock-up.
