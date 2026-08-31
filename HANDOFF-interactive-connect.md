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

## Phase 2 — Microsoft Entra MFA (7 tasks) — PART-DONE (t1-4,7 ticked; t5-6 live-only, on pending list)
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
- **t7 DONE:** `ConnectView` `InfoBar` reworded to "Credentials are used for this connection only
  and are never written to disk, though Microsoft Entra sign-in tokens stay cached in memory until
  the app closes." One sentence. Build green, app launches clean.

## Phase 2 — FINAL STATUS (part-done, as far as the loop can take it)
- **Ticked: t1, t2, t3, t4, t7** — build green at every step, app launches clean.
- **NOT ticked: t5, t6** — both are *purely* live-Entra-target verification with no code
  component the loop can do. They stay on the pending list below. Per the plan's Phase 2 preamble
  this is the expected "hands off part-done" outcome when no live target is available.
- **t6** additionally needs a one-line comment recorded near `AuthMode` in `ConnectionSettings.cs`
  once the re-prompt behaviour is observed — that comment decides whether the persistent MSAL
  token-cache Open question becomes its own phase.

## Phase 3 — Connection-string mode (4 tasks) — CODE COMPLETE (t1-3 ticked; t4 live-only)
Phase boundary reached. All code tasks done on green builds; the only remaining task is
live-server manual verification with no code component — the plan's "hand off part-done" case.

- **t1 DONE:** `ConnectionSettings.RawConnectionString` + `UseConnectionString` added (both
  cleared by `Reset()`). `ConnectView` gains an `EntryModeBox` ComboBox ("Enter details" /
  "Paste connection string"); `ApplyEntryMode()` toggles `DetailsPanel` (a new StackPanel
  wrapping the server/db grid, `AuthBox`, `SqlAuthPanel`, encrypt/trust panel) against a
  collapsed multiline `ConnectionStringBox`. Constructor prefills text + mode and calls
  `ApplyEntryMode()`; `Commit()` persists both. Build green, `dotnet run` launches clean.
- **t2 DONE:** `BuildConnectionString` now short-circuits to `BuildFromRawConnectionString(raw)`
  when `RawConnectionString` is non-empty — parses via `new SqlConnectionStringBuilder(raw)`
  (try/catch on `ArgumentException`/`FormatException`/`KeyNotFoundException` →
  `PlanCaptureException`), sets `ApplicationName`/`ConnectTimeout` only when not already
  present. Build green.
- **t3 DONE:** connection-string mode is authoritative. `ConnectView.Commit()` in raw mode
  calls `_settings.Reset()` then sets only `UseConnectionString` + `RawConnectionString`;
  details mode clears both. `ConnectionSettings.Describe()` → `DescribeRawConnectionString()`
  when raw mode active: parses via `SqlConnectionStringBuilder` and returns
  `DataSource · InitialCatalog · connection string` ("Connection string" on parse failure or
  missing DataSource). `Reset()` still yields "Not connected". Build green, app launches clean.
- **t4 NOT ticked:** purely live-server manual verification (known-good Entra + SQL-auth
  strings connect via Test connection; malformed string shows a clear error; toggling back to
  details mode restores form editing). On the pending list below.

## Phase 3 — Live-server verification pending (user runs before merge)
- **P3 t1 (UI, non-server):** open Connect, switch input mode to "Paste connection string" →
  the details form (server/db/auth/sql-auth/encrypt) hides and a single multiline connection-
  string box shows; switch back to "Enter details" → the form returns.
- **P3 t3 (UI + server):** connect via a pasted string; the command-strip status readout shows
  the right `server · db · connection string`.
- **P3 t4:** a known-good Entra string and a known-good SQL-auth string both connect via "Test
  connection"; a malformed string shows a clear error; toggling back to details mode restores
  form editing.

## Phase 4 — Remember recent connections (5 tasks) — CODE COMPLETE (t1-4 ticked; t5 live-only)
Phase boundary reached. All code tasks done on green builds; t5 is purely live-server manual
verification with no code component — the plan's "hand off part-done" case.
- **t1 DONE:** `src/SqlPlanViz/Capture/RecentConnectionsStore.cs`. `RecentConnection` record
  (Server, Database, UserId, Auth — no password) + `RecentConnectionsStore`. JSON file at
  `%LOCALAPPDATA%\SqlPlanViz\recent-connections.json` via
  `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` — **no `ApplicationData.Current`**.
  `Load()` returns most-recent-first, never throws (missing/corrupt → empty). `Record(entry)`
  front-inserts, dedups by Server+Database (OrdinalIgnoreCase), caps at 10 (`MaxEntries`), ignores
  blank-server entries. `Save()` creates the subfolder and swallows IO/UnauthorizedAccess so a
  disk failure never breaks connecting. Enum stored as string. A 2nd ctor takes an explicit path.
  Build green. Store semantics reasoned through (no live server needed for this one).
  **Unpackaged-storage open question — RESOLVED for Phase 4:** the plain `LocalApplicationData`
  JSON approach compiles and is the right call; nothing about it needs a package identity. Phase 5
  task 2 still needs to confirm `PasswordVault` separately.

- **t2 DONE:** `ConnectView` has a `RecentConnectionsStore _recent`; details-mode `Commit()` ends
  with `_recent.Record(new RecentConnection(Server, Database, UserId, Auth))`. Connection-string
  mode returns before that (pasted string may embed a password). Build green.

- **t3 DONE:** `ServerBox` + `DatabaseBox` are now `AutoSuggestBox` (`.Text` API unchanged).
  Constructor loads `_recentConnections` and seeds server suggestions; `OnServer/DatabaseTextChanged`
  refilter on user input; `OnServerSuggestionChosen` prefills Database/Login/Auth from the matching
  entry. Build green, app launches clean.

- **t4 DONE:** `ConnectView` `InfoBar` reworded to "Recent servers and logins are remembered on
  this PC, but passwords are never written to disk, though Microsoft Entra sign-in tokens stay
  cached in memory until the app closes." One sentence; keeps the Phase 2 task 7 Entra clause.
  Build green.
- **t5 NOT ticked:** purely live-server — on the pending list below.

### Phase 4 — Live-server verification pending (user runs before merge)
- **P4 t3/t5:** connect to two different servers for real, relaunch the app, open Connect — both
  appear as `ServerBox` suggestions and picking one prefills server/database/login/auth. (The
  store read/write/dedup/cap-10 logic is exercised whenever a details-mode connect commits; only
  the "entries written by a real connect, survive relaunch, prefill correctly" round-trip is
  unverified by the loop.)

## Phase 4 — FINAL STATUS
- **Ticked: t1, t2, t3, t4** — build green at every step, app launches clean.
- **NOT ticked: t5** — purely live-server, on the pending list.
- **Unpackaged storage — RESOLVED for Phase 4:** `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`
  + `SqlPlanViz\recent-connections.json` compiles and runs with no package identity needed;
  `ApplicationData.Current` was correctly avoided. This does NOT resolve Phase 5's `PasswordVault`
  question — that is a different API (`Windows.Security.Credentials`) and still needs its own
  check at Phase 5 task 2 (fall back to DPAPI `ProtectedData` over a JSON store if it throws
  unpackaged).

## What Phase 5 needs (do NOT start it here)
Phase 5 = optional SQL-auth password storage, strictly opt-in, OS-backed, revocable.
- **t1:** "Remember password" `CheckBox` in `SqlAuthPanel` (`ConnectView.xaml`), visible for
  `SqlLogin`, unchecked by default. Build gate.
- **t2 — has its own open question:** confirm `Windows.Security.Credentials.PasswordVault` works
  for this UNPACKAGED app as the first step. It generally does, but if it throws, fall back to
  DPAPI (`System.Security.Cryptography.ProtectedData`) over a JSON store next to
  `recent-connections.json`. Resolve inside the task, do not guess. Then write on `Commit()` when
  checked (key = `Server` + `UserId`), remove on unchecked.
- **t3:** on dialog open / recent-suggestion chosen, read back the vault credential for
  `Server` + `UserId` and prefill `PasswordBox`. The suggestion-chosen hook already exists
  (`OnServerSuggestionChosen` in `ConnectView.xaml.cs`) — extend it.
- **t4:** "Forget saved password" button beside the checkbox, enabled only when a credential
  exists for the current `Server` + `UserId`.
- **t5:** reword the `InfoBar` again to cover the opt-in stored-password case.
- **t6:** end-to-end live verification → pending list.

## Phase 5 — Optional password storage (6 tasks) — CODE COMPLETE (t1-5 ticked; t6 live-only)
Phase boundary reached. All code tasks done on green builds; t6 is purely live-server manual
verification with no code component — the plan's "hand off part-done" case.
**Credential mechanism: `Windows.Security.Credentials.PasswordVault`** (NOT DPAPI) — the
unpackaged round-trip check in t2 passed, so no package identity is needed and the
`ProtectedData` fallback was never used. Recorded in a class comment on `PasswordVaultStore`.

- **t1 DONE:** `SqlAuthPanel` in `ConnectView.xaml` changed from `Grid` to `StackPanel` wrapping
  the login/password `Grid` plus a new `RememberPasswordBox` `CheckBox` (unchecked by default,
  tooltip explains Credential Manager storage). `OnAuthChanged` still toggles `SqlAuthPanel`
  visibility, so the checkbox is visible only for `SqlLogin`. Build green.
- **t2 DONE — open question RESOLVED:** **`PasswordVault` is the mechanism, not DPAPI.** A
  throwaway console app on the app's exact TFM (`net8.0-windows10.0.19041.0`, unpackaged) ran
  `vault.Add` → `Retrieve` → `RetrievePassword` → `Remove` successfully, so no package identity is
  needed and the DPAPI (`ProtectedData`/CurrentUser) fallback was not used. Recorded in a class
  comment on `PasswordVaultStore`. New `src/SqlPlanViz/Capture/PasswordVaultStore.cs`
  (`Save`/`Remove`/`Retrieve`/`Has`, resource `"SqlPlanViz"`, account `Server|UserId`, all paths
  try/catch). `ConnectView.Commit()` details path: `SqlLogin` + `RememberPasswordBox` checked →
  `Save`; else → `Remove`. Build green; credential round-trip verified via the snippet.
- **t3 DONE:** `ConnectView.TryPrefillPassword(server, userId)` — on a vault hit sets
  `PasswordBox.Password` and ticks `RememberPasswordBox`. Called from the constructor and from
  `OnServerSuggestionChosen`. Build green.
- **t4 DONE:** `ForgetPasswordButton` beside `RememberPasswordBox`, disabled by default.
  `UpdateForgetPasswordState()` (`_passwords.Has(server, userId)`) runs on server/login text
  changes and after a prefill. `OnForgetPassword` removes the vault entry, clears `PasswordBox`,
  unchecks the box. Build green.
- **t5 DONE:** `ConnectView` `InfoBar` reworded to one sentence covering the opt-in stored-password
  case: "Recent servers and logins are remembered on this PC, a SQL password is never written to
  disk and is kept only in Windows Credential Manager when you tick \"Remember password\", and
  Microsoft Entra sign-in tokens stay cached in memory until the app closes." Build green, app
  launches clean.
- **t6:** NOT ticked — purely live-server end-to-end (on the pending list above). No code.

### Phase 5 — Live-server verification pending (user runs before merge)
- **P5 t3/t6:** check "Remember password", connect for real, relaunch, pick the server from
  `ServerBox` suggestions → `PasswordBox` prefills and "Remember password" is ticked.
- **P5 t4/t6:** "Forget saved password", relaunch → no prefill.

## Phase 5 — FINAL STATUS
- **Ticked: t1, t2, t3, t4, t5** — build green at every step; app launches clean.
- **NOT ticked: t6** — purely live-server end-to-end, no code component. On the pending list.
- **Open question RESOLVED:** `PasswordVault` works unpackaged on this target (t2 snippet
  round-trip: `Add` → `Retrieve` → `RetrievePassword` → `Remove`). Mechanism = `PasswordVault`,
  not DPAPI; no `ProtectedData` fallback needed. Documented in the `PasswordVaultStore` class
  comment and the plan's t2 tick note.
- New file: `src/SqlPlanViz/Capture/PasswordVaultStore.cs`.
- `ConnectView`: `RememberPasswordBox` + `ForgetPasswordButton` in `SqlAuthPanel` (now a
  `StackPanel`); `Commit()` save/remove on the SqlLogin path; `TryPrefillPassword` on open and
  suggestion-chosen; `UpdateForgetPasswordState` gates the button; InfoBar reworded.

## What Phase 6 needs (do NOT start it here)
Phase 6 = named connection profiles: a separate `ConnectionProfileStore` holding the full config
(Server, Database, Auth incl. `EntraMfa`, Encrypt, TrustServerCertificate, UserId, "password is
vaulted" + "is raw connection string" flags); a "Save as…" affordance in `ConnectView`; a profile
picker that loads every field (pulling the vaulted password via `PasswordVaultStore` when the flag
is set); rename/delete; one-click profile entries in the empty-state panel (around
`MainPage.xaml:501`). `PasswordVaultStore` and `RecentConnectionsStore` are reusable as-is.
Phase 6 is explicitly droppable/deferrable per the plan.

## Phase boundary — STOP after Phase 5
Phase 5 code is complete (t1-5 ticked, t6 live-only). Do NOT start Phase 6.

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
- Unpackaged WinUI 3 storage APIs — **RESOLVED both sides.** Phase 4: plain `LocalApplicationData`
  JSON file works, no package identity needed. Phase 5 task 2: `PasswordVault` works unpackaged on
  this target (verified round-trip) — used directly, no DPAPI fallback.
- Persistent MSAL token cache — decided by Phase 2 task 6.
