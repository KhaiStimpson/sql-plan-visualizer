# Command strip mode rail — plan

Status: not started · Branch: `claude/command-strip-mode-rail` · Written: 2026-08-22

## Goal

Variant 3 from the command-strip brainstorm (artifact `Command Strip Triage`, published
2026-08-22): the plan canvas, ranked-operator list, Query Store history, and anti-pattern library
are already four mutually-exclusive `Grid.Row="0"` siblings inside `LeftContent`
(`MainPage.xaml:266-445`), each flipped on by its own handler in `MainPage.xaml.cs`
(`OnToggleOperatorList`, `OnToggleQueryStore`, `OnToggleAntiPatterns`, lines 712-768) that
independently sets `Canvas.Visibility` and its own pane's `Visibility`. Nothing coordinates them —
opening History while Ranked is already open does not close Ranked. This plan turns those three
scattered toolbar buttons plus the implicit tree view into one `ViewMode` enum with a single left
icon rail, so there is exactly one active view at a time by construction, not by convention.

**Correction against the brainstorm mockup:** Compare, Re-run, and Baseline are *not* part of this
rail. Re-reading `MainViewModel.CompareLatestPlans()` (line 585) shows they compute a `PlanDiff`
that feeds the existing "Delta" tab in the right-hand detail panel and recolor the canvas — they
never touch the four `Grid.Row="0"` panes. They stay exactly where they are in the top strip.
Likewise Playback and Blame modify how the *current* view renders rather than switching what's
shown, so they also stay in the top strip.

## Ground rules

- **Build:** `dotnet build src/SqlPlanViz/SqlPlanViz.csproj`
- **Tests:** do not write tests; the build plus a manual run is the gate. This repo has no
  committed test project (`tests/SqlPlanViz.Tests` on disk carries only stale, untracked build
  output) and `docs/tdd.md` describes this as a personal, single-user tool. Decided once here,
  matching `docs/editor-and-parameters-ux-plan.md` — not re-asked.
- **Branching:** new branch `claude/command-strip-mode-rail` off `main`. The current branch
  (`claude/live-plan-editor-impl-svla9m`) has unrelated, uncommitted work on
  `SqlEditorControl.cs` — do not build this effort on top of it or touch that file.
- **UI changes:** run the app (`dotnet run --project src/SqlPlanViz`) after each visible-change
  task. Specifically verify: each rail icon shows exactly one pane and hides the other three;
  the "Back to tree" buttons already inside `OperatorListPane`, `QueryStorePane`, and
  `AntiPatternPane` still return to Tree; `OnJumpToRankedOperator` and `OnOpenQueryStorePlan`
  still land back on the canvas with the right node selected.
- **Rail placement (decided, not re-litigated):** the rail is a narrow (~46px) column inside
  `LeftContent`, spanning the same `Grid.Row="0"` as `Canvas` and the three overlay panes, to
  their left. It does not extend into the SQL editor pane below (`Grid.Row="2"`) or the detail
  pane in the outer content `Grid` (`Grid.Column="1"`).
- **Out of scope:** variant 1's top-strip grouping/overflow menu and variant 2's two-row split
  (separate efforts, not prerequisites); reorganizing Compare/Re-run/Baseline/Playback/Blame —
  see the correction above, they are not becoming rail entries; persisting the selected view mode
  across sessions.
- **Already decided, do not re-litigate:** the rail models exactly four states — Tree, Ranked,
  History, Library — matching the four existing `Grid.Row="0"` siblings. No fifth state is being
  invented in this effort.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — One `ViewMode`, one setter

**Goal:** fix the underlying coordination bug before any new UI exists. The four panes are driven
by one piece of state, wired to the *existing* toolbar buttons, so this phase is provably safe to
review on its own.

- [ ] Add a `ViewMode` enum (`Tree`, `Ranked`, `History`, `Library`) and a `[ObservableProperty]
  ViewMode` on `MainViewModel`, defaulting to `Tree`.
- [ ] Add `SetViewMode(ViewMode mode)` in `MainPage.xaml.cs` that sets `Canvas.Visibility`,
  `OperatorListPane.Visibility`, `QueryStorePane.Visibility`, and `AntiPatternPane.Visibility`
  from a single switch, and sets `ViewModel.ViewMode`. Replace the bodies of
  `OnToggleOperatorList`, `OnToggleQueryStore`, and `OnToggleAntiPatterns` to call it (toggling
  between the relevant mode and `Tree`), keeping their existing `Click` wiring on the current
  buttons for now — no visual change yet.
- [ ] Route the "Back to tree" buttons inside the three panes, `OnJumpToRankedOperator`, and
  `OnOpenQueryStorePlan` through `SetViewMode(ViewMode.Tree)` instead of setting `Visibility`
  directly.
- [ ] Run the app: open a plan, cycle Ranked → History → Library → Ranked from the existing
  buttons, confirm only one pane is ever visible and the canvas reappears correctly each time.

## Phase 2 — The rail

**Goal:** replace the three toolbar buttons with the left icon rail; four icons, one active at a
time, driven by the `ViewMode` from Phase 1.

- [ ] Add the rail as a new `Grid.Column="0"` inside `LeftContent`'s `Grid.Row="0"` (shifting
  `Canvas` and the three panes to `Grid.Column="1"`): four `ToggleButton`s (Tree/Ranked/
  History/Library) in a vertical `StackPanel`, each `Click` calling `SetViewMode`.
- [ ] Remove `OperatorListButton` and the top-strip `History` and `Library` buttons from the
  command strip (`MainPage.xaml`, the `Grid.Column="3"` `StackPanel`) now that the rail owns
  those three.
- [ ] Style the active rail button's checked state (accent background, matching the existing
  `AccentButtonStyle`/`AccentFillColorSecondaryBrush` usage elsewhere in this file) so the
  current view is visually obvious without reading labels.
- [ ] Run the app: confirm the rail is the only way to reach Ranked/History/Library, the removed
  toolbar buttons are gone, and the top strip is visibly shorter.

## Phase 3 — Parity pass

**Goal:** the rail should not read like a placeholder — real icons, tooltips, and accessibility
matching the rest of the toolbar's conventions.

- [ ] Replace the four rail buttons' content with `Segoe Fluent Icons` `FontIcon` glyphs (matching
  the existing icon-button pattern used throughout `MainPage.xaml`, e.g. the zoom buttons), not
  text or emoji: Tree, ranked list (reuse `OperatorListButton`'s old glyph `&#xEA37;`), history,
  and library glyphs.
- [ ] Add `ToolTipService.ToolTip` to each rail button ("Plan tree", "Ranked by self time",
  "Query Store history", "Anti-pattern library"), matching every other icon-only button in this
  file.
- [ ] Add `AutomationProperties.Name` to each rail button so the mode switch is screen-reader
  legible, matching `SqlEditorControlAutomationPeer`'s existing accessibility conventions
  elsewhere in the app.
- [ ] Run the app: tab through the rail with the keyboard, confirm focus order and visible focus
  rings, confirm each tooltip reads correctly.

## Open questions

None blocking — rail placement and scope were decided above rather than left open, since both
were answerable from the existing layout.
