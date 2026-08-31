# Handoff — interactive-connect

Branch: `claude/interactive-connect`, cut off `main`. PR targets `main`.

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
