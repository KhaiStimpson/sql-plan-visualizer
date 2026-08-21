# Live Plan Editor

**Status:** Plan v1 — no code written yet
**Author:** Khai
**Companion to:** [tdd.md](tdd.md) (the viewer) and [tuning-roadmap.md](tuning-roadmap.md) (the tuning
tool). This document defines the *editor*: the loop that lets you change the SQL and watch the plan
move.

## Premise

Today the SQL pane (`MainPage.xaml:527`) is a 190px read-only `TextBox`. It shows the statement the
loaded plan came from and highlights the clause a selected operator probably belongs to. The tuning
loop it supports is one-directional: read the plan, leave the app, edit the query, come back with a
new file.

This plan closes the loop. The pane becomes an editable, syntax-highlit, completion-driven T-SQL
editor with a parameters panel. `Ctrl+Enter` compiles the edited batch against the live connection
and swaps in the new plan. Every re-plan is diffed against a **pinned baseline**, and the result is
reported in four places at once: a headline cost bar, gutter marks on the lines that moved, inline
annotations naming what changed, and the plan canvas recolored by delta.

## Decisions (locked)

| Decision | Choice | Why |
|---|---|---|
| Editor host | Native WinUI, Win2D-rendered | No WebView2, no Monaco. Consistent with `PlanCanvas`; stays native and offline-capable |
| Tokenizer / parser | `Microsoft.SqlServer.TransactSql.ScriptDom` 180.78.1 | The parser SSMS and SqlPackage use. MIT, netstandard2.0. Real token stream, AST, and parameter discovery instead of hand-rolled regex |
| Re-plan trigger | Explicit `Ctrl+Enter` | Nothing reaches the server until asked. The cost bar marks itself stale when the text diverges |
| Plan source | Estimated by default, actual opt-in | `SET SHOWPLAN_XML ON` compiles without executing, so an edited `DELETE` is safe |
| Baseline | Pinned, defaults to the original | Deltas accumulate across many edits. "Pin current" re-anchors once an improvement is banked |
| Offline | Fully editable | Highlighting, keyword + plan-derived completions, and parameter prefill all work from a `.sqlplan` file. Only re-planning needs a server |
| Completions | Keywords + catalog + plan + tuning | Four ranked providers behind one engine, each degrading independently |
| Layout | Resizable bottom pane | A splitter replaces the fixed 190px strip. Plan and SQL stay visible together — otherwise gutter marks point at lines nobody can see |

## Current state (reviewed against the repo)

More of this exists than expected. The following is reuse, not invention:

| Existing | State | Role here |
|---|---|---|
| `Controls/PlanCanvas.cs` | `SetDiff(PlanDiffResult?)` already recolors nodes by added/removed/changed | Canvas delta highlighting is one wire-up |
| `Diagnostics/PlanDiff.cs` | Shape-matched operator pairing with cost and row deltas | The before/after primitive for the cost bar and gutter |
| `Diagnostics/PlanFingerprint.cs` | Metric-free shape hash | Detects "the edit changed plan shape" vs "same shape, different numbers" |
| `Parsing/ShowplanParser.cs` | Already parses `ParameterList` → name, `ParameterDataType`, compiled + runtime value | The parameter panel's prefill source, unchanged |
| `Capture/PlanCaptureService.cs` | Both `CaptureMode.Actual` and `EstimatedOnly` work | Needs SQL error *line numbers* surfaced, not flattened into a message string |
| `Diagnostics/SqlNodeMapper.cs` | Maps one operator → one clause span, by string search | Needs the inverse (deltas per line) and AST-backed offsets |
| `Capture/DatabaseContextService.cs` | Per-object catalog lookups | Completions need one bulk schema read, cached per connection |
| `ViewModels/MainViewModel.cs` | `SessionPlans`, `CompareLatestPlans`, `CurrentDiff` | Each re-plan lands in session history and stays inspectable |

New surface lives under a `SqlPlanViz.Editing` namespace. Follow the existing model convention:
**no `required`**, default-initialise instead (`= string.Empty`, `= []`, `= new()`).

---

## Phase 1 — Editor core

**Goal:** a real text editor that types, highlights, and undoes. No database, no completions. This is
the riskiest component, so it ships standalone and gets judged before anything depends on it.

Text input goes through `CoreTextEditContext` rather than raw key events, so IME composition, dead
keys, and touch keyboards work correctly. That single detail separates a real editor from a toy.

- [x] `Editing/SqlDocument.cs` — text buffer, line index, change events
- [x] `SqlDocument` undo/redo stack, coalescing consecutive typing into one unit
- [x] Add `Microsoft.SqlServer.TransactSql.ScriptDom` 180.78.1 to `SqlPlanViz.csproj`
- [x] `Editing/TSqlTokenizer.cs` — wrap `TSql160Parser.GetTokenStream`, classified spans with offsets
- [x] Incremental re-tokenize: only the dirty line range on each edit, not the whole document
- [x] `Editing/SqlSyntaxTheme.cs` — token class → brush, theme-aware, shaped like `PlanPalette`
- [x] `Controls/SqlEditorControl.cs` — Win2D `CanvasControl` host, `CanvasTextLayout` line rendering
- [x] Viewport virtualization: draw only visible lines (the plan canvas already sets this precedent)
- [x] Caret rendering, blink, and keyboard navigation (arrows, Home/End, Ctrl+arrows, page keys)
- [x] Selection: mouse drag, shift+navigation, double-click word, triple-click line
- [x] Clipboard: cut / copy / paste, plain text only
- [x] `CoreTextEditContext` wiring for IME and composition input
- [x] Line-number column and an empty gutter column reserved for Phase 5 marks
- [x] Vertical and horizontal scrolling, mouse wheel, and scroll-to-caret
- [x] `AutomationPeer` exposing the text pattern, so screen readers see a text control
- [x] Tab / Shift+Tab indent and outdent on a selection

**Deliverable:** a control you can type T-SQL into, with correct highlighting and working undo,
hosted in a scratch page. Not yet wired into `MainPage`.

**Goal command:**

```
Implement Phase 1 of docs/live-plan-editor-plan.md (Editor core). Work through the
checkboxes in order, tick each one in the file as it lands, and commit per logical
group. Do not start Phase 2. I will build and verify on Windows — flag anything you
could not compile-check.
```

---

## Phase 2 — Completions, offline sources first

**Goal:** `Ctrl+Space` and type-ahead completion that works with no connection at all.

Context comes from the AST, not from string matching: after `FROM` offer tables, after an alias and a
dot offer that alias's columns, inside a `SELECT` list offer columns from tables already in scope.

- [x] `Editing/Completion/CompletionItem.cs` — label, insert text, kind, detail, sort rank
- [x] `Editing/Completion/ICompletionProvider.cs` — provider contract, each independently disableable
- [x] `Editing/Completion/CompletionContext.cs` — caret position → enclosing clause, aliases in scope
- [x] `Editing/Completion/CompletionEngine.cs` — fan out to providers, merge, rank, filter
- [x] `Editing/Completion/KeywordProvider.cs` — T-SQL keywords, built-in functions, clause snippets
- [x] `Editing/Completion/PlanObjectProvider.cs` — tables, indexes, columns harvested from the loaded plan
- [x] `Controls/CompletionPopup.cs` — native WinUI `Popup` + `ListView`, positioned from the caret rect
- [x] Keyboard model: `Ctrl+Space` invoke, arrows navigate, Tab/Enter accept, Esc dismiss
- [x] Type-ahead filtering as characters arrive, with prefix matches ranked above substring matches
- [x] Dismiss correctly on caret move, selection change, focus loss, and document reload

**Deliverable:** completions from keywords and from the loaded plan's own objects, working on a
`.sqlplan` file opened with no server connection.

**Goal command:**

```
Implement Phase 2 of docs/live-plan-editor-plan.md (Completions, offline sources).
Phase 1 must be complete first. Tick checkboxes as they land. Catalog and tuning
providers are Phase 6 — do not build them yet.
```

---

## Phase 3 — Parameters, extracted and injected

**Goal:** never type `DECLARE` again. The editor works out which parameters the batch needs, offers
typed fields for them, prefills from the plan, and writes the declarations itself at capture time.

A ScriptDom visitor collects every `VariableReference` and subtracts those the batch already
declares; what remains is what the user must supply. Types are inferred from the plan's
`ParameterList` first, then from comparison context in the AST, then defaulted with the type left
editable.

- [ ] `Editing/SqlParameterExtractor.cs` — `TSqlFragmentVisitor` collecting `VariableReference`
- [ ] Subtract `DeclareVariableStatement` and procedure parameters already in the batch
- [ ] Type inference: plan `ParameterList` → AST comparison context → editable default
- [ ] `ViewModels/ParameterBindingItem.cs` — name, type, value, `IsNull`, validation state
- [ ] Prefill from `ParameterCompiledValue` / `ParameterRuntimeValue` (parser already reads both)
- [ ] Scalar type editors: numeric, string, date/time, `uniqueidentifier`, `bit`, binary
- [ ] `NULL` handling as an explicit per-parameter toggle, distinct from empty string
- [ ] Table-valued parameters: detect the user table type, render a row grid shaped by its columns
- [ ] `Editing/SqlBatchComposer.cs` — build the `DECLARE` prelude, prepend to user text
- [ ] TVP composition: `DECLARE @t AS dbo.Type` plus generated `INSERT` rows
- [ ] Offset map from composed batch back to editor lines (prelude length must not shift error lines)
- [ ] Literal escaping and quoting per type, so a value containing `'` cannot break the batch
- [ ] `Views/ParameterStrip.xaml` — the strip under the editor, collapsible when there are none

**Deliverable:** open a parameterised plan, see its parameters listed with values from the plan, edit
them, and get a correct composed batch — verified by inspection, since capture is Phase 4.

**Goal command:**

```
Implement Phase 3 of docs/live-plan-editor-plan.md (Parameters). Phases 1-2 must be
complete. Pay particular attention to the offset map and to literal escaping — those
two are where correctness bugs will hide. Tick checkboxes as they land.
```

---

## Phase 4 — Re-plan pipeline

**Goal:** `Ctrl+Enter` produces a new plan on the canvas.

- [ ] `ViewModels/SqlEditorViewModel.cs` — text, parameters, busy, error, staleness
- [ ] `MainViewModel.ReplanAsync` — compose batch, capture estimated, parse, activate
- [ ] Route each re-plan through the existing `SessionPlans` machinery so history is preserved
- [ ] Surface SQL error line and column from `SqlException` instead of flattening to a message
- [ ] Translate error positions through the Phase 3 offset map to editor lines
- [ ] Render compile errors as inline squiggles in the editor, plus a message on the status bar
- [ ] Keep the selected statement stable across re-plans by statement index plus fingerprint
- [ ] Disable editing while a capture is in flight; keep the request cancellable
- [ ] Stale tracking: mark the plan stale as soon as the text diverges from the captured batch
- [ ] `Ctrl+Enter` accelerator, plus a toolbar button with the same command

**Deliverable:** edit the SQL, press `Ctrl+Enter`, and the canvas shows the plan for the edited query.

**Goal command:**

```
Implement Phase 4 of docs/live-plan-editor-plan.md (Re-plan pipeline). Phases 1-3
must be complete. Estimated capture only — the actual run is Phase 7 and is
deliberately gated. Tick checkboxes as they land.
```

---

## Phase 5 — Better or worse

**Goal:** the payoff. Four feedback surfaces, cheapest and most reliable first.

The headline cost bar needs no text mapping at all — just `PlanDiff` against the pinned baseline — so
it lands before anything that depends on inference.

- [ ] `Diagnostics/TuningSession.cs` — pinned baseline, current plan, diff between them
- [ ] Auto-pin the plan the session started from; "Pin current as baseline" re-anchors
- [ ] Cost delta bar above the editor: baseline cost → current, percent change, direction
- [ ] Name the shape changes in the bar ("Key Lookup added", "Index Seek → Index Scan")
- [ ] Label the unit as *estimated* cost, and mark when the plan is estimated-only
- [ ] Stale state in the bar when the text has changed since the last capture
- [ ] Wire `Canvas.SetDiff(TuningSession.Diff)` — canvas node highlighting, mostly free
- [ ] Rewrite `SqlNodeMapper` spans on ScriptDom AST offsets instead of string search
- [ ] `Diagnostics/SqlDeltaMapper.cs` — fold node deltas into per-line `LineImpact` records
- [ ] Confidence threshold on `LineImpact`; below it, render nothing rather than a wrong arrow
- [ ] Gutter marks: improved / regressed / added, drawn in the Phase 1 gutter column
- [ ] Inline end-of-line annotations naming the delta and its cause
- [ ] Toggle for inline annotations, since they are the noisiest of the four surfaces
- [ ] Click a gutter mark → select the responsible operator on the canvas
- [ ] `MainPage.xaml` — splitter, resizable editor pane, collapse/restore, replacing the 190px strip

**Deliverable:** an edit that adds a Key Lookup shows red in the bar, red in the gutter on the
responsible line, and a recolored node on the canvas — all against the pinned baseline.

**Goal command:**

```
Implement Phase 5 of docs/live-plan-editor-plan.md (Better or worse). Phases 1-4
must be complete. Build the cost bar first — it needs no text mapping and proves the
baseline plumbing before the inference-dependent surfaces go in. Where mapping
confidence is low, render nothing. Tick checkboxes as they land.
```

---

## Phase 6 — Catalog and tuning-aware completions

**Goal:** completions that know the real schema, and completions that know what is wrong with the
plan.

- [ ] `Capture/CatalogMetadataService.cs` — one bulk read on connect, cached per session
- [ ] Read `sys.schemas`, `sys.tables`, `sys.views`, `sys.columns`, `sys.indexes`, `sys.table_types`
- [ ] Manual refresh command, since schemas change under a long-lived session
- [ ] `Editing/Completion/CatalogProvider.cs` — schema-qualified objects, alias-aware columns
- [ ] Detail text on catalog items: data type, nullability, index membership
- [ ] Feed `sys.table_types` columns into the Phase 3 TVP row grid
- [ ] `Editing/Completion/TuningProvider.cs` — suggestions drawn from the diagnostics layer
- [ ] Offer covering-index columns from an active missing-index finding
- [ ] Offer a SARGable rewrite of the predicate under the caret (`non-sargable-predicate` rule)
- [ ] Offer an explicit column list to replace `SELECT *`
- [ ] Rank tuning suggestions above generic matches, and mark them visually as suggestions

**Deliverable:** connected, the editor completes real tables and columns; with a missing-index
finding active, it offers the index's columns where they belong.

**Goal command:**

```
Implement Phase 6 of docs/live-plan-editor-plan.md (Catalog and tuning completions).
Phases 1-5 must be complete. The catalog read must be one round trip and cached —
do not query per keystroke. Tick checkboxes as they land.
```

---

## Phase 7 — Opt-in actual run

**Goal:** real row counts, behind a guard that makes the consequence unmissable.

The mistake this exists to prevent is running a dev query against production.

- [ ] `Editing/BatchSafetyAnalyzer.cs` — classify statements via ScriptDom
- [ ] Detect `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `TRUNCATE`, and all DDL
- [ ] `Views/ConfirmRunDialog.xaml` — names every modifying statement it found
- [ ] State the connected server and database prominently in that dialog
- [ ] Require a deliberate second click; never make running the default action
- [ ] `MainViewModel.RunActualAsync` — `CaptureMode.Actual`, reusing the Phase 4 pipeline
- [ ] Enable the runtime-only metrics (rows, elapsed, self time, efficiency, skew) after an actual run
- [ ] Cost bar switches to actual measurements and says so, replacing the estimated-cost caveat

**Deliverable:** a guarded "Run for actual plan" action that produces runtime row counts, with a
confirmation no one clicks through by accident.

**Goal command:**

```
Implement Phase 7 of docs/live-plan-editor-plan.md (Opt-in actual run). Phases 1-6
must be complete. The guard is the point of this phase — err toward too much
friction, not too little. Tick checkboxes as they land.
```

---

## Documentation to update alongside the code

- [ ] `docs/tuning-roadmap.md` — add this as Phase 10; add the `Editing/` layer to its state table
- [ ] `docs/tdd.md` §5 — add ScriptDom to the tech-stack table
- [ ] `docs/tdd.md` §6 — add the editor as a third plan-capture path beside file import and live capture
- [ ] `README.md` — the SQL pane no longer only "maps a selected operator back to the SQL clause it most likely represents"; that stops being true at Phase 5

---

## Risks

**The editor is the whole project.** A native code editor means implementing text input, IME
composition, selection, undo, clipboard, accessibility, and scrolling by hand. This is where the
schedule goes if it goes anywhere. *Mitigation:* `CoreTextEditContext` for input rather than key
handling, `AutomationPeer` for screen readers, and Phase 1 delivered standalone so it can be judged
before anything depends on it.

**Operator-to-line mapping is inference.** Showplan carries no source offsets. Even with an AST,
attributing a Key Lookup to line 4 rather than line 3 is a heuristic, and a confidently wrong gutter
arrow is worse than no arrow. *Mitigation:* the cost bar, which needs no mapping, is the primary
signal; marks render only above a confidence threshold; copy says "likely".

**Estimated costs are not time.** A drop in estimated subtree cost is the optimizer's opinion, not a
measured improvement. *Mitigation:* the bar labels its unit as estimated cost and marks the plan as
estimated; Phase 7 is the path to real numbers.

**Parser version versus server version.** ScriptDom parses to a fixed T-SQL version. `TSql160Parser`
rejects syntax newer than SQL Server 2022 and accepts syntax an older target server will not.
*Mitigation:* pick the parser from `@@VERSION` on connect, fall back to the newest, and treat parse
failures as non-fatal — highlighting degrades, the batch still gets sent.

**Re-planning hits a real server.** Even estimated plans consume compile time. *Mitigation:*
`Ctrl+Enter` is explicit and never automatic; captures are cancellable; the existing
`ConnectionSettings.CommandTimeoutSeconds` applies unchanged.

**Nothing here can be built on Linux.** This is a Windows WinUI 3 app. Code can be written in a
remote session but not compiled, run, or screenshotted there. *Mitigation:* verification happens on
Windows; keep phases small so the first-build fix-up stays cheap.
