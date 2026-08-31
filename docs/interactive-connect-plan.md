# Interactive database connections — plan

Status: in progress · Branch: `claude/interactive-connect` (cut off `main`; PR targets `main`) ·
Written: 2026-08-22 · Reworked: 2026-09-01 (rebased onto `main`, not the editor branch)

## Goal

Connecting to a SQL Server stops being welded to capturing a plan, and gains near-SSMS parity for
getting *in*. Concretely, when this is done:

- A **Connect** button replaces **Capture** in the command strip. It opens a connection with no
  query in hand and makes the connection-dependent surfaces (Query Store browser, object context,
  re-run) live against it. Capturing a plan from a live query is still reachable (empty-state
  panel) — the button goes, the capability does not.
- A connection **status readout** in the command strip always shows which server/database/auth is
  live, with a **Disconnect** that tears the connection and its derived state back down.
- **Microsoft Entra MFA** (interactive browser/popup auth) is selectable alongside Windows and
  SQL auth and actually authenticates against a real Entra-secured Azure SQL / Managed Instance.
- A **connection-string** mode lets you paste a full ADO.NET string and connect verbatim,
  bypassing the form.
- **Recent connections** (server, database, login, auth — never the password) are remembered on
  the PC and offered back on the next connect.
- A SQL-auth **password can optionally be remembered** via Windows Credential Manager, opt-in and
  revocable — never plaintext to disk.
- **Named connection profiles** hold a full config (including Entra MFA and remembered-password
  references) and connect in one click from the dialog or the empty-state panel.

## Drift — reworked 2026-09-01 against `main`

The first rework (also dated 2026-08-22→09-01) was written against the **editor branch**
(`claude/live-plan-editor-impl-svla9m`) and referenced a completion engine, catalog/tuning
providers (`_catalogProvider`, `_tuningProvider`, `CatalogSnapshot.Empty`, `RefreshCatalogAsync`)
and an editor parser (`TSqlParserFactory`). **None of that is on `main`.** This effort's branch is
cut off `main` and its PR targets `main`, so every task must build against `main` as it is today.

What `main` actually has:

- `ConnectionSettings` (`src/SqlPlanViz/Capture/ConnectionSettings.cs`) — `Server`, `Database`,
  `Auth` (`AuthMode` enum: `Windows`, `SqlLogin`), `UserId`, `Password`, `Encrypt`,
  `TrustServerCertificate`, `CommandTimeoutSeconds` (default 60), and a `Describe()` that already
  returns `"Not connected"` when `Server` is blank else `"server · db"`.
- `ConnectView` (`src/SqlPlanViz/Views/ConnectView.xaml[.cs]`) — the form, `Commit()`,
  `OnTestConnection`, `OnAuthChanged`, and a **`ConnectOnly` property (Phase 1 task 1 — already
  landed)** that collapses `QueryBox` + `ModeButtons`.
- `PlanCaptureService` — `CaptureAsync`, `TestConnectionAsync`, `internal static
  BuildConnectionString(ConnectionSettings)`.
- `MainViewModel` (`src/SqlPlanViz/ViewModels/MainViewModel.cs`) — `Connection` is a **get-only
  single instance** (`public ConnectionSettings Connection { get; } = new();`). Connection-derived
  state that exists today: `CanRerun`, `CanBrowseQueryStore`
  (`!string.IsNullOrWhiteSpace(Connection.Server)`), `QueryStorePlans`, `SelectedObjectContext`,
  all driven **statelessly** off `Connection` via `DatabaseContextService` on each call — there is
  no cached catalog to refresh or clear.
- `MainPage` — command-strip button `OnConnect` (`MainPage.xaml:89`) does capture-then-visualise
  via `ViewModel.CaptureAsync`; the empty-state panel has a second `OnConnect` entry
  (`MainPage.xaml:507`, "Capture from server").

Scope decisions from the earlier rework still hold: auth is **Entra MFA only** (Password /
Integrated / device-code / managed-identity / service-principal are out — connection-string mode
covers them); connection-string mode, Disconnect, and named profiles are in.

**Deferred, not dropped:** wiring the connection into catalog completions and the plan editor.
That work belongs to whenever the live-plan-editor effort lands on `main`; a one-line follow-up
task will add the `Connect`/`Disconnect` hooks into the completion providers then. It is out of
this plan because it cannot be built here.

## Ground rules

The loop prompt points here, which is why the loop prompt stays short. Everything binding lives in
this section.

- **Build:** `scripts/run-gated.sh dotnet build src/SqlPlanViz/SqlPlanViz.csproj`
- **Tests:** do not write tests; the build is the gate. Decided once, here. No test project is
  committed anywhere in this repo (`tests/SqlPlanViz.Tests` is gitignored and empty of source),
  `docs/tdd.md` §2 makes this a single-user personal tool, and every prior phase across the
  editor and highlighting work landed on a green build alone. Not a change in posture — do not
  re-open it mid-loop.
- **Model:** `sonnet` for loop iterations.
- **Context backstop:** `250000` — the safety net, not the trigger. Phases end sessions; this
  only catches a runaway task.
- **Branching:** work on `claude/interactive-connect`, cut from `main`. This repo's convention is
  one branch per effort off `main` — there is no `integration/*` or `dev` branch in use. The PR
  targets `main`. Merge only when the whole effort is reviewed.
- **UI changes:** WinUI 3 desktop app, no automated UI test harness, no `docs/screenshots/`
  convention in this repo — **skip the generic screenshot step**. A running instance locks
  `bin/` — kill it before the next build.
- **Manual verification posture (decided 2026-09-01, do not re-ask):** the loop has **no live
  SQL Server** and cannot drive the desktop UI. A task is ticked on a **green build** plus any
  check the loop *can* do without a server (code compiles, XAML parses, a launched app opens
  the dialog / collapses the right controls / does not crash). Every step that needs a live
  server — connecting, Query Store, object context, Entra MFA popups — is **not** done by the
  loop; instead each such step is appended to a **"Live-server verification pending"** list in
  `HANDOFF-interactive-connect.md`, for the user to run at the phase boundary. The loop still
  launches the app for the non-server checks where a task names them.
- **Out of scope:**
  - Entra auth modes other than interactive MFA (Password, Integrated, device code, managed
    identity, service principal) — the connection-string mode is the escape hatch for these.
  - Catalog-completion / plan-editor integration — deferred to when that infra reaches `main`
    (see Drift).
  - Multiple simultaneous connections / per-tab connections — `MainViewModel.Connection` stays a
    single shared instance.
  - Server discovery / "browse for servers" button.
  - LLM-assisted connection-error diagnosis.
  - A query-results grid — this app visualises plans, not rowsets.
  - Persistent MSAL token cache across restarts — deferred; see Open questions. Phase 2 records
    whether it is actually needed.
- **Already decided, do not re-litigate:**
  - Entra auth goes through `Microsoft.Data.SqlClient`'s existing `Authentication` /
    `SqlAuthenticationMethod` support — **not** a hand-rolled MSAL integration. MSAL is already in
    the dependency graph transitively via `Microsoft.Data.SqlClient` 5.2.2; no new package.
  - Nothing is persisted today. Phase 4 (recent connections) is the first write of connection
    info to disk; Phase 5 (remembered password) is the first secret written anywhere. Both are
    deliberately in scope, both strictly opt-in where a secret is involved, and Phase 5 uses
    Windows Credential Manager (`PasswordVault`) only — never plaintext disk storage.
  - `MainViewModel.Connection` is get-only; Disconnect mutates its fields via a new
    `ConnectionSettings.Reset()`, it cannot reassign the property.
  - The `ConnectView` `InfoBar` copy ("…never written to disk") becomes false once Phase 4 lands
    and is reworded as those phases go in — this is expected, not a question.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — Connect without capturing

A standalone Connect action plus a visible, tear-downable connection state. Nothing else can be
built until connecting and capturing are separate. ~6 tasks; task 1 already landed.

- [x] Add a connect-only mode to `ConnectView` (constructor flag or settable property) that
      collapses the `QueryBox` and the `ModeButtons` radio group and changes nothing else. Build
      gate only. *(Done — `ConnectView.ConnectOnly`.)*
- [x] Split capture from connect in `MainPage.xaml.cs`: rename the current `OnConnect` to
      `OnCapture` (keep its dialog title "Capture a plan from SQL Server", primary button
      "Capture", and its `view.Commit()` + `ViewModel.CaptureAsync(view.Query, view.Mode)` body).
      Write a new `OnConnect` that news up `ConnectView` with `ConnectOnly = true`, primary button
      "Connect", and on primary result calls `view.Commit()` then a new
      `ViewModel.NotifyConnectionChanged()` (raises `CanRerun`, `CanBrowseQueryStore` and any
      other `Connection`-derived flags) — **no** `CaptureAsync`. *(Live-server verify pending in
      handoff: Connect → Query Store lists plans with no plan captured.)*
- [x] Replace the command-strip "Capture" button (`MainPage.xaml:89`) with a "Connect" button
      (keep a glyph + the label "Connect", tooltip "Open a connection to a SQL Server") wired to
      the new `OnConnect`. Leave the empty-state panel button (`MainPage.xaml:507`, "Capture from
      server") wired to `OnCapture`. *(App launches clean; dialog click-through pending in
      handoff.)*
- [x] Add a connection status readout to the command strip — a `TextBlock` bound to
      `ViewModel.Connection.Describe()` (add a `ConnectionDescription` pass-through on the VM that
      `NotifyConnectionChanged()` raises, since `Describe()` is a method not a bindable property).
      *(`ConnectionReadout` TextBlock in the command strip, raised from `NotifyConnectionChanged`
      and after capture. App launches clean; server round-trip pending in handoff.)*
- [x] Add `ConnectionSettings.Reset()` (clears `Server`, `Database`, `UserId`, `Password`, resets
      `Auth` to `Windows`) and a "Disconnect" control next to the status readout that calls it,
      then clears connection-derived VM state: `QueryStorePlans.Clear()`,
      `SelectedObjectContext = null`, `QueryStoreMessage = null`, and `NotifyConnectionChanged()`.
      *(`MainViewModel.Disconnect()` + `IsConnected`; `DisconnectButton` shows only when
      connected. App launches clean; server round-trip pending in handoff.)*
- [x] Extend `ConnectionSettings.Describe()` to name the auth mode when connected (e.g.
      `server · db · Windows`, `server · db · SQL login`) and keep returning "Not connected" after
      `Reset()`. *(`AuthLabel` switch appended to `Describe()`. Readout text pending in handoff.)*

## Phase 2 — Microsoft Entra MFA

The headline auth requirement. No new package — one `SqlAuthenticationMethod` value and a parent
window handle for the popup. **This phase is at the 7-task ceiling** and its manual steps need a
real Entra-secured Azure SQL / Managed Instance target; if that target is unavailable, tasks 4–5
block and the phase should hand off part-done.

- [x] Extend the `AuthMode` enum (`src/SqlPlanViz/Capture/ConnectionSettings.cs`) with `EntraMfa`
      (Active Directory Interactive). Add a comment that Password / Integrated / device-code
      modes are deliberately deferred to the connection-string path. Build gate only.
      *(`AuthMode.EntraMfa` added with the deferral comment; `Describe()` `AuthLabel` maps it to
      "Microsoft Entra MFA". Build green.)*
- [x] Wire `EntraMfa` into `PlanCaptureService.BuildConnectionString`: set
      `SqlConnectionStringBuilder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive`,
      and do not populate `UserID` / `Password`. Build gate only.
      *(Auth `if/else` converted to a `switch`; `EntraMfa` sets `Authentication` only. Build green.)*
- [x] Add a "Microsoft Entra MFA" item to `AuthBox` in `ConnectView.xaml`; fix `OnAuthChanged`,
      `Commit()`, and the constructor's index↔`AuthMode` mapping for three items, so `SqlAuthPanel`
      shows only for `SqlLogin`. Manually verify: switch to Entra MFA, login/password fields hide;
      switch back to SQL auth, they return; Windows still shows neither.
      *(3rd ComboBoxItem added; `AuthToIndex`/`IndexToAuth` helpers replace the two-way ternaries.
      Build green, app launches clean. Interactive field-toggle check pending in handoff.)*
- [ ] Anchor the MFA popup to the app window — pass the app `HWND` (from `MainWindow`) into the
      interactive auth flow (`WithParentActivityOrWindow` via a custom `SqlAuthenticationProvider`
      registration, or the provider's parent-window hook). Manually verify against a real
      Entra-secured target: the popup appears anchored to the app window and auth succeeds via
      "Test connection".
- [ ] Manually verify each path end to end against the real target: Entra MFA connect from the
      new command-strip Connect button makes the Query Store browser live; capture-from-server
      with Entra MFA still produces a plan.
- [ ] Verify repeat connects within one session do not re-prompt for MFA (MSAL's in-memory
      cache), and record the observed behaviour as a comment near `AuthMode` in
      `ConnectionSettings.cs`. This is what decides whether the persistent-token-cache item in
      Open questions becomes a phase.
- [ ] Update the `ConnectView` `InfoBar` copy so it does not claim credentials are unused beyond
      the connection in a way that misleads for the interactive flow (tokens are cached in memory
      by MSAL for the session). Keep it one sentence. Build gate + visual check.

## Phase 3 — Connection-string mode

Paste a full ADO.NET string and connect verbatim — parity with "it works in SSMS, here's the
string", and the escape hatch for every auth/option combo the form does not model. **Deliberately
a short phase (4 tasks) — not padded.**

- [ ] Add `RawConnectionString` to `ConnectionSettings` and a mode toggle to `ConnectView`
      ("Enter details" / "Paste connection string") that swaps the form grid for a single
      multiline `ConnectionStringBox`. Constructor prefills it; `Commit()` persists the string and
      which mode is active. Manually verify the toggle shows/hides the right controls.
- [ ] Branch `PlanCaptureService.BuildConnectionString`: when `RawConnectionString` is non-empty,
      build `new SqlConnectionStringBuilder(raw)` (this validates and normalises), inject
      `ApplicationName` / `ConnectTimeout` only if absent, and return it; a parse failure becomes
      a `PlanCaptureException` with a readable message. Otherwise the existing field path. Build
      gate only.
- [ ] Make connection-string mode authoritative when active: `Commit()` records only
      `RawConnectionString`, and `Describe()` shows the `DataSource` / `InitialCatalog` parsed
      from it. Manually verify the status readout shows the right server/db after connecting via a
      pasted string.
- [ ] Manually verify: a known-good Entra string and a known-good SQL-auth string both connect
      via "Test connection"; a malformed string shows a clear error; toggling back to details
      mode restores form editing.

## Phase 4 — Remember recent connections

Stop retyping the server every session. First write of connection info to disk.

- [ ] Add a `RecentConnectionsStore` service that reads/writes a JSON file under the per-user
      local app-data folder (the app is **unpackaged** — use
      `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + an app subfolder, not
      `ApplicationData.Current`, which throws unpackaged — see Open questions). Up to 10 entries
      of `{ Server, Database, UserId, Auth }`, most-recent-first, de-duplicated by
      `Server` + `Database`. Never the password. Build gate only.
- [ ] Call the store from `ConnectView.Commit()` to record the current connection on every
      successful commit; skip or redact when connection-string mode is active. Build gate only.
- [ ] Change `ServerBox` and `DatabaseBox` from `TextBox` to `AutoSuggestBox`, sourced from the
      store and loaded when the dialog opens; selecting a suggestion prefills Database, Login, and
      Auth from the matching entry. Manually verify suggestions appear and prefill.
- [ ] Reword the `ConnectView` `InfoBar` to state what is now true — recent servers and logins
      are remembered on this PC, passwords are not. Build gate + visual check.
- [ ] Manually verify: connect to two different servers, relaunch the app, open Connect, both
      appear as suggestions and picking one prefills server/database/login/auth.

## Phase 5 — Optional password storage

The one piece that reverses "nothing persisted" — strictly opt-in, OS-backed, revocable.

- [ ] Add a "Remember password" `CheckBox` to `SqlAuthPanel` in `ConnectView.xaml`, visible for
      `SqlLogin`, unchecked by default. Build gate only.
- [ ] On `Commit()` when checked, write the password to
      `Windows.Security.Credentials.PasswordVault` keyed by `Server` + `UserId`; when unchecked,
      remove any existing entry for that key. Confirm `PasswordVault` works for an unpackaged app
      (see Open questions) as the first step of this task. Build gate only.
- [ ] When the dialog opens or a recent-connection suggestion is selected, if a vault credential
      exists for that `Server` + `UserId`, read it back and prefill `PasswordBox`. Manually
      verify prefill after relaunch.
- [ ] Add a "Forget saved password" button beside the checkbox, enabled only when a credential
      exists for the current `Server` + `UserId`, that removes it from the vault. Manually verify
      the next launch no longer prefills.
- [ ] Reword the `ConnectView` `InfoBar` once more to cover the opt-in stored-password case.
      Build gate + visual check.
- [ ] Manually verify end to end: check "Remember password", connect, relaunch, pick the server
      from suggestions, password prefills; "Forget saved password", relaunch, no prefill.

## Phase 6 — Named connection profiles

One-click reconnect to a named server config. The "reassess" item from the brainstorm — Phases
1–5 are the core; this can be dropped or deferred without stranding anything.

- [ ] Add a `ConnectionProfileStore` (separate persistence from the recent list) for user-named
      profiles holding the full config: Server, Database, Auth (incl. `EntraMfa`), Encrypt,
      TrustServerCertificate, UserId, and flags for "password is vaulted" and "is a raw
      connection string". Build gate only.
- [ ] Add a "Save as…" affordance to `ConnectView` that prompts for a profile name and writes the
      current form (or raw string) as a profile. Manually verify a profile is saved.
- [ ] Add a profile picker to `ConnectView` — selecting a profile loads every field, pulling the
      vaulted password when the flag is set. Manually verify a saved profile round-trips.
- [ ] Add rename + delete for profiles (inline list or a small secondary dialog). Manually verify
      rename and delete.
- [ ] Show saved profiles as one-click connect entries in the empty-state panel (the button
      stack around `MainPage.xaml:501`). Clicking one connects without opening the dialog.
      Manually verify.
- [ ] Manually verify end to end: create a prod (Entra MFA) profile and a local (SQL auth +
      remembered password) profile, relaunch, connect to each from both the dialog picker and the
      empty-state list.

## Open questions

- [ ] **Blocking for Phase 4 task 1 / Phase 5 task 2:** unpackaged WinUI 3 storage APIs.
      `ApplicationData.Current` and possibly `PasswordVault` throw or misbehave for an unpackaged
      app (`WindowsPackageType=None`). Phase 4 assumes a plain `LocalApplicationData` JSON file;
      Phase 5 task 2 must confirm `PasswordVault` is usable unpackaged (it generally is, but needs
      verifying on this target) or fall back to DPAPI (`ProtectedData`) over the same JSON store.
      Resolve inside the task, do not guess silently.
- [ ] **Non-blocking, decided by Phase 2 task 6:** persistent MSAL token cache across app
      restarts (`Microsoft.Identity.Client.Extensions.Msal`, already in the graph, via a custom
      `SqlAuthenticationProvider`). Becomes its own phase only if Phase 2 finds that MFA
      re-prompts on every launch and that is painful. Not in scope until then.
- [ ] **Non-blocking, follow-up after this effort:** once the live-plan-editor work reaches
      `main`, add a task to hook `Connect` / `Disconnect` into the catalog-completion providers
      and editor parser. Deliberately excluded from this plan because that infra is not on `main`.
