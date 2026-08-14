# Hot Path Plan

**Status:** Plan v1
**Author:** Khai
**Companion to:** [tuning-roadmap.md](tuning-roadmap.md) — that document built the *diagnostics
layer*; this one makes the results **legible, attributable, and temporal**.

## Premise

The tuning roadmap is delivered: sixteen rules, findings panel, blame overlay, hot-path
highlight, self-time metric, `PlanDiff`, DMV enrichment. The remaining problem is not
detection coverage. It is that an execution plan is a **structural** artefact being asked a
**profiling** question:

- A node reads `Nested Loops / Inner Join` — the operator is named, but not *what it joins*,
  *on what*, or *why it is the problem*.
- The tree spends its pixels on shape, so the operator owning 71% of the wall clock is drawn
  the same size as one owning 0.3%.
- Every view is a single instant, so "performance is degrading" — a statement about time —
  has no view that can answer it.
- Capture is post-mortem only, so the queries that hurt most (the ones still running when the
  user gives up) cannot be inspected at all.

Five phases, ordered as a chain: **read → attribute → observe → track → explain**.

## Working agreements

Carried forward from the tuning roadmap; they still bind.

- `Parsing/ShowplanParser.cs` stays a pure XML→model mapper. Derivation belongs in
  `Diagnostics/`, presentation in `Controls/` and `ViewModels/`.
- Model types **do not use `required`** — default-initialise (`= string.Empty`, `= []`,
  `= new()`).
- All new Showplan-derived fields are nullable. A missing element must never throw.
- A rule or a derivation that throws is swallowed and skipped. One bad feature must never
  break plan viewing.
- Detail-pane tabs are index-switched in `MainPage.xaml.cs` (`OnPaneChanged`). Adding or
  reordering a tab means updating those indices, not just the XAML.
- `MainViewModel.OnSelectedStatementChanged` holds a runtime-metric fallback guard. Every new
  `SizeMetric` value must be added to it or estimated plans render a flat canvas.
- Anything touching a live connection is **read-only**. No DDL, no `sp_query_store_force_plan`,
  no trace-flag changes. Generated statements are shown to copy, never executed.

---

## Phase 0 — Groundwork

**Goal:** a place to prove any of this works. `tests/SqlPlanViz.Tests/` currently contains an
empty `Samples/` directory and no project file, so today nothing below is verifiable beyond
`dotnet build`.

- [x] Create `tests/SqlPlanViz.Tests/SqlPlanViz.Tests.csproj` (xunit, net8.0) referencing
      `src/SqlPlanViz`.
- [x] Add a solution file at the repo root so `dotnet build` and `dotnet test` cover app,
      baseline tool, and tests in one command.
- [x] Copy `samples/orders-actual.sqlplan` and `samples/nested-loop-lookup-storm.sqlplan` into
      `tests/SqlPlanViz.Tests/Samples/` as embedded resources, with a `SampleLoader` helper.
- [x] Add a characterisation test per existing rule: parse a sample, assert the current
      finding set. These lock in today's behaviour before anything below changes it.
- [x] Add an estimated-only sample (no runtime stats) as a fixture — every phase below has a
      degradation path that only this fixture exercises.

**Deliverable:** `dotnet test` runs green and fails loudly if a rule's output changes.

---

## Phase 1 — Self-describing nodes

**Goal:** every node answers *what*, *how much*, and *why* without being clicked.
No new data source; all of it is derivable from the parsed model.

### Derivation

- [x] Add `Diagnostics/NodeLabeller.cs` — pure functions over `PlanNode`, no UI references.
- [x] `DescribeSources(PlanNode)` — for a node with no `ObjectName`, walk each input subtree to
      the nearest object-bearing descendants and return them ordered by input.
- [x] Render two sources as `Orders ⋈ Customers`; three or more as `3 sources`; zero as null
      (fall back to `LogicalOp`).
- [x] Handle the ambiguous shapes explicitly — Spool, Exchange/Parallelism, Concatenation,
      Compute Scalar — by passing through to the child rather than naming the operator.
- [x] `DescribeJoinKeys(PlanNode)` — extract the column pair from `Predicate` /
      `OuterReferences`, emit the short form (`on CustomerId`). Return null rather than
      guessing when the predicate does not parse.
- [x] Truncate long object names from the left (`…Orders.PK_Orders`), so the distinguishing
      part survives.
- [x] Unit tests for each of the above against both samples, plus a synthetic ambiguous-shape
      fixture.

### Canvas

- [x] Extend the subtitle line in `Controls/PlanCanvas.cs` (~line 1287) to use
      `NodeLabeller` output in place of `ObjectName ?? LogicalOp`.
- [x] Add a **verdict line**: the highest-severity `PlanFinding` touching the node, as a single
      clause, in the severity accent colour, separated by a hairline rule.
- [x] Add a **self-time share line** — `SelfTimeMs` as a percentage of statement elapsed —
      shown in place of estimated cost when `RuntimeMetricsAvailable`.
- [x] Increase node height to fit the extra lines; verify `Layout/PlanLayoutEngine.cs` spacing
      and edge routing still hold at the new size.
- [x] Add a zoom-density threshold: below it, drop to operator name plus heat only. Reuse the
      canvas's existing zoom state.
- [x] Add a **label detail** toggle (minimal / standard / full) to the command strip, persisted
      per session.
- [x] Extend the search predicate (`PlanCanvas.cs:1165`) to match derived source names, so
      searching `Customers` finds the join, not just the seek.

### Verification

- [x] Open `samples/nested-loop-lookup-storm.sqlplan`; every Nested Loops names its two sides.
- [x] Open the estimated-only fixture; no self-time line, no empty space where it would be.
- [ ] Screenshot a 200-node plan at three zoom levels and confirm it is still readable. No
      200-node fixture exists in this repo to test at that scale; the three LOD tiers (full
      card / operator-name-only / no-text) were confirmed readable on the existing samples,
      both via zoom and via the new label-detail toggle, but not at 200-node density.

**Deliverable:** the lookup-storm sample is diagnosable from the canvas alone.

---

## Phase 2 — Time attribution flame graph

**Goal:** a view sorted by time rather than by shape, and an explicit read on where the
optimizer's cost model disagrees with the clock.

### Model

- [x] Add `Model/TimeAttribution.cs` — flattens a `PlanStatement` into ordered frames
      (`NodeId`, `Depth`, `Offset`, `Width`, `Basis`).
- [x] Implement three width bases: `Elapsed`, `Cpu`, `RowsRead`.
- [x] **Handle parallelism correctly.** `ActualElapsedMs` is the max across threads, not the
      sum, so elapsed does not add up across a parallel branch. Use CPU time as the additive
      basis beneath a parallel operator, and mark frames whose width is approximate.
- [x] Add `PlanNode.CpuSelfMs` alongside the existing `SelfTimeMs` if not already derivable.
- [x] Clamp negative self-times (child elapsed exceeding parent) to zero and count them; a
      non-zero count means the basis is unreliable and the view must say so.
- [x] Unit tests: a serial plan sums to statement elapsed within tolerance; a parallel plan
      does not silently over-count.

### View

- [ ] Add `Views/FlameView.xaml` + `.cs` as a peer view to the canvas, with a view switcher in
      the command strip.
- [ ] Draw frames with width proportional to the basis, coloured by the existing
      `PlanPalette` heat ramp, labelled `Operator · object · time · ×executions`.
- [ ] Hover shows the full label when truncated; click selects the node.
- [ ] Two-way selection sync with `PlanCanvas` — selecting in either highlights in both.
- [ ] Basis selector (elapsed / CPU / rows read) with the approximate-width warning surfaced
      when parallelism is present.
- [ ] Disable the view with an explanation when `RuntimeMetricsAvailable` is false, rather than
      rendering an empty frame.

### Divergence

- [ ] Add `Diagnostics/Rules/CostModelDivergenceRule.cs` — flags operators where
      `|estimated cost share − actual time share|` exceeds a threshold, ranked by the gap.
- [ ] Register it in `RuleEngine` and add it to the rule-toggle list.
- [ ] Add a divergence column to the existing ranked-operator list: est %, actual %, delta,
      sorted by absolute delta.
- [ ] Characterisation test on `orders-actual.sqlplan` asserting the expected divergence
      ranking.

**Deliverable:** the widest bar is the problem, and the cost/clock gap is a named finding.

---

## Phase 3 — Live query profiling

**Goal:** inspect a query while it runs against read-only prod, including queries that never
complete.

### Capability detection

- [ ] Add `Capture/LiveProfilingService.cs` as a sibling to `DatabaseContextService.cs`.
- [ ] Detect server version and lightweight-profiling availability
      (default-on from SQL Server 2019; 2016 SP1+ via TF 7412).
- [ ] Detect `VIEW SERVER STATE`; on absence, surface a plain explanation of what is missing
      and why, not an exception.
- [ ] **Never offer to enable a trace flag or change instance configuration.** When profiling
      is unavailable, say so and stop.

### Session picker

- [ ] Query `sys.dm_exec_requests` joined to `sys.dm_exec_sql_text`, filtered to running
      requests above a duration threshold.
- [ ] Add `Views/LiveSessionPicker.xaml` — session id, elapsed, status, wait type, query text
      preview, sortable.
- [ ] Exclude the app's own session; make the refresh interval configurable.

### Attach and poll

- [ ] Poll `sys.dm_exec_query_profiles` filtered by `session_id`, joined to the plan tree by
      `node_id`, at a configurable interval (default 500 ms).
- [ ] Fetch the in-flight plan once via `sys.dm_exec_query_plan` / `query_plan_hash` and render
      it through the normal parser, then animate row counts filling in.
- [ ] Show per-operator running rows against estimate, with the over/under factor updating
      live.
- [ ] Carry `wait_type`, `wait_time`, `blocking_session_id` from `dm_exec_requests` into a live
      status band — a query blocked on a lock is not a plan problem and the app must say so
      before the user tunes the wrong thing.
- [ ] Run `estimate-blowup-origin` against partial row counts on each poll so the finding
      appears before the query finishes.
- [ ] Handle the query completing, being killed, or the connection dropping mid-poll —
      freeze the last state and label it, do not clear the view.
- [ ] Cancellation: detaching stops polling immediately and disposes the connection.

### Verification

- [ ] Manual test against a dev instance with a deliberately slow query; confirm rows climb.
- [ ] Manual test with `VIEW SERVER STATE` revoked; confirm the degradation message.
- [ ] Manual test against a pre-2016 instance (or simulated version string); confirm the
      unavailable path.

**Deliverable:** attach to a running prod query and watch the estimate break in real time.

---

## Phase 4 — Query Store regression forensics

**Goal:** answer *when* it got slow, *whether the plan changed*, and *why it is only slow
sometimes*.

### Access layer

- [ ] Add `Capture/QueryStoreService.cs` — read-only, all queries parameterised.
- [ ] Detect Query Store state (`sys.database_query_store_options`): off, `READ_ONLY`, or
      `READ_WRITE`; explain rather than fail when unavailable.
- [ ] **`sp_query_store_force_plan` must not be callable from the app.** If plan forcing is
      ever surfaced, it is copyable text with a caveat, never a button.

### Query browser

- [ ] Query `sys.query_store_query` + `query_store_query_text`, ranked by total duration.
- [ ] Add a **regression ranking**: recent-window average against a prior baseline window,
      sorted by ratio, with a minimum execution count to filter noise.
- [ ] Search by object name, query text fragment, or `query_id`.

### Timeline

- [ ] Query `query_store_runtime_stats` joined to `query_store_runtime_stats_interval`:
      avg / min / max / stdev duration, CPU, logical reads, `count_executions`, per interval,
      split by `plan_id`.
- [ ] Add `Views/QueryTimeline.xaml` — median line with a p50–p95 band, log scale option,
      selectable window (24 h / 7 d / 30 d / custom).
- [ ] Draw **plan-change markers**: a new `plan_id` for the same `query_id` is a vertical rule.
- [ ] Click a marker to load both plans and open the existing `Diagnostics/PlanDiff.cs` view.
- [ ] Handle intervals aged out of retention with a visible gap, not an interpolated line.

### Variance

- [ ] Add `Diagnostics/Rules/ExecutionVarianceRule.cs` — high stdev relative to mean across a
      **single stable plan** means the plan is not the problem (sniffing, skew, or cache
      state). This is the direct answer to "only slow sometimes."
- [ ] Cross-reference with `ParameterInfo.Sniffed` and `HasThreadSkew` to name the likely
      cause rather than just flagging variance.
- [ ] Surface the distribution, never just the average — an average duration hides exactly the
      case this rule exists to find.

### Query Store as a plan source

- [ ] Load any historic plan from `sys.query_store_plan` into the normal viewer so all rules
      run against it.
- [ ] Add historic plans to the session capture strip alongside file and live captures.

**Deliverable:** "it got slow last week" becomes a timestamp, a plan diff, and a named cause.

---

## Phase 5 — LLM plan analyst

**Goal:** explanation and follow-up questions, grounded on findings the rule engine already
produced. The model does language and ranking; it does **not** do detection.

### Digest

- [ ] Add `Advisor/PlanDigest.cs` — builds a compact JSON document from `PlanStatement` and its
      findings.
- [ ] **Allowlist, not scrubber.** Fields are opted in explicitly, so a new parser field can
      never leak by default. Add a test that fails when a model property has no digest
      decision recorded.
- [ ] Include: operator types, node ids, tree shape, estimated vs actual rows, executions,
      elapsed / CPU / self-time, waits, object and index names, key columns, findings with
      severity and impact fraction.
- [ ] Exclude: predicate literals and constants, parameter compiled/runtime values, query text
      (unless explicitly opted in per plan), server / instance / login / database host, any
      row values.
- [ ] Add a **payload preview** dialog reachable from the menu — the exact bytes that would be
      sent, shown before the first call of a session.
- [ ] Unit test: digest of both samples contains no string from a known-literals list.

### Client — Claude Code CLI, not an API key

The app shells out to the `claude` CLI in headless mode rather than calling the Messages API
directly. **The app then handles no credentials at all** — authentication is whatever the
user has already configured for Claude Code, and there is no key to store, leak, or rotate.
That removes the single most sensitive item from the app's threat model.

- [ ] Add `Advisor/ClaudeCliClient.cs` — spawns `claude` as a child process, streams stdout,
      cancellable via process kill.
- [ ] Detect the CLI on startup (`claude --version`); when absent, disable the advisor surface
      with an explanation and a link, not an error.
- [ ] Invoke headless with the digest on **stdin** and the question as the prompt argument:

      claude -p "<question>"
        --output-format stream-json --include-partial-messages
        --model sonnet
        --system-prompt <advisor prompt>
        --allowed-tools ""
        --strict-mcp-config
        --setting-sources ""
        --max-turns 1

- [ ] **Lock the sandbox down.** An empty `--allowed-tools` denies every tool, so the subprocess
      cannot read files or run commands; `--strict-mcp-config` with no `--mcp-config` disables
      MCP servers; `--setting-sources ""` stops user/project settings, hooks, and CLAUDE.md
      discovery from loading. Assert all four flags in a unit test over the built argument
      list — a dropped flag is a silent privilege escalation.
- [ ] Run the child process in a scratch working directory, never the user's repo or a
      database-adjacent path, so nothing on disk is reachable even if a flag regresses.
- [ ] Do **not** use `--bare`. It looks apt, but it forces `ANTHROPIC_API_KEY` authentication
      and never reads OAuth, which defeats the point of using the CLI.
- [ ] Parse `stream-json` line-delimited events into the UI as they arrive; render partial
      assistant text incrementally.
- [ ] Capture `session_id` from the init event; use `--session-id <uuid>` on the first call and
      `--resume <uuid>` for follow-ups so the chat keeps context without re-sending the digest.
- [ ] Surface `total_cost_usd` and duration from the final result event in a status line, so
      the cost of asking is visible.
- [ ] Treat the stdout schema as a **versioned external contract** — pin the tested CLI version
      range, degrade gracefully on an unrecognised event type rather than throwing.
- [ ] Handle: CLI missing, not logged in, rate-limited, non-zero exit, timeout, and the user
      cancelling mid-stream. Each gets a distinct message; none blocks plan viewing.
- [ ] Add a policy switch that disables the entire advisor surface, for environments that need
      it gone.

**Trade-offs to accept knowingly:** a Claude Code install becomes a prerequisite for the
feature (not for the app); process spawn adds latency to the first token; and requests consume
the user's own Claude plan limits rather than a per-app key. All three are acceptable given
what is bought — zero credential handling in a tool that connects to production databases.

Redaction discipline is **unchanged**. The digest still leaves the machine; the CLI is a
transport, not a boundary.

### Grounding

- [ ] Structured output: every assertion carries a `node_id` drawn from the digest.
- [ ] **Citation filter** — drop any claim referencing a node id absent from the digest, before
      display. Log the drop count; a rising count means the prompt needs work.
- [ ] Render citations as chips that select the node in canvas and flame view.
- [ ] Keep `ViewModels/PlanNarrative.cs` as the deterministic fallback; the advisor extends it
      and never replaces it.

### Surfaces

- [ ] **Narrate findings** — a paste-into-a-ticket paragraph built from the ranked findings.
- [ ] **Follow-up chat** scoped to the current plan, with the digest as context — "why is the
      Key Lookup running 84k times?", "what breaks if I add that index?"
- [ ] **Rewrite proposals** for non-sargable predicates and implicit conversions, as copyable
      text with the trade-off stated. Never auto-applied, never executed.
- [ ] **Two-plan mode** — with Phase 4 present, the digest carries both plans and the question
      becomes "what changed and why is it slower."
- [ ] Extend *Copy diagnosis as markdown* to include the advisor narrative, marked as
      model-generated.

### Verification

- [ ] Every advisor surface degrades to the deterministic path with the feature off.
- [ ] Digest redaction test passes for both samples and the estimated-only fixture.
- [ ] Argument-construction test asserts `--allowed-tools ""`, `--strict-mcp-config`,
      `--setting-sources ""`, and the scratch working directory on every invocation path.
- [ ] Manual test with the CLI uninstalled and with it logged out; both degrade cleanly.
- [ ] Manual review: an advisor answer on the lookup-storm sample cites real node ids and
      contradicts nothing the rule engine found.

**Deliverable:** grounded explanation with citations, and a deterministic app without it.

---

## Sequencing

| Phase | Effort | New dependency | Blocks |
|---|---|---|---|
| 0 · Groundwork | Low | None | Verification for everything below |
| 1 · Self-describing nodes | Low | None | Makes every other view legible |
| 2 · Flame graph | Medium | None | Cost-vs-clock divergence rule |
| 3 · Live profiling | High | `VIEW SERVER STATE`, SQL 2016 SP1+ | — |
| 4 · Query Store | High | Query Store enabled | Two-plan advisor mode |
| 5 · LLM analyst | Medium | `claude` CLI on PATH, network egress | — |

Phases 1 and 2 are offline and unblock nothing else; ship them first. Phases 3 and 4 are
independent of each other. Phase 5 is richer with 2 and 4 present but does not require them.

## Explicitly out of scope

**Sandbox mode** — apply a suggested index inside a transaction, capture, roll back. It is the
natural next step and it has no business anywhere near a read-only prod connection. If it is
ever built: dev-database-only posture, loud per-session opt-in, never a default, never silent.
