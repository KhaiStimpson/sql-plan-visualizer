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
- **P2 t3 (UI, non-server):** open Connect, switch auth to "Microsoft Entra MFA" → login/password
  fields hide; switch back to "SQL Server Authentication" → they return; "Windows" shows neither.
- **P2 t4:** against a real Entra-secured Azure SQL / MI target, the MFA popup appears anchored to
  the app window and "Test connection" succeeds.
- **P2 t5:** Entra MFA connect from the command-strip Connect button makes the Query Store browser
  live; capture-from-server with Entra MFA still produces a plan.
- **P2 t6:** repeat connects within one session do not re-prompt for MFA (MSAL in-memory cache);
  record observed behaviour as a comment near `AuthMode` in `ConnectionSettings.cs`.
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
- **t2 DONE:** `BuildConnectionString` auth branch is now a `switch`; `EntraMfa` sets
  `Authentication = ActiveDirectoryInteractive`, no UserID/Password. Build green.
- **t3 DONE:** 3rd `AuthBox` item "Microsoft Entra MFA"; `AuthToIndex`/`IndexToAuth` helpers.
  `SqlAuthPanel` still shows only for index 1 (SqlLogin). Build green, app launches clean.
- Task 7 is build-gate / visual UI and can be done by the loop.

### BLOCKED — Phase 2 task 4 needs a decision from the user

Task 4 ("anchor the MFA popup to the app window … `WithParentActivityOrWindow` via a custom
`SqlAuthenticationProvider` registration, or the provider's parent-window hook") assumes the
bundled `Microsoft.Data.SqlClient` exposes a parent-window hook. **It does not at the pinned
version 5.2.2.** `ActiveDirectoryAuthenticationProvider` in 5.2.2 has no
`SetParentActivityOrWindowFunc` and no `Func<object>` (parent activity/window) constructor —
those arrived in Microsoft.Data.SqlClient 6.0. In 5.2.2 the only public ctors are `()`,
`(string clientId)`, and `(Func<DeviceCodeResult,Task>, string)`; the setters are
`SetAcquireAuthorizationCodeAsyncCallback` and `SetDeviceCodeFlowCallback` only.

So there is no way to anchor the interactive popup to the app HWND without one of:
  1. **Upgrade `Microsoft.Data.SqlClient` to 6.x** (6.1.2 / 7.0.1 are already in the local
     nuget cache) and use `ActiveDirectoryAuthenticationProvider(() => App.WindowHandle)` or
     `SetParentActivityOrWindowFunc`. This is a package *version bump*, which brushes against
     the "no new package" line (it is an upgrade, not a new dependency, but still a change).
  2. **Register a hand-rolled `SqlAuthenticationProvider`** whose `AcquireTokenAsync` calls MSAL
     (`Microsoft.Identity.Client`, already transitive) directly with
     `.WithParentActivityOrWindow(App.WindowHandle)`. This is exactly the "no hand-rolled MSAL"
     that the do-not-relitigate list rules out.
  3. **Drop the anchoring requirement** — ship `ActiveDirectoryInteractive` as-is (task 2 already
     wires it). The MSAL popup still appears, just not parented to the app window; it is a
     top-level browser/dialog. Task 4 becomes "documented as won't-fix at 5.2.2".

Both 1 and 2 cross a settled decision, so the loop stopped rather than pick one. **User: choose
1, 2, or 3** (or confirm the package upgrade is acceptable). Tasks 5–6 (live-server) partly
depend on 4; task 7 (InfoBar copy) does not but the loop does not skip ahead.

Current Phase 2 state: **tasks 1–3 ticked, build green. Task 4 blocked on the above. Tasks 5–7
not started.**
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
