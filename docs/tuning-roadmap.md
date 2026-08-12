# Tuning Intelligence Roadmap

**Status:** Plan v1
**Author:** Khai
**Companion to:** [tdd.md](tdd.md) — that document defines the *viewer*; this one defines the
*tuning tool* built on top of it.

## Premise

Today the app surfaces plan **data** (cost, rows, warnings) and leaves the inference to the
user. Every phase below moves work from the user's head into the app: from "here is your
plan" to "here are your three problems, here is why, here is what to change."

The organising idea is a **derived diagnostics layer**. `ShowplanParser` stays a pure
XML→model mapper. A new `Diagnostics/` layer takes a `PlanStatement` and returns findings.
Rules are individually addable, individually disableable, and independently exercisable
against the sample plans in `samples/`.

## Current state (reviewed against the repo, base app complete)

| Layer | State |
|---|---|
| `Model/PlanModel.cs` | Solid. Warnings, `EstimateErrorFactor`, `EstimatedOperatorCost`, runtime stats, `Max*` aggregates |
| `Parsing/ShowplanParser.cs` | RelOps, warnings, missing indexes, runtime counters, predicates, output lists |
| `Layout/PlanLayoutEngine.cs` | Tree layout, hit testing, collapse support |
| `Controls/PlanCanvas.cs` | Win2D canvas: heat, edges, pan/zoom, filter, collapse, selection, `BringIntoView`, zoom keybinds |
| `Controls/PlanPalette.cs` | Theme-aware heat ramp, `SizeMetric` enum (4 values) |
| `Capture/PlanCaptureService.cs` | Live capture, **both** `CaptureMode.Actual` and `CaptureMode.Estimated` |
| `ViewModels/MainViewModel.cs` | Plan/statement/node selection, metric fallback, `Warnings` + `MissingIndexes` collections |
| `ViewModels/NodeDetail.cs` | Per-node detail projection, `WarningItem` |
| `App.xaml`, `MainWindow.cs`, `MainPage.xaml` | **Shell complete.** Command strip, canvas host, 3-tab detail pane, status bar |
| `Views/ConnectView.xaml` | Connection dialog |
| `samples/orders-actual.sqlplan` | The sample plan to develop rules against |

**The shell is built**, so no phase below is blocked on it — the earlier gating note in this
plan is obsolete and has been removed. Phases 1–3 remain pure C# with no UI surface; phases
4 onward extend concrete, existing UI rather than inventing it.

Note also that the TDD says WPF; the code is WinUI 3 + Win2D. This plan follows the code.

### Things the review changed

- **Phase 8.2 is already done.** `CaptureMode.Estimated` / `SET SHOWPLAN_XML ON` shipped with
  the base app. Struck from the checklist.
- `tests/SqlPlanViz.Tests/Samples` is empty; the real sample lives at
  `samples/orders-actual.sqlplan`. Rules get developed against that.
- The model **dropped `required`** in favour of default-initialised properties
  (`= string.Empty`, `= new()`, `= []`). New model types must follow that convention.
- Detail-pane tabs are index-switched in `MainPage.xaml.cs` (`OnPaneChanged`), so adding a
  tab means updating those indices, not just the XAML.
- `PlanCanvas.OnKeyDown` already handles zoom keys; Phase 6 extends it rather than starting
  fresh.
- `MainViewModel.OnSelectedStatementChanged` contains a runtime-metric fallback guard that
  every new `SizeMetric` value must be added to, or an estimated plan will render a flat
  canvas.

---

## Phase 1 — Parser enrichment

**Goal:** capture the Showplan data the diagnostic rules need. No UI, no behaviour change —
model and parser only. Doing this first means every later rule has its evidence available.

**New model types** (`Model/PlanModel.cs`, or a sibling `Model/PlanDiagnosticsModel.cs` if
the file gets unwieldy):

```
MemoryGrantInfo
  SerialRequiredMemoryKb, SerialDesiredMemoryKb   double
  RequestedMemoryKb, GrantedMemoryKb, MaxUsedMemoryKb   double?
  GrantWaitTimeMs   double?
  UsedFraction => MaxUsedMemoryKb / GrantedMemoryKb

WaitStat
  WaitType   string
  WaitTimeMs double
  WaitCount  long

ThreadRuntime                      // one per thread, per operator
  Thread      int
  ActualRows  double
  ElapsedMs   double?
  CpuMs       double?

ParameterInfo
  Name             string
  DataType         string?
  CompiledValue    string?         // value the plan was optimised for
  RuntimeValue     string?         // value it actually ran with
  Sniffed => CompiledValue != RuntimeValue

StatisticsUsage
  Database, Schema, Table, StatisticsName   string
  SamplingPercent            double?
  LastUpdate                 DateTime?
  ModificationCount          long?

CompileInfo
  CompileTimeMs, CompileCpuMs, CompileMemoryKb   double?
  EarlyAbortReason      string?   // "TimeOut" | "MemoryLimitExceeded" | "GoodEnoughPlanFound"
  CardinalityEstimationModelVersion   string?
  TraceFlags            string[]
  SetOptions            IReadOnlyDictionary<string,string>
```

**Wiring:**
- `PlanNode` gains `MemoryGrant` (on the grant-owning operator), `PerThread` (`IReadOnlyList<ThreadRuntime>`).
- `PlanNode.HasThreadSkew` — computed: max thread rows ÷ mean thread rows, `> 2` with more than one thread.
- `PlanSummary` gains `MemoryGrant`, `Waits`, `Parameters`, `StatisticsUsed`, `Compile`.
- `ShowplanParser` gets matching `ParseMemoryGrant`, `ParseWaitStats`, `ParsePerThread`,
  `ParseParameters`, `ParseStatisticsUsage`, `ParseCompileInfo` methods, following the
  existing `XName.LocalName`-walking convention so namespace-prefixed plans parse identically.
- All new fields nullable — estimated-only plans and older SQL Server versions omit most
  of them, and a missing element must never throw.
- Follow the current model convention: **no `required`**, default-initialise instead
  (`= string.Empty`, `= []`, `= new()`).

**Also in this phase:** keep the existing per-thread aggregation behaviour
(`ActualRows` summed, `ActualElapsedMs` maxed) so nothing downstream shifts; `PerThread` is
purely additive.

**Deliverable:** parser fills every field above from a Showplan XML with runtime stats.

---

## Phase 2 — Diagnostics engine + core rules

**Goal:** the centrepiece. Turn plan shapes into named, explained, fixable findings.

**New namespace:** `SqlPlanViz.Diagnostics`

```
Diagnostics/
  PlanFinding.cs        // the finding record + Fix + enums
  IPlanRule.cs          // one rule
  RuleEngine.cs         // runs all rules over a statement, ranks the output
  Rules/
    KeyLookupRule.cs
    EstimateBlowupOriginRule.cs
    ResidualPredicateScanRule.cs
    ImplicitConversionRule.cs
    SpillRule.cs
    NonSargablePredicateRule.cs
```

**Core contracts:**

```csharp
public sealed record PlanFinding
{
    public required string RuleId { get; init; }          // "key-lookup-storm"
    public required string Title { get; init; }           // templated with real numbers
    public required FindingSeverity Severity { get; init; }   // Critical | Warning | Info
    public required FindingConfidence Confidence { get; init; } // High | Likely | Possible
    public required IReadOnlyList<PlanNode> Nodes { get; init; } // may span several
    public required string Why { get; init; }             // the explanation
    public IReadOnlyList<Fix> Fixes { get; init; } = [];
    public double ImpactFraction { get; init; }           // 0..1 of total plan cost/time
}

public sealed record Fix(string Summary, string? Snippet, string? Caveat, FixKind Kind);
// FixKind: Index | Rewrite | Statistics | Hint | Configuration | Investigate

public interface IPlanRule
{
    string RuleId { get; }
    IEnumerable<PlanFinding> Analyse(PlanStatement statement);
}

public sealed class RuleEngine
{
    public RuleEngine(IEnumerable<IPlanRule>? rules = null);   // defaults to all built-ins
    public IReadOnlyList<PlanFinding> Analyse(PlanStatement statement);
}
```

`RuleEngine.Analyse` ranks by `Severity`, then `ImpactFraction`, then `Confidence`. A rule
that throws is swallowed and skipped — one bad rule must never break plan viewing.

`PlanStatement` gains a lazily-computed `Findings` property mirroring the existing
`AllNodes` caching pattern, so the UI can bind without orchestrating the engine.

**The six core rules:**

1. **`estimate-blowup-origin`** — *the highest-value rule.* Walk the tree and find the
   **deepest** node where `EstimateErrorFactor` first crosses 10x (i.e. the error is not
   inherited from a child). Everything above it is collateral damage. Title: *"Row estimate
   first goes wrong here — 40x under at the `IX_OrderDate` seek."* No mainstream tool does
   this well; it is the differentiator.
2. **`key-lookup-storm`** — Key Lookup / RID Lookup with high `ActualExecutions` under a
   Nested Loop. Fix: generated `CREATE INDEX … INCLUDE (…)` built from the lookup's
   `OutputList` (already parsed) plus the seek's key columns.
3. **`residual-predicate-scan`** — Scan or Seek where `ActualRows` greatly exceeds the rows
   leaving the parent filter. Report the selectivity ratio. Fix: index on the predicate columns.
4. **`implicit-conversion`** — `PlanAffectingConvert` warning; extract the column and both
   types from the warning detail. Fix: match the parameter type to the column, or change
   the column. Caveat: changing a column type is a schema migration.
5. **`spill-to-tempdb`** — sort/hash/exchange spill warnings, cross-referenced with
   `MemoryGrantInfo` from Phase 1. Report granted vs. used. Fix: update statistics (a spill
   is usually an underestimate downstream), or a grant hint as a last resort.
6. **`non-sargable-predicate`** — regex the `Predicate`/`SeekPredicate` text for a function
   wrapping a column: `YEAR(x)`, `LEFT(x,…)`, `CONVERT(…, x)`, `ISNULL(x,…)`, `x + ''`,
   leading-wildcard `LIKE '%…'`. Fix: the rewritten range predicate, shown before/after.

**Explanation text lives with the rule**, templated with that node's real numbers — never a
generic blurb. "Nested Loops runs the inner side once per outer row. Here that is 84,320
executions of a Key Lookup, which is 71% of this query."

**Deliverable:** `new RuleEngine().Analyse(statement)` returns ranked findings for a plan.

---

## Phase 3 — Extended rules

**Goal:** exploit the Phase 1 data. Same shape as Phase 2, no new infrastructure.

7. **`parameter-sniffing`** — `ParameterInfo.Sniffed` is true **and** the plan has a bad
   estimate. This converts "maybe it's sniffing" into evidence. Fix: `OPTIMIZE FOR`,
   `RECOMPILE`, or local-variable rewrite — each with its trade-off stated.
8. **`parallelism-skew`** — `PlanNode.HasThreadSkew`. Often the true cause of "sometimes
   it's slow" and invisible in every aggregate view.
9. **`stale-statistics`** — `StatisticsUsage.ModificationCount` high relative to table size,
   or `ColumnsWithNoStatistics`, combined with estimate error. Fix: `UPDATE STATISTICS` with
   the exact object name.
10. **`optimizer-gave-up`** — `CompileInfo.EarlyAbortReason` is `TimeOut` or
    `MemoryLimitExceeded`. The plan you are staring at was never fully considered. Pure
    context, but context nobody surfaces.
11. **`fat-inner-side-loop`** — Nested Loop where inner subtree cost × executions is large.
    Fix: the index that makes the inner seek cheap; hash-join hint as a fallback.
12. **`spool-trap`** — Table/Index Spool with high rebinds. Usually Halloween protection or
    an ORM-generated correlated subquery.
13. **`scalar-udf`** — UDF operator present, or a TVF with the giveaway fixed 100/1 row
    estimate. Fix: inline the function, or upgrade to a version with scalar UDF inlining.
14. **`wait-dominated`** — `WaitStat` total exceeds a large share of elapsed time. Title:
    *"This is not a plan problem — 3.1s of 4.2s was spent waiting on `LCK_M_S`."* Prevents
    tuning the wrong thing entirely.
15. **`wide-update`** — many index-update operators under one DML. List which indexes are
    costing writes.
16. **`missing-index-merge`** — dedupe and merge overlapping Showplan suggestions across the
    batch: `(A) INCLUDE (B)` + `(A) INCLUDE (C)` → one index. Always attach the write-cost
    caveat and the index width; never present DMV suggestions as unqualified advice.

**Deliverable:** sixteen rules, engine-registered, disableable individually.

---

## Phase 4 — Coloring: make heat mean something specific

**Goal:** the canvas stops being a picture of a plan and becomes a picture of the problems.

**Extend `SizeMetric`** (`Controls/PlanPalette.cs`, `PlanCanvas.MetricValue`/`MetricMax`):
- `EstimateSkew` — **diverging** ramp centred at 1.0: blue = overestimate, red =
  underestimate, neutral grey in the middle. Direction matters (overestimates waste memory
  grants; underestimates cause spills and loop joins), so one unidirectional ramp cannot say
  both. Needs a new `PlanPalette.Diverging(double signedFraction)` alongside `Heat`.
- `Efficiency` — `ActualRows` at this node ÷ rows the query finally returned. Finds "read
  2M rows to give you 40" instantly.
- `SelfTime` — `ActualElapsedMs` minus max child elapsed. The real "what is slow", and
  almost always a different node than subtree cost suggests. Add as a computed property on
  `PlanNode` and a `PlanStatement.MaxSelfTimeMs`.

**Blame overlay** — a new mode orthogonal to `SizeMetric`, e.g. `PlanCanvas.ColorMode`
(`Metric` | `Blame`). In `Blame`, node color comes from the worst finding touching that node
(red halo Critical, amber Warning), and every unimplicated node desaturates toward the
surface color. `PlanPalette` gains `FindingAccent(FindingSeverity)`. **Make `Blame` the
default** once findings exist; the metric heat map becomes the toggle.

**Edge encoding beyond thickness:**
- Dashed edge where the estimate through it was wrong by more than 10x — the *flow* shows
  the bad guess, not just the node.
- Distinct color for edges crossing a parallelism boundary.

**Color-blind safety** — heat by color alone fails for roughly 8% of men, and also dies in
greyscale screenshots. Pair every heat value with a redundant channel: vary the left accent
strip's **width** with the same fraction, and add a small severity glyph on flagged nodes.
Cheap at `DrawingContext` level.

**Wiring the new metrics through the existing UI** — three touch points, all of which already
exist and will silently misbehave if skipped:
1. `PlanCanvas.MetricMax` / `MetricValue` switch expressions — add the new cases.
2. `PlanCanvas` legend text (the `SizeMetric.ElapsedTime`/`ActualRows`/`OperatorCost` switch
   near the draw code) — add labels.
3. `MainViewModel.OnSelectedStatementChanged` — its guard currently falls back to
   `SubtreeCost` when `!RuntimeMetricsAvailable && Metric is ActualRows or ElapsedTime`.
   `Efficiency`, `SelfTime` and `EstimateSkew` all need runtime data too, so add them to
   that condition or an estimated plan will render a uniformly-coloured canvas.
4. The `MetricSelector` in `MainPage.xaml` — add items alongside `RowsMetricItem` /
   `TimeMetricItem`, matching their enable/disable binding to `RuntimeMetricsAvailable`.

**Deliverable:** three new metrics, a blame overlay, richer edges, redundant encoding.

---

## Phase 5 — Explanation layer

**Goal:** teach while diagnosing. The app should produce text worth pasting into a PR.

- **Operator explainer cards** — hover any node for 2–3 sentences on what that operator
  does, phrased around *this* node's numbers. A `Diagnostics/OperatorGlossary.cs` keyed by
  `PhysicalOp`, with a template hole for the live figures.
- **Narrated plan summary** — a generated paragraph at the top of the detail panel, built
  from the ranked findings: *"This query takes 4.2s. 89% is one Key Lookup on
  `Orders.IX_CustomerId` executed 84k times, caused by an underestimate at the `OrderDate`
  seek. The index below should remove it."* New `ViewModels/PlanNarrative.cs`.
- **Findings panel** — a **fourth tab in the existing detail-pane `SelectorBar`**, alongside
  `Operator` / `Indexes` / `Warnings`. Mirror the `WarningsPane` pattern exactly: a
  `Findings` `ObservableCollection<PlanFinding>` on `MainViewModel` populated in
  `OnSelectedStatementChanged`, plus a `FindingsPane` with matching visibility switching.
  Note `MainPage.xaml.cs` `OnPaneChanged` switches panes by **index** — inserting Findings
  first means renumbering the other three. Click a finding to select and `BringIntoView` its
  node (the canvas already exposes both). Severity chips, impact %, expandable Why and Fixes.
- **Copy diagnosis as markdown** — findings + narrative + suggested DDL, formatted for a
  ticket or PR comment. Small feature, gets used constantly.
- **Read-order playback** — animate the tree in actual execution order (which is *not*
  top-to-bottom), rows visibly flowing along edges, speed proportional to real elapsed time.
  You watch where the query stalls. Builds on the existing Win2D draw loop.
- **Verbosity toggle** — terse vs. expansive on all explanation text. Same data, different
  word count, for when you are showing someone else.
- **Inline SQL ↔ node mapping** — split view with statement text; clicking an operator
  highlights the clause it came from (join predicate → the `JOIN` line, seek predicate →
  the `WHERE`). Heuristic; even 70% accuracy closes the gap between "the plan is bad" and
  "which line to change." **Sequence this last in the phase** — it is the least certain
  piece and the rest of the phase must not wait on it.

**Deliverable:** every finding is explained, narrated, and exportable.

---

## Phase 6 — Navigation for large plans

**Goal:** make a 400-node ORM plan tractable. Several of these also serve the TDD §8
performance strategy, so they pay twice.

- **Auto-collapse cheap subtrees** on load — anything under ~2% of total cost, with a badge
  showing what is hidden. `PlanCanvas` already has `_collapsed`, `CollapseAll`,
  `ExpandAll`, `ToggleCollapse`, wired to the command strip's collapse menu; this adds a
  cost-thresholded variant and a menu entry beside the existing two.
- **Hot-path highlight** — compute the single root-to-leaf path carrying the most cost;
  collapse everything else by default. Most plans have one hot spine and a lot of noise.
- **Minimap** with the heat baked in — on a big plan you navigate by finding the red smudge.
- **Focus mode** — double-click to re-root the canvas on that subtree, breadcrumb to return.
- **Ranked operator list** as a peer view to the tree, sorted by self-cost. Sometimes you do
  not want a tree, you want "the top 10 expensive things." Cheap, disproportionately used.
- **Keyboard-first navigation** — extend the existing `PlanCanvas.OnKeyDown` (which already
  handles `+`/`-`/`0`/`Home` for zoom): arrows walk the tree, `n`/`p` jump between findings,
  `/` focuses search, `f` fits to view. This is a daily-use tool; make it fast
  hands-on-keyboard. Keep the existing bindings working.
- **Node annotations**, saved beside the plan file — *"this is the one that regressed after
  the 8.2 deploy."*

**Deliverable:** big plans open focused rather than overwhelming.

---

## Phase 7 — Session history and plan comparison

**Goal:** turn the app from a viewer into a tuning *loop*. TDD lists comparison as a v2
non-goal; it is pulled forward here because it is cheap once the tree model is stable, and
it is where a tuning tool earns its keep.

- **Session capture strip** — every plan opened or captured this session appears in a strip
  along the bottom. In-memory only, so the "no plan repository in v1" non-goal holds.
- **Structural tree diff** — match nodes across two plans by shape + object name, then
  color: green = only in B, red = only in A, amber = same operator, materially different
  cost or rows. New `Diagnostics/PlanDiff.cs`.
- **Metric delta table** — sortable "which operators got worse." The fastest possible read
  on a regression.
- **Plan fingerprint** — hash the tree shape (operators + objects, costs excluded). Answers
  "is this the same plan as yesterday, or did the shape change?" and is the foundation for
  any later plan-change alerting.

**Deliverable:** capture, change something, re-capture, see exactly what moved.

---

## Phase 8 — Live connection as an enrichment channel

**Goal:** the connection is worth far more than just running the query.
`Capture/PlanCaptureService.cs` already owns connectivity; add a sibling
`Capture/DatabaseContextService.cs` for read-only DMV lookups.

- ~~**Estimated plan without executing**~~ — **already shipped.** `CaptureMode.Estimated`
  drives `SET SHOWPLAN_XML ON` in `PlanCaptureService`, and `MainViewModel.CaptureAsync`
  takes the mode. Nothing to do.
- **Object context on click** — click a table operator for row count, size on disk, existing
  indexes, stats last-updated and rows modified since. All from DMVs, one query.
- **Verify index suggestions against reality** — before showing `CREATE INDEX`, check
  `sys.indexes`: does something close already exist? Is the table 4 rows or 400M? This turns
  a naive DMV suggestion into a judged one, and it is the single biggest credibility win
  available to the tool.
- **Re-run and compare in a loop** — add index, re-capture, automatic before/after diff
  using Phase 7. This is the actual tuning workflow.
- **Query Store as a plan source** — every plan a query has ever had, on a timeline with
  duration. Spot the day the plan regressed.

**Sandbox mode** (apply the suggested index inside a transaction, capture, roll back) is
genuinely useful and genuinely dangerous. If built: explicit loud opt-in per session, a
dev-database-only posture, never a default, and never silent.

**Deliverable:** suggestions are checked against the real database before they are shown.

---

## Phase 9 — Distinctive extras

Ideas that go past SSMS, Plan Explorer, and pev2. Independent of each other; pick by appetite.

- **Anti-pattern library** — give the findings memorable names (*Lookup Storm*, *Sniffing
  Skew*, *The Spool Trap*) with a consistent explainer page per name. Naming a bug makes it
  teachable and searchable; this is what makes people recommend a tool.
- **What-if estimation** — *"if this Key Lookup became a covered seek, this subtree drops
  from 71% to ~4%."* Approximate by re-costing the subtree under the assumption. Rough, but
  the *ranking* of fixes is what matters, not the precision.
- **Fix triage state** — tick findings off as tried / did not help / fixed, persisted with
  the plan. Tuning is iterative and you lose track of what you have ruled out.
- **Regression-test mode** — save a fingerprint + duration as a baseline in the repo, check
  it in CI, fail if the plan shape changes. Turns a desktop tool into part of a workflow.

---

## Dependency summary

```
Phase 1 (parser) ──→ Phase 2 (engine + core rules) ──→ Phase 3 (extended rules)
                              │                              │
                              └──────────────┬───────────────┘
                                             ▼
                              Phase 4 (coloring) ──→ Phase 5 (explanations)
                                                          │
                              Phase 6 (navigation)        ▼
                                    │              Phase 7 (history + diff)
                                    │                     │
                                    └─────────┬───────────┘
                                              ▼
                                   Phase 8 (live DB) ──→ Phase 9 (extras)
```

Nothing is blocked on infrastructure — the shell is complete. Phases 1–3 are pure C# with no
UI surface and can proceed immediately. Phase 6 depends on nothing but the existing canvas
and can be reordered freely; Phase 8's DMV work depends only on the existing
`PlanCaptureService`, though 8.5 (re-run and auto-diff) needs Phase 7.

## Ground rules for implementation

- **No tests in this effort.** Deliberate scope decision for this roadmap; the rule engine
  is designed to be testable later without rework. Verify by building and by loading
  `samples/orders-actual.sqlplan` in the app.
- `ShowplanParser` stays a pure mapper. Inference belongs in `Diagnostics/`.
- Every new Showplan field is nullable. A missing element never throws.
- New model types use default-initialised properties, not `required` — match the existing
  `Model/PlanModel.cs` style.
- A rule that throws is caught and skipped — diagnostics must never break plan viewing.
- Findings quote real numbers from the node. Never a generic blurb.
- Every `Fix` that mutates the database carries a caveat. Index suggestions always state
  their write cost.
- Keep `Model/` free of WinUI and Win2D types, as it is today. `Diagnostics/` follows the
  same rule — it is pure logic, so only `Controls/` and `ViewModels/` touch UI types.
- Estimated-only plans must keep working. Any feature that needs runtime data degrades
  visibly rather than rendering blank.

---

## Task checklist

Implementation order. Each item is a self-contained unit of work.

### Phase 1 — Parser enrichment
- [x] 1.1 Add `MemoryGrantInfo`, `WaitStat`, `ThreadRuntime`, `ParameterInfo`, `StatisticsUsage`, `CompileInfo` to the model
- [x] 1.2 Add `MemoryGrant` + `PerThread` + `HasThreadSkew` to `PlanNode`; new fields on `PlanSummary`
- [x] 1.3 Parse memory grant info in `ShowplanParser`
- [x] 1.4 Parse wait stats
- [x] 1.5 Parse per-thread runtime counters (keep existing aggregation intact)
- [x] 1.6 Parse the parameter list (compiled vs. runtime values)
- [x] 1.7 Parse optimizer statistics usage
- [x] 1.8 Parse compile info, early-abort reason, CE model version, trace flags, SET options

### Phase 2 — Diagnostics engine + core rules
- [x] 2.1 `PlanFinding`, `Fix`, `FindingSeverity`, `FindingConfidence`, `FixKind`
- [x] 2.2 `IPlanRule` + `RuleEngine` with ranking and per-rule exception isolation
- [x] 2.3 `PlanStatement.Findings` lazy property
- [x] 2.4 Rule: `estimate-blowup-origin`
- [x] 2.5 Rule: `key-lookup-storm` (with generated covering-index DDL)
- [x] 2.6 Rule: `residual-predicate-scan`
- [x] 2.7 Rule: `implicit-conversion`
- [x] 2.8 Rule: `spill-to-tempdb` (cross-referenced with memory grant)
- [x] 2.9 Rule: `non-sargable-predicate` (with before/after rewrite)

### Phase 3 — Extended rules
- [x] 3.1 Rule: `parameter-sniffing`
- [x] 3.2 Rule: `parallelism-skew`
- [x] 3.3 Rule: `stale-statistics`
- [x] 3.4 Rule: `optimizer-gave-up`
- [x] 3.5 Rule: `fat-inner-side-loop`
- [x] 3.6 Rule: `spool-trap`
- [x] 3.7 Rule: `scalar-udf`
- [x] 3.8 Rule: `wait-dominated`
- [x] 3.9 Rule: `wide-update`
- [x] 3.10 Rule: `missing-index-merge` (dedupe + merge across the batch)

### Phase 4 — Coloring
- [x] 4.1 `PlanPalette.Diverging()` + `SizeMetric.EstimateSkew`
- [x] 4.2 `SizeMetric.Efficiency` (rows read per row returned)
- [x] 4.3 `PlanNode.SelfTimeMs` + `SizeMetric.SelfTime`
- [x] 4.4 Wire the new metrics through `MetricMax`/`MetricValue`, the canvas legend, the `MainViewModel` runtime-availability guard, and the `MetricSelector` in `MainPage.xaml`
- [x] 4.5 `PlanCanvas.ColorMode` with the blame overlay; default to `Blame` when findings exist
- [x] 4.6 Edge encoding: dashed on bad estimates, distinct color across parallelism boundaries
- [x] 4.7 Redundant encoding: accent-strip width tracks heat, severity glyph on flagged nodes

### Phase 5 — Explanation layer
- [x] 5.1 `Diagnostics/OperatorGlossary.cs` + hover explainer cards
- [x] 5.2 `ViewModels/PlanNarrative.cs` — generated plan summary paragraph
- [x] 5.3 Findings panel as a fourth `SelectorBar` tab (renumber `OnPaneChanged`), ranked, click-to-select, expandable Why + Fixes
- [x] 5.4 Copy diagnosis as markdown
- [x] 5.5 Read-order playback animation
- [x] 5.6 Verbosity toggle on explanation text
- [x] 5.7 Inline SQL ↔ node mapping (split view)

### Phase 6 — Navigation
- [x] 6.1 Auto-collapse subtrees under ~2% of total cost, with a hidden-count badge
- [x] 6.2 Hot-path computation and highlight
- [x] 6.3 Minimap with heat
- [x] 6.4 Focus mode (re-root on subtree + breadcrumb)
- [x] 6.5 Ranked operator list view
- [x] 6.6 Keyboard navigation — extend the existing `PlanCanvas.OnKeyDown`, keeping the zoom binds
- [x] 6.7 Node annotations saved beside the plan file

### Phase 7 — History and comparison
- [x] 7.1 In-memory session capture strip
- [x] 7.2 `Diagnostics/PlanDiff.cs` — structural tree diff
- [x] 7.3 Diff rendering on the canvas (green/red/amber)
- [x] 7.4 Metric delta table
- [x] 7.5 Plan fingerprint (shape hash)

### Phase 8 — Live connection enrichment
- [x] 8.1 `Capture/DatabaseContextService.cs` — read-only DMV lookups
- [x] 8.2 ~~Estimated-plan capture via `SET SHOWPLAN_XML ON`~~ — shipped with the base app
- [x] 8.3 Object context on node click (row count, size, indexes, stats age)
- [x] 8.4 Verify index suggestions against `sys.indexes` before showing them
- [x] 8.5 Re-run and auto-diff loop (needs Phase 7)
- [x] 8.6 Query Store plan-history browser

### Phase 9 — Distinctive extras
- [x] 9.1 Anti-pattern library: named diagnoses + explainer pages
- [x] 9.2 What-if estimation
- [x] 9.3 Fix triage state
- [x] 9.4 Regression-test mode (baseline fingerprint + CI check)
