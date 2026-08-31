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
- **P2 t4 (regression):** after the `Microsoft.Data.SqlClient` 5.2.2 → 6.1.2 bump, ordinary
  capture (Windows auth and SQL-login) against a live server still produces a plan.
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

### Phase 2 task 4 — package bump DONE; popup anchoring BLOCKED (premise was wrong)

**Bump done & verified.** `Microsoft.Data.SqlClient` 5.2.2 → **6.1.2** (latest 6.x in the local
nuget cache) in `SqlPlanViz.csproj`. Build green; `dotnet run` launches the app clean on the new
major version (no crash, no stderr). Ordinary-capture regression check against a live server is
on the live-server pending list.

**Anchoring blocked — the parent-window hook does not exist in 6.x or 7.x either.** The rewritten
task 4 (and the earlier handoff) assumed `ActiveDirectoryAuthenticationProvider` gained
`SetParentActivityOrWindowFunc` / a `Func<object>` parent-window ctor in 6.0. It did not.
Reflection over `microsoft.data.sqlclient/6.1.2/lib/net8.0/Microsoft.Data.SqlClient.dll` shows
the full public surface of `ActiveDirectoryAuthenticationProvider` is:

- ctors: `()`, `(string applicationClientId)`, `(Func<DeviceCodeResult,Task>, string)`
- methods: `AcquireTokenAsync`, `ClearUserTokenCache`, `IsSupported`, `BeforeLoad`,
  `BeforeUnload`, `SetAcquireAuthorizationCodeAsyncCallback`, `SetDeviceCodeFlowCallback`

No parent-window/activity member anywhere. The 7.0.1 XML doc confirms the same (no `parent*`
member on the type). So bumping the package — the thing the user approved — does **not** unlock
window anchoring.

Options now (unchanged in substance from before, minus the "just upgrade" one which is disproven):
  1. **Drop the anchoring requirement.** Ship `ActiveDirectoryInteractive` as wired in task 2.
     The MSAL popup still appears (system browser / embedded WebView2), just not owned by the app
     window. On Windows desktop it comes to the foreground on its own; the practical cost is low.
     Task 4 becomes "won't-fix, documented" and the package bump is kept for its own sake.
  2. **Hand-rolled `SqlAuthenticationProvider`** whose `AcquireTokenAsync` calls MSAL
     (`Microsoft.Identity.Client`, transitive) directly with
     `.WithParentActivityOrWindow(App.WindowHandle)`. This is the "no hand-rolled MSAL" that the
     do-not-relitigate list rules out — needs the user to lift that.
  3. **`SetAcquireAuthorizationCodeAsyncCallback`** — supply our own browser step and host it in
     an owned window. More code than 2, same rule tension, and we'd be reimplementing the auth-code
     redirect listener.

Recommendation: **option 1.** Keep the 6.1.2 bump (already done, green), document anchoring as
won't-fix, move on to tasks 5–7. Only option 2/3 give a truly parented popup and both need a
ruling on the hand-rolled-MSAL line.

**User: choose 1, 2, or 3.**

Current Phase 2 state: **tasks 1–3 ticked, build green. Task 4 — package bump done (uncommitted
pending this decision / committed as a partial step), anchoring blocked on the choice above.
Tasks 5–7 not started.**
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
