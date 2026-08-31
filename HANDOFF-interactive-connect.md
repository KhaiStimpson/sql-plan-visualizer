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
- **P2 t4:** against a real Entra-secured Azure SQL / MI target, the MFA popup (driven by the
  custom `InteractiveAuthProvider`) appears anchored to the app window and "Test connection"
  succeeds; a second connect in the same session is silent (MSAL in-memory cache).
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

- **t4 DONE:** `Microsoft.Data.SqlClient` 5.2.2 → **6.1.2** (commit `e7a3afd`), plus a custom
  `SqlAuthenticationProvider` — `src/SqlPlanViz/Capture/InteractiveAuthProvider.cs` — that calls
  MSAL directly to anchor the Entra MFA popup. The bundled `ActiveDirectoryAuthenticationProvider`
  has **no** parent-window hook at any version (reflection-verified over 6.1.2 and 7.0.1), so the
  "no hand-rolled MSAL" rule was lifted (recorded in the plan's Ground rules). Provider:
  `PublicClientApplicationBuilder.Create(<SqlClient public app id>)` — `SqlAuthenticationParameters`
  carries no `ClientId` in 6.1.2, so the documented id `2fd908ad-0664-4344-b9be-cd3e8b574c38` is
  always used — `.WithAuthority(parameters.Authority).WithRedirectUri("http://localhost")`, then
  `AcquireTokenSilent` → on `MsalUiRequiredException` `AcquireTokenInteractive(scopes)`
  `.WithParentActivityOrWindow(() => hwnd)` `.WithLoginHint(parameters.UserId)` (hint only when
  non-empty), scopes `{ parameters.Resource.TrimEnd('/') + "/.default" }`. Registered once in
  `App.OnLaunched` via `InteractiveAuthProvider.Register(() => App.WindowHandle)` after the HWND
  is captured. Added an explicit `Microsoft.Identity.Client` `PackageReference` at **4.73.1** — the
  exact version already pulled transitively via `Azure.Identity` 1.14.2, so **no new subtree**,
  just a promotion to direct. Build green; `dotnet run` launches clean (provider registration at
  startup does not throw). Live-server steps on the pending list.
- Task 7 is build-gate / visual UI and can be done by the loop.

Current Phase 2 state: **tasks 1–4 ticked, build green. Tasks 5–6 need a live Entra target
(pending list). Task 7 (InfoBar copy) is build-gate + visual, doable by the loop.**
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
