# Technical Design Document: MSSQL Execution Plan Visualizer

**Status:** v2 — implemented (stack revised from WPF to WinUI 3, see §4/§10)
**Author:** Khai
**Inspiration:** [pev2](https://github.com/dalibo/pev2) (Postgres Explain Visualizer 2)

## 1. Summary

A native Windows desktop app that visualizes SQL Server execution plans the way pev2
visualizes Postgres plans: import or capture a plan, see it as an interactive,
cost-weighted tree, and drill into the operators that are actually expensive — without
the twelve-tab-deep tooltips of SSMS's built-in graphical plan viewer.

Personal tool. Single user, single machine, no server component, no accounts.

## 2. Goals / Non-Goals

**Goals (v1)**
- Open a `.sqlplan` file or paste raw Showplan XML and render it as an interactive tree.
- Run a query against a live SQL Server connection and capture its actual plan directly
  (no need to go via SSMS first).
- Surface the things that actually matter when tuning a query: subtree cost, actual vs.
  estimated rows (the #1 symptom of a bad estimate), warnings, and missing index
  suggestions — all at a glance, not buried in a properties grid.
- Feel fast and native: instant open, no browser chrome, works with no internet
  connection.

**Non-goals (v1)**
- No plan repository / history database (each session is stand-alone; "save as" a file
  is enough for now).
- No SSMS/ADS extension integration.
- No plan comparison view (two plans side by side) — candidate for v2.
- No Query Store browsing — candidate for v2.
- No macOS/Linux support.

## 3. Why not just embed pev2's approach directly?

pev2 is a Vue component that consumes Postgres's plain-text/JSON EXPLAIN output. SQL
Server's equivalent is structurally different (Showplan XML, a strongly-typed schema
with its own quirks — parallelism branches, spools, missing index DMV-style
suggestions), so the parser and data model have to be written from scratch. But the
*visualization approach* pev2 popularized — a cost-weighted node tree you can click into,
rather than a static diagram — is exactly right for this problem too, so that's the UX
this design borrows.

## 4. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  WinUI 3 Shell (.NET 8, Windows App SDK 1.6)                 │
│  ┌───────────────┐   ┌────────────────────────────────────┐ │
│  │ MVVM ViewModels│   │ PlanCanvas (Win2D CanvasControl)    │ │
│  │ - MainViewModel│──▶│  - PlanLayoutEngine (C#, Reingold-  │ │
│  │ - NodeDetail   │   │    Tilford / Buchheim O(n))         │ │
│  │ - ConnectView  │   │  - Immediate-mode node/edge drawing │ │
│  └───────┬───────┘   │    on CanvasDrawingSession           │ │
│          │            │    (viewport-virtualized)            │ │
│          │            │  - Matrix3x2 on the drawing session  │ │
│          │            │    for pan/zoom                      │ │
│  ┌───────▼────────┐   └────────────────────────────────────┘ │
│  │ Plan Parser     │  Showplan XML → normalized PlanNode tree │
│  │ (System.Xml.Linq)│                                         │
│  └───────┬────────┘                                          │
│  ┌───────▼────────┐                                          │
│  │ Capture Service │  Microsoft.Data.SqlClient                │
│  │ - file import   │  SET STATISTICS XML ON; <query>          │
│  │ - live capture  │  SET SHOWPLAN_XML ON  (estimated only)   │
│  └────────────────┘                                          │
└─────────────────────────────────────────────────────────────┘
```

**Fully native — no WebView2.** The plan tree is drawn directly: a Win2D
`CanvasControl` renders each operator and edge immediate-mode onto a
`CanvasDrawingSession` (viewport-virtualized — see §8), positioned by a hand-rolled
layout engine. This keeps the app in one technology end to end, drops the WebView2
runtime as a dependency (no version-mismatch or cold-start-latency surprises), and
stays GPU-composited through Direct2D. The trade-off is that the tree-layout, drawing,
and pan/zoom logic has to be written by hand in C# instead of borrowed from D3 — see §8.

*Revision note:* this design originally specified WPF with `DrawingVisual`. The shell was
built on WinUI 3 instead (§10 flagged this as open). WinUI 3 has no retained
drawing-visual layer, so Win2D's immediate-mode drawing session is the equivalent
primitive — and a closer fit, since it is Direct2D rather than a retained visual tree.
Every performance point in §8 survives the swap; only the API names change.

The C# host owns everything end to end (file I/O, SQL connections, layout, rendering)
— there's no cross-technology boundary or message-passing layer to design around.

## 5. Tech stack

| Layer | Choice |
|---|---|
| Shell | WinUI 3 / Windows App SDK 1.6, .NET 8 (net8.0-windows10.0.19041.0), MVVM (CommunityToolkit.Mvvm) |
| Plan rendering | Win2D `CanvasControl`, immediate-mode node/edge drawing (viewport-virtualized), hand-rolled tree layout engine — see §8 |
| XML parsing | System.Xml.Linq |
| T-SQL parsing | Microsoft.SqlServer.TransactSql.ScriptDom — the parser SSMS and SqlPackage use. Powers the editor's token stream, completion context, parameter discovery and batch safety analysis (see [live-plan-editor-plan.md](live-plan-editor-plan.md)) |
| SQL editing | Win2D `CanvasControl` with `CoreTextEditContext` input — the editor is rendered the same way the plan canvas is, with no WebView2 and no Monaco |
| SQL connectivity | Microsoft.Data.SqlClient |
| Packaging | Unpackaged + self-contained folder — the Windows App SDK runtime ships in the output, so there is no installer dependency chain. (WinUI 3 does not support `PublishSingleFile`.) |

## 6. Plan capture

Three paths, all producing the same raw Showplan XML that feeds the one parser:

**A. File import** — open a `.sqlplan` file (these are just Showplan XML with a
different extension) or paste XML text directly.

**B. Live capture** — user supplies a connection (server, auth, database), the app
wraps their query as:
```sql
SET STATISTICS XML ON;
<user's query>
SET STATISTICS XML OFF;
```
and reads the XML result set back over `Microsoft.Data.SqlClient`. This gives an
*actual* plan (with runtime row counts, actual elapsed time per operator) rather than
just an estimated one — the more useful case for tuning.

**C. The editor** — the SQL pane is an editable T-SQL editor, and `Ctrl+Enter` composes the
edited batch (a generated `DECLARE` prelude for its parameters, then the user's text
unchanged), sends it with `SET SHOWPLAN_XML ON`, and swaps in the plan that comes back.
Estimated by default, because `SET SHOWPLAN_XML` compiles without executing and an edited
`DELETE` therefore costs nothing; the actual run is opt-in behind a confirmation that names
every modifying statement it found. Each re-plan lands in the same session history as A and
B and is diffed against a pinned baseline. See [live-plan-editor-plan.md](live-plan-editor-plan.md).

*Explicitly out of scope for v1:* attaching to a live workload via Extended Events to
capture plans for queries the app didn't itself run (useful for prod diagnosis without
re-running a query, but a meaningfully bigger feature — permissions, session
management, filtering — worth its own design pass later).

## 7. Data model

Parsed into a plain tree with no dependency on the UI layer, so the parser, the layout
engine and the view models all consume the same types:

```
PlanNode
  NodeId              int
  PhysicalOp          string   // e.g. "Clustered Index Seek"
  LogicalOp            string
  EstimatedRows        double
  ActualRows           double?   // null if this is an estimated-only plan
  EstimatedSubtreeCost  double
  EstimatedCpuCost     double
  EstimatedIoCost       double
  ActualElapsedMs       double?  // max across threads, when present
  ActualExecutions      int?
  Parallel             bool
  ObjectName            string?  // table/index touched, if any
  Predicate             string?
  OutputList            string[]
  Warnings              Warning[]
  Children              PlanNode[]

Warning
  Type        string   // e.g. "NoJoinPredicate", "ColumnsWithNoStatistics", "SpillToTempDb"
  Severity    enum { Info, Warning, Critical }
  Detail      string?

MissingIndexSuggestion
  Database, Schema, Table   string
  EqualityColumns, InequalityColumns, IncludedColumns   string[]
  ImpactPercent             double
  SuggestedCreateStatement  string   // generated by the app, not present verbatim in the XML

PlanSummary
  StatementText         string
  TotalSubtreeCost       double
  DegreeOfParallelism    int
  QueryElapsedMs          double?   // from QueryTimeStats, actual plans only
  QueryCpuMs              double?
```

Parsing note: Showplan XML sometimes carries a namespace prefix (`p1:RelOp`) and
sometimes a bare default namespace, depending on SQL Server version and client. The
parser walks by `XName.LocalName` rather than `getElementsByTagName`/qualified-name
matching, so both forms parse identically.

## 8. Visualization design

The HTML/D3 prototype in §9 was skipped — the layout algorithm was taken straight from
the literature (Buchheim's linear-time Reingold–Tilford) rather than worked out
empirically, which removed the reason to iterate in a browser first. The design is:

- **Plan tree** — top-down node/edge diagram. `PlanLayoutEngine` computes each node's
  position (Reingold–Tilford via Buchheim's O(n) formulation), and `PlanCanvas` draws
  each node and edge directly onto a Win2D drawing session at that position (see
  Performance strategy below — this is what makes virtualization possible). Node color and a
  left-edge accent strip both encode the selected size metric — subtree cost by
  default, toggle to actual rows or actual elapsed time when the plan has runtime
  data. Edges are drawn with thickness proportional to row count flowing through
  them, so a fat line into a nested loop is visible before you've clicked anything.
- **Detail panel** — a docked side panel (bound to `SelectedNode` on the view model,
  not a popup) shows estimated vs. actual rows (flagged if they diverge by more than
  ~10x, the classic bad-estimate signal), cost breakdown, predicate, output list, and
  any warnings on that node. Unlike the plan tree, this is one ordinary XAML panel —
  no performance concerns here. Note that Showplan reports `EstimateRows` *per
  execution* but `ActualRows` as a total, so the comparison uses
  `EstimateRows × (rebinds + rewinds + 1)`; comparing the raw attributes reports a
  spurious skew on the inner side of every nested loop.
- **Missing index panel** — every suggestion the plan carries, with impact % and a
  generated `CREATE INDEX` statement, copyable to clipboard.
- **Warnings summary** — a count badge plus a jump-to-node list, so a spill-to-tempdb
  three levels deep isn't invisible.
- **Search** — a bound `FilterText` property dims non-matching nodes' opacity at draw
  time, same behavior as the prototype.
- **Pan/zoom** — a single `Matrix3x2` (scale ∘ translate) assigned to the drawing
  session's `Transform`, driven by the pointer wheel (zoom, anchored on the cursor) and
  click-drag (pan). Until the user pans or zooms the view stays "auto" and re-fits on
  resize, so opening a plan never leaves it stranded off-screen.

### Performance strategy

Ordered by impact — worth building this way from the start rather than treating it as
a later optimization pass:

1. **Node visuals: drawn, not templated.** Skip the control/template/style system for
   plan nodes entirely — each operator is a few `FillGeometry`/`DrawText` calls on the
   drawing session, so there is no XAML element, no logical tree and no layout pass per
   node. This is the standard technique for rendering hundreds or thousands of items
   smoothly, and isn't meaningfully more code than a control-per-operator.
2. **Viewport virtualization.** Only draw nodes whose bounds intersect the current
   visible area (plus a small buffer), recomputed from the pan/zoom transform. On a
   300-node plan at typical zoom, that can mean drawing 20–30 nodes instead of 300.
3. **Transform the drawing session, don't re-layout.** Pan/zoom is one matrix applied
   at draw time, so no measure/arrange pass runs on a zoom tick and the tree layout is
   untouched.
4. **Build geometry once, reuse it every frame.** Plan data is immutable once loaded.
   All nodes are the same size, so a single rounded-rect geometry (plus its accent-strip
   clip) is built once and re-drawn under a per-node translation; edge geometry is built
   once per layout, not per frame. Both are rebuilt only on device-lost.
5. **Compute layout once, cache positions.** The tree-layout pass is O(n) and only
   needs to run once per loaded plan — not per frame or per pan/zoom event.
6. **Collapse/expand subtrees.** For genuinely huge plans (a UNION ALL with dozens of
   near-identical branches, say), the real lever isn't drawing faster — it's not
   realizing those nodes until the user asks. Worth pulling into the MVP rather than
   later polish, given it's the most direct fix for the worst-case plans.
7. **Level-of-detail at low zoom.** Once zoomed out far enough that node text would be
   unreadable anyway, skip text/detail drawing and render flat colored rects instead —
   cuts `DrawingContext.DrawText` work exactly when it's least needed.

## 9. Deliverables from this pass

1. This document.
2. ~~`index.html` — a standalone HTML/D3 prototype of the rendering layer.~~
   **Not built.** It was scaffolding for working out the layout algorithm empirically,
   and the algorithm came from the literature instead (Buchheim). Building a throwaway
   renderer to derive a design that was already settled would have been pure detour, so
   the app was written directly. The one thing lost is a fast loop for tuning the visual
   design — that tuning happened against the real canvas instead.

## 10. Open questions / assumptions to revisit

- **WPF vs. WinUI 3:** ~~picked WPF for tooling maturity.~~ **Resolved: WinUI 3.** The
  deciding consequence is the rendering layer — WinUI 3 has no `DrawingVisual`, so the
  plan canvas is Win2D (Direct2D) instead. That turned out to be the better primitive
  for this job, and the Fluent control set (Mica backdrop, `SelectorBar`, `InfoBar`,
  `Expander`) carries the shell. Cost: one extra dependency (Win2D), and no
  single-file publish — see §5.
- **Auth for live capture:** assuming Windows Auth (trusted connection) as the default
  and SQL auth as a fallback; no credential storage in v1 (re-enter per session).
- **Large plans:** plans with hundreds of operators (common in generated
  ORM queries) may need a "collapse subtree" affordance from the start rather than as a
  later optimization — flagging so it's not a surprise. (See §8 for the resolved
  performance approach.) **Built:** double-click or the node's expander collapses a
  subtree, with collapse-all/expand-all in the toolbar.
- **Not yet exercised at scale:** the canvas has been verified on a small plan. The
  virtualization, LOD and collapse paths are written but have not been measured against
  a several-hundred-operator plan — that is the obvious next check.

## 11. Suggested phases

1. ~~**Rendering prototype** — `index.html`.~~ Skipped, see §9.
2. **Shell** — ✅ WinUI 3 window, file open (picker, drag-drop, command line, paste XML),
   parser and Win2D canvas wired through it.
3. **Live capture** — ✅ connection dialog, `SET STATISTICS XML ON` for actual plans plus
   `SET SHOWPLAN_XML ON` for an estimated-only capture that doesn't run the query.
4. **Polish** — ✅ search, collapse/expand, missing-index and warning panels with
   jump-to-node. ⬜ Save/export is still open.
