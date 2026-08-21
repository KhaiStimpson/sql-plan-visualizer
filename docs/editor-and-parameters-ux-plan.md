# Editor and parameters UX — plan

Status: not started · Branch: `claude/editor-and-parameters-ux` · Written: 2026-08-22

## Goal

The five items shortlisted from the editor/parameters brainstorm (`docs/research/` has no file for
this — see the artifact published earlier in-session) land, in order: parameter values get
type-shaped controls, the parameter type field becomes a pick instead of free text, the parameter
strip's spacing stops feeling cramped, the pane splitter grows a visible grip, and the native
editor gains comment-toggle, bracket-matching, and find/replace. Nothing here touches the plan
canvas, the completion engine, or replaces the Win2D editor — nothing here reaches for WebView2 or
Monaco.

## Ground rules

- **Build:** `dotnet build src/SqlPlanViz/SqlPlanViz.csproj`
- **Tests:** do not write tests; the build is the gate. This repo has no committed test project
  (`tests/SqlPlanViz.Tests` on disk carries only stale, untracked build output) and
  `docs/tdd.md` describes this as a personal, single-user tool. Decided once here, not re-asked.
- **Branching:** one branch, `claude/editor-and-parameters-ux`, off `main`. Merge back to `main`
  when the checklist is clear — this repo has no `integration/`/`dev` tier, just flat feature
  branches (see `git log --oneline`).
- **UI changes:** run the app (`dotnet run --project src/SqlPlanViz`) after each visible-change
  task and eyeball it against the mock-up before ticking the box. This repo has no
  `docs/screenshots/` convention and no CI screenshot pipeline — don't invent one for this effort.
- **Out of scope:** the docking-workspace and multi-cursor ideas from the brainstorm (both
  ambitious-tier, deliberately left for a later effort); cost-bar auto-collapse; persisting the
  splitter's height across sessions; table-valued-parameter grid editing.
- **Already decided, do not re-litigate:** the SQL editor stays native Win2D — no WebView2, no
  Monaco (`docs/tdd.md:97`, `docs/live-plan-editor-plan.md:26`). Every editor-engine task below
  extends `SqlDocument` / `TSqlTokenizer` / `SqlEditorControl`, it does not replace them.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — Type-shaped parameter value controls

`ParameterBindingItem.EditorKind` (`src/SqlPlanViz/ViewModels/ParameterBindingItem.cs:126-138`)
already classifies every scalar parameter as Text/Numeric/DateTime/Guid/Bit/Binary — the value
column in `ParameterStrip.xaml:100-108` just never reads it. This phase wires it up, one kind at a
time so each step stays reviewable.

- [x] Add a `DataTemplateSelector` keyed off `EditorKind` and wire it onto the `ItemsControl` at
      `ParameterStrip.xaml:67-131` in place of the single flat `DataTemplate`. Move the existing
      `TextBox` markup into the selector's default template (covers Text, Guid, Binary) so this
      step is a refactor with no behavior change.
- [x] Add the Numeric template: a `NumberBox` bound to `Value`, with a converter between
      `NumberBox.Value` (double) and the string `Value` property, since `ParameterBindingItem.Value`
      stays a string all the way to `SqlLiteral.Format` (`ParameterBindingItem.cs:225`).
- [x] Add the Bit template: a `ToggleSwitch` bound to `Value` through a bool↔string converter
      (`"1"`/`"0"`, matching what `SqlLiteral` already accepts for bit literals).
- [x] Add the DateTime template: a `CalendarDatePicker` plus a time-of-day text field, composing
      into the same `Value` string `SqlLiteral.Format` expects for datetime/datetime2.

## Phase 2 — Parameter type as a pick, not free text

`DataType` is a plain `TextBox` today (`ParameterStrip.xaml:93-98`), so a typo produces a
validation error `Validate()` (`ParameterBindingItem.cs:214-228`) only catches after the fact.

- [x] Add a small static list of common T-SQL types (int, bigint, nvarchar(n), varchar(n),
      datetime2, date, uniqueidentifier, bit, varbinary(n), decimal(p,s) …) and swap the `TextBox`
      at `ParameterStrip.xaml:93-98` for an editable `AutoSuggestBox`, still two-way bound to
      `DataType` so `OnDataTypeChanged` (`ParameterBindingItem.cs:239-243`) keeps recalculating
      `EditorKind` exactly as it does now. Typing a value the list doesn't have (e.g.
      `nvarchar(50)`) must still work — this replaces the input surface, not the validation.

## Phase 3 — Parameter row spacing and rhythm

- [x] Loosen `ParameterStrip.xaml:74-84`'s row: replace the flat `Margin="0,0,0,6"` with real
      `RowSpacing`/padding, align the NULL `CheckBox` to the value column instead of a bare Auto
      column, and give rows alternating tint (matches the mock-up's legend item on row rhythm).
- [x] Add narrow-width responsive stacking (name+type above value) via a `VisualStateManager`
      `AdaptiveTrigger` on the row template, so the strip doesn't squeeze three columns into one
      row at every width.

## Phase 4 — A splitter you can see

`PaneSplitter` (`src/SqlPlanViz/Controls/PaneSplitter.cs`) already drags and responds to
`Up`/`Down` keys — it just renders as a fully transparent 6px bar (`PaneSplitter.cs:29-33`), so
nothing signals it's there.

- [x] Give `PaneSplitter` a visible rest-state background (a thin centered grip, not the full
      bar) plus a distinct hover/drag brush, without touching any of its existing drag or keyboard
      logic.

## Phase 5 — Editor engine: comment toggle, bracket matching, find/replace

Ordered easiest-to-hardest; each later task can lean on the one before it.

- [x] Add a `Ctrl+/` line-comment toggle, shaped like `IndentSelection`
      (`SqlEditorControl.Input.cs:693-712`): operate line-by-line over the selection, prefix/strip
      `-- `, one undo group.
- [x] Add bracket/paren match highlighting, driven by the existing `TSqlTokenizer` token stream
      that already powers syntax coloring — no new parsing, just a highlight rule evaluated at
      caret-move time and drawn in `OnDraw`.
- [ ] Add a `Ctrl+F` find overlay: a small UI over the canvas, a search-in-`SqlDocument` helper,
      and a highlight-all-matches drawing pass.
- [ ] Extend the find overlay to a `Ctrl+H` replace mode, committing each replacement through the
      existing `ReplaceRange` path (`SqlEditorControl.Input.cs:553-563`) so it stays one undo step
      per replacement.

## Open questions

None blocking — the test-posture question was asked and answered before this plan was written
(no tests; build is the gate).
