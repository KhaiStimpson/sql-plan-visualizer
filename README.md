# SQL Plan Visualizer

A native Windows app that visualizes SQL Server execution plans as an interactive,
cost-weighted tree — the pev2 approach, applied to Showplan XML.

Design: [docs/tdd.md](docs/tdd.md).

## Running it

```bash
dotnet run --project src/SqlPlanViz
```

Open a plan directly:

```bash
dotnet run --project src/SqlPlanViz -- samples/orders-actual.sqlplan
```

## Building a standalone copy

WinUI 3 doesn't support single-file publish, so the "no installer dependency chain"
deliverable is a self-contained folder — the Windows App SDK runtime ships inside it, so
the target machine needs nothing installed:

```bash
dotnet publish src/SqlPlanViz -c Release -o dist
```

Then run `dist/SqlPlanViz.exe`.

## What it does

**Loading a plan** — open a `.sqlplan` file, drag one onto the window, paste Showplan XML,
pass a path on the command line, or capture one from a live server.

**Live capture** — point it at a SQL Server instance (Windows auth or a SQL login), paste a
query, and it runs `SET STATISTICS XML ON` to get the *actual* plan with runtime row counts.
There's also an estimated-only mode (`SET SHOWPLAN_XML ON`) that compiles the query without
running it — useful when the query is slow or writes data. Credentials are held for the
connection only and never written to disk.

**Reading the plan** — the tree is colour-weighted by whichever metric you pick (subtree
cost, operator cost, actual rows, elapsed time), edges are thickened by the rows flowing
through them, and an operator whose estimate missed by 10× or more says so on the node
itself. The side panel breaks down the selected operator; the other two tabs list every
missing-index suggestion (with a generated `CREATE INDEX` you can copy) and every warning
in the plan, each of which jumps to its operator.

**Navigating** — scroll to zoom, drag to pan, double-click an operator to collapse its
subtree, and search to dim everything that doesn't match.

## Layout

```
src/SqlPlanViz/
  Model/          normalized plan tree — no UI or XML dependencies
  Parsing/        Showplan XML → model (namespace-agnostic, walks by LocalName)
  Layout/         Reingold–Tilford tree layout (Buchheim's O(n) variant)
  Controls/       Win2D plan canvas + theme palette
  Capture/        live capture over Microsoft.Data.SqlClient
  ViewModels/     MainViewModel, formatted operator detail
  Views/          connection dialog
samples/          an example actual plan to open
```

## Stack

WinUI 3 (Windows App SDK 1.6, unpackaged + self-contained), .NET 8, Win2D for the plan
canvas, CommunityToolkit.Mvvm, Microsoft.Data.SqlClient.
