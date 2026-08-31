# Handoff — interactive-connect

Branch: `claude/interactive-connect`, cut off `main`. PR targets `main`.

## BLOCKED — needs your decision on manual verification

Phase 1 task 2 code is **implemented and committed** (`OnCapture`/`OnConnect` split +
`MainViewModel.NotifyConnectionChanged()`), build green — but **not ticked**, because its
manual step needs a real SQL Server: *"click Connect, fill a real server, Connect; then open
the Query Store browser and confirm it is enabled and lists plans."* I have no live server
or credentials, and this is a WinUI 3 desktop app with no automation harness — I cannot
click through it.

Almost every task in Phases 1–3 has a live-server manual step. How do you want the loop to
handle this?
- **A:** You run each UI verification yourself when the loop stops for it, then tell it to
  tick and continue. (Loop stops ~once per task.)
- **B:** Loop ticks on green build + whatever non-server UI check is possible, and batches
  all live-server verification for you to do at each phase boundary. Handoff lists what to
  check.
- **C:** You provide a reachable dev SQL Server (name + auth) the loop can use, at least for
  Windows/SQL-auth paths (Entra MFA in Phase 2 still needs your Azure target).

Until you decide, the loop is stopped.

## State
- Plan **rewritten against `main`** (2026-09-01). The earlier draft assumed catalog/editor
  infrastructure that lives only on the unmerged `claude/live-plan-editor-impl-svla9m` branch;
  that wiring is now explicitly deferred (Open questions, last item).
- Phase 1 task 1 done: `ConnectView.ConnectOnly` property. Build green.
- Next task: **Phase 1 task 2** — split `OnConnect`/`OnCapture` in `MainPage.xaml.cs`, add
  `MainViewModel.NotifyConnectionChanged()`. Has a manual UI verification step.

## Do not re-litigate
- Branch off `main`, PR targets `main` (user decided).
- No test project — build is the gate.
- Entra via `Microsoft.Data.SqlClient` `Authentication`, no hand-rolled MSAL, no new package.
- `MainViewModel.Connection` is get-only; Disconnect uses `ConnectionSettings.Reset()`.
- Catalog-completion / editor integration is out of scope here — follow-up once that infra
  reaches `main`.

## Open questions
- Unpackaged WinUI 3 storage APIs (blocks Phase 4 t1 / Phase 5 t2) — resolve inside the task.
- Persistent MSAL token cache — decided by Phase 2 task 6.
- Phases 2–3 manual steps need a real Entra-secured Azure SQL / Managed Instance target.
