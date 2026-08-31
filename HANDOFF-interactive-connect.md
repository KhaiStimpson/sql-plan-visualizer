# Handoff — interactive-connect

Branch: `claude/interactive-connect`, cut off `main`. PR targets `main`.

## Manual verification posture (decided — do not re-ask)
Option B: loop ticks on green build + any non-server check possible; all live-server
verification is batched into the list below for the user to run at each phase boundary.
Recorded in the plan's Ground rules.

## Live-server verification pending (user runs at phase boundary)
- **Phase 1 task 2:** launch, click command-strip **Connect**, fill a real server, Connect;
  open the Query Store browser — confirm it is enabled and lists plans with no plan captured.
- **Phase 1 task 3:** command-strip **Connect** button opens the connect-only dialog (no query
  box / mode picker); empty-state **Capture from server** opens the full capture dialog; both
  complete a real connection. (App confirmed to launch clean.)
- **Phase 1 task 4:** command-strip readout shows "Not connected" at start; after Connect it
  shows `server · db`; after capture-from-server against a *different* server it updates again.

## State
- Plan rewritten against `main` (2026-09-01); catalog/editor wiring deferred (see plan Open
  questions).
- Phase 1 task 1 done: `ConnectView.ConnectOnly`.
- Phase 1 task 2 done: `OnCapture`/`OnConnect` split + `MainViewModel.NotifyConnectionChanged()`.
  Build green. Live-server check pending (above).
- Next: Phase 1 task 3 — swap command-strip "Capture" button to "Connect" wired to `OnConnect`;
  leave empty-state button on `OnCapture`.

## Do not re-litigate
- Branch off `main`, PR targets `main`.
- No test project — build is the gate.
- Manual verification posture: option B (above).
- Entra via `Microsoft.Data.SqlClient` `Authentication`, no hand-rolled MSAL, no new package.
- `MainViewModel.Connection` is get-only; Disconnect uses `ConnectionSettings.Reset()`.
- Catalog-completion / editor integration out of scope here.

## Open questions
- Unpackaged WinUI 3 storage APIs (blocks Phase 4 t1 / Phase 5 t2) — resolve inside the task.
- Persistent MSAL token cache — decided by Phase 2 task 6.
- Entra MFA manual steps (Phase 2) need a real Entra-secured Azure SQL / Managed Instance.
