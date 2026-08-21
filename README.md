# SQL Plan Visualizer

[![Proudly Vibe Coded - Molten Ember](https://vibecoded.fyi/badges/flat/main/proudly-vibe-coded-molten-ember.svg)](https://vibecoded.fyi/)

SQL Plan Visualizer is a native Windows application for understanding SQL Server execution plans. It turns Showplan XML into an interactive tree, ranks the most important performance findings, and explains what the plan is doing in practical terms.

![SQL Plan Visualizer showing a query plan and ranked diagnostic findings](docs/app-overview.png)

## Understand the plan, not just the diagram

Execution plans contain a lot of useful evidence, but finding the part that deserves attention can be slow. SQL Plan Visualizer brings the plan shape, runtime measurements, warnings, and tuning guidance into one workspace.

- Explore an interactive, cost-weighted operator tree.
- Rank operators by subtree cost, operator cost, actual rows, elapsed time, self time, efficiency, or estimate skew.
- Surface spills, lookup storms, cardinality errors, stale statistics, implicit conversions, parameter sniffing, and other common plan problems.
- See findings ranked by impact and confidence, with explanations and suggested next steps.
- Inspect estimated versus actual rows, cost, timing, predicates, output columns, and operator warnings.
- Review missing-index suggestions and avoid presenting unchecked DDL when no live database connection is available.
- Map a selected operator back to the SQL clause it most likely represents, highlighted and scrolled into view — from the parse tree where the batch parses, and from clause scoring where it does not.
- Edit the query in place and re-plan it against the live connection, with syntax highlighting, completions, typed parameter fields, and on-demand reformatting that one undo takes back.
- See whether an edit helped: a cost delta bar against a pinned baseline, gutter marks and inline annotations on the lines that moved, and the plan canvas recoloured by delta.
- Search, zoom, pan, collapse subtrees, focus the hot path, and replay operators in execution order.
- Compare captured plans and save regression baselines for later checks.

## Operator details

Select a finding or operator to see the evidence behind it. The detail pane combines row-estimate accuracy, cost, CPU and I/O, runtime timing, executions, warnings, predicates, and output columns.

![Selected Sort operator with row estimates, cost, timing, and tempdb spill details](docs/operator-details.png)

## Index suggestions

Missing-index recommendations are collected in one place with impact scores and key/include columns. When connected to SQL Server, suggestions can be checked against existing indexes before generating DDL.

![Index suggestions for the sample execution plan](docs/index-suggestions.png)

## Open or capture a plan

The app accepts SQL Server `.sqlplan` files and Showplan XML in several ways:

- Open a file.
- Drag a plan onto the window.
- Paste Showplan XML from the clipboard.
- Pass a plan path on the command line.
- Connect to SQL Server and capture an actual or estimated plan.
- Edit the SQL in the editor pane and press `Ctrl+Enter` to compile the edited batch.

Actual-plan capture uses `SET STATISTICS XML ON`. Estimated-plan capture uses `SET SHOWPLAN_XML ON`, so the query is compiled without being executed. Connection credentials are held only for the active connection and are not written to disk.

## Edit the query and watch the plan move

The SQL pane is a full T-SQL editor rather than a read-only view. It is drawn natively, with no
embedded browser, and is backed by the same parser SSMS uses.

- Syntax highlighting, undo that groups by word, multi-caret-free selection, and IME input.
- Completions from four sources that degrade independently: T-SQL keywords, the objects named
  by the loaded plan, the connected database's real schema, and the diagnostics layer — which
  offers missing-index columns, a SARGable rewrite of a non-sargable predicate, and the
  explicit column list that replaces `SELECT *`.
- Parameters are worked out from the batch, typed from the plan's own `ParameterList`,
  prefilled with the values the plan was captured for, and written into a `DECLARE` prelude at
  capture time. Table-valued parameters get a row grid shaped by their table type.
- `Ctrl+Enter` compiles the edited batch and swaps in the new plan. Compile errors are
  reported on the editor line they belong to, not the line of the generated batch.
- Every re-plan is diffed against a pinned baseline, so improvements accumulate across many
  edits instead of being measured against the previous attempt.

Highlighting, completions from the plan, and parameter prefill all work from a `.sqlplan` file
with nothing connected. Only re-planning needs a server.

Running the batch for real — the only way to get measured row counts and timings — is opt-in
behind a confirmation that names the connected server and every modifying statement it found.

## Run locally

Requirements:

- Windows 10 version 1809 or later
- .NET 8 SDK

Start the app:

```powershell
dotnet run --project src/SqlPlanViz
```

Open the bundled sample directly:

```powershell
dotnet run --project src/SqlPlanViz -- samples/nested-loop-lookup-storm.sqlplan
```

## Build a standalone copy

Publish a self-contained Windows build:

```powershell
dotnet publish src/SqlPlanViz -c Release -o dist
```

Run `dist/SqlPlanViz.exe` on the target machine. The publish folder includes the Windows App SDK runtime, so an installer is not required.

## Technology

SQL Plan Visualizer is built with WinUI 3, .NET 8, Win2D, CommunityToolkit.Mvvm, and Microsoft.Data.SqlClient.

For implementation and design details, see the [technical design document](docs/tdd.md).
