# Handoff — interactive-connect

Branch: `claude/interactive-connect`, cut off `main`. PR targets `main`.

## Phase 1 — COMPLETE (all 6 tasks ticked)
Connecting and capturing are now separate:
- `ConnectView.ConnectOnly` collapses the query box + mode picker.
- `MainPage.OnConnect` (connect-only dialog, "Connect" button) → `view.Commit()` +
  `MainViewModel.NotifyConnectionChanged()`, no capture. `OnCapture` keeps the old capture flow.
- Command-strip button relabelled **Connect** → `OnConnect`; empty-state "Capture from server"
  → `OnCapture`.
- Command-strip `ConnectionReadout` TextBlock bound to `MainViewModel.ConnectionDescription`
  (→ `Connection.Describe()`); `DisconnectButton` shows only while `IsConnected`.
- `ConnectionSettings.Reset()` + `MainViewModel.Disconnect()` (clears Query Store plans,
  object context, message).
- `Describe()` names the auth mode: `server · db · Windows` / `server · db · SQL login`,
  `Not connected` after reset.

Build green at every task. No live-server testing was done (see below).

## Live-server verification pending (user runs before merge)
- **t2:** command-strip Connect → fill real server → Connect; Query Store browser enables and
  lists plans with no plan captured.
- **t3:** Connect button opens connect-only dialog (no query box/mode picker); empty-state
  "Capture from server" opens the full capture dialog; both complete a real connection.
- **t4:** readout "Not connected" → `server · db` after Connect → updates again after
  capture-from-server against a *different* server.
- **t5:** connect, populate Query Store, Disconnect → readout "Not connected", button hides,
  Query Store/Re-run disable, list clears.
- **t6:** readout shows `· Windows` vs `· SQL login`; captured-plan `SourceName` still reads
  sensibly (now carries the auth label).

## Phase 2 — Microsoft Entra MFA (7 tasks, at the ceiling) — IN PROGRESS
- **t1 DONE:** `AuthMode.EntraMfa` added with deferral comment; `Describe()` maps it to
  "Microsoft Entra MFA". Build green.
- Tasks 2–3 and 7 are build-gate / non-server UI and can be done by the loop.
- **Tasks 4–5 need a real Entra-secured Azure SQL / Managed Instance** for the MFA popup and
  end-to-end auth. If that target is unavailable, the phase hands off part-done after task 3
  (+ 6/7 where possible) with 4–5 on the pending list.
- Task 6 records observed MFA re-prompt behaviour as a comment near `AuthMode` — decides
  whether the persistent-token-cache Open question becomes a phase.

## Do not re-litigate
- Branch off `main`, PR targets `main`.
- No test project — build is the gate.
- Manual verification posture: option B — tick on green build + any non-server check;
  live-server steps batched into the list above for the phase boundary. (In Ground rules.)
- Entra via `Microsoft.Data.SqlClient` `Authentication` / `SqlAuthenticationMethod`, no
  hand-rolled MSAL, no new package.
- `MainViewModel.Connection` is get-only; Disconnect uses `ConnectionSettings.Reset()`.
- Catalog-completion / editor integration out of scope here (deferred — plan Open questions).

## Open questions
- Unpackaged WinUI 3 storage APIs (blocks Phase 4 t1 / Phase 5 t2) — resolve inside the task.
- Persistent MSAL token cache — decided by Phase 2 task 6.
