# Interactive database connections — plan

Status: not started · Branch: `claude/interactive-connect` · Written: 2026-08-22 · Reworked: 2026-09-01

## Goal

Connecting to a SQL Server stops being welded to capturing a plan, and gains near-SSMS parity for
getting *in*. Concretely, when this is done:

- A **Connect** button replaces **Capture** in the command strip. It opens a connection with no
  query in hand and wires the editor, catalog completions, and Query Store to it. Capturing a
  plan from a live query is still reachable (empty-state panel, editor re-plan) — the button
  goes, the capability does not.
- A connection **status readout** in the command strip always shows which server/database/auth is
  live, with a **Disconnect** that tears the connection and its catalog state back down.
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

## Drift from the 2026-08-22 draft

Re-read against the tree on 2026-09-01. The base connection code is essentially unchanged since
the draft, so almost nothing has rotted — but scope moved:

- `ConnectionSettings.Describe()` **already exists** (`src/SqlPlanViz/Capture/ConnectionSettings.cs:35`),
  so the Phase 1 status-readout task is lighter than the draft assumed — bind, don't build.
- `MainViewModel.Connection` is a **get-only single instance** (`MainViewModel.cs:155`,
  `public ConnectionSettings Connection { get; } = new();`). Disconnect must mutate its fields or
  call a new `ConnectionSettings.Reset()` — it cannot reassign the property.
- The command-strip button today is labelled **"Capture"** (`MainPage.xaml:89`) and its handler
  `OnConnect` (`MainPage.xaml.cs:584`) does capture-then-refresh. The empty-state panel has a
  second entry point, `OnConnect` again, labelled "Capture from server" (`MainPage.xaml:477`).
- **Scope change (this rework):** auth is now **Entra MFA only** — `EntraPassword` and
  `EntraIntegrated` from the draft's Phase 2, plus device-code / managed-identity / service
  principal, are **out** (the connection-string mode covers those cases). **Connection-string
  mode** (new — draft had none), **Disconnect** (new), and **named profiles** (new) are added.
- `AuthMode` enum still has only `Windows`, `SqlLogin`. The settings property is `Auth`; the enum
  is `AuthMode`. `CommandTimeoutSeconds` defaults to 60.
- The branch `claude/interactive-connect` was never cut — editor work continued on other
  `claude/*` branches. This effort still gets its own branch off `main`.

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
  one branch per effort off `main` — there is no `integration/*` or `dev` branch in use. Merge to
  `main` only when the whole effort is reviewed.
- **UI changes:** WinUI 3 desktop app, no automated UI test harness, no `docs/screenshots/`
  convention in this repo — **skip the generic screenshot step**. Instead, any task that changes
  `ConnectView`, `MainPage`, or adds a dialog must be manually exercised with
  `dotnet run --project src/SqlPlanViz` before it is ticked. Each such task's description says
  what to click through.
- **Out of scope:**
  - Entra auth modes other than interactive MFA (Password, Integrated, device code, managed
    identity, service principal) — the connection-string mode is the escape hatch for these.
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
  - The `ConnectView` `InfoBar` copy ("…never written to disk") is now false once Phase 4 lands
    and must be reworded as those phases go in — this is expected, not a question.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — Connect without capturing

A standalone Connect action plus a visible, tear-downable connection state. Nothing else can be
built until connecting and capturing are separate.

- [x] Add a connect-only mode to `ConnectView` (constructor flag or settable property) that
      collapses the `QueryBox` and the `ModeButtons` radio group and changes nothing else. Build
      gate only.
- [ ] Add an `OnConnect` handler in `MainPage` distinct from capture: rename the current
      `OnConnect` to `OnCapture` (keep its dialog title "Capture a plan from SQL Server" and its
      `CaptureAsync` call), and write a new `OnConnect` that opens `ConnectView` in connect-only
      mode, primary button "Connect", calls `view.Commit()` then `RefreshCatalogAsync(forceRefresh: false)`
      with **no** `CaptureAsync`. Manually verify: launch, Connect to a real server with no plan
      loaded, confirm catalog/completion providers pick up the schema.
- [ ] Replace the command-strip "Capture" button (`MainPage.xaml:89`) with a "Connect" button
      (glyph + label) wired to the new `OnConnect`. Relabel the empty-state panel button
      (`MainPage.xaml:477`) to keep capture-from-server reachable, wired to `OnCapture`. Manually
      verify: both buttons open their correct dialog and complete.
- [ ] Add a connection status readout to the command strip — a `TextBlock` bound to
      `ViewModel.Connection.Describe()`, refreshed after both the Connect and Capture flows.
      Manually verify: connect, readout updates; capture against a *different* server, readout
      updates again.
- [ ] Add a `ConnectionSettings.Reset()` and a "Disconnect" control next to the status readout
      that calls it, then clears catalog state (`CatalogSnapshot.Empty` into `_catalogProvider` /
      `_tuningProvider`, reset the editor parser to `TSqlParserFactory` default). Manually verify:
      connect, disconnect, confirm completions stop offering schema and run-actual disables.
- [ ] Extend `ConnectionSettings.Describe()` to name the auth mode when connected (e.g.
      `server · db · Windows`) and read "Not connected" when `Reset()`. Manually verify the
      readout text in each state.

## Phase 2 — Microsoft Entra MFA

The headline auth requirement. No new package — one `SqlAuthenticationMethod` value and a parent
window handle for the popup. **This phase is at the 7-task ceiling** and its manual steps need a
real Entra-secured Azure SQL / Managed Instance target; if that target is unavailable, tasks 4–5
block and the phase should hand off part-done.

- [ ] Extend the `AuthMode` enum (`src/SqlPlanViz/Capture/ConnectionSettings.cs`) with `EntraMfa`
      (Active Directory Interactive). Add a comment that Password / Integrated / device-code
      modes are deliberately deferred to the connection-string path. Build gate only.
- [ ] Wire `EntraMfa` into `PlanCaptureService.BuildConnectionString`: set
      `SqlConnectionStringBuilder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive`,
      and do not populate `UserID` / `Password`. Build gate only.
- [ ] Add a "Microsoft Entra MFA" item to `AuthBox` in `ConnectView.xaml`; fix `OnAuthChanged`,
      `Commit()`, and the constructor's index↔`AuthMode` mapping for three items, so `SqlAuthPanel`
      shows only for `SqlLogin`. Manually verify: switch to Entra MFA, login/password fields hide;
      switch back, they return.
- [ ] Anchor the MFA popup to the app window — pass the app `HWND` (from `MainWindow`) into the
      interactive auth flow (`WithParentActivityOrWindow` via a custom `SqlAuthenticationProvider`
      registration, or the provider's parent-window hook). Manually verify against a real
      Entra-secured target: the popup appears anchored to the app window and auth succeeds via
      "Test connection".
- [ ] Manually verify each path end to end against the real target: Entra MFA connect from the
      new command-strip Connect button reaches the catalog; capture-from-server with Entra MFA
      still works.
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
- [ ] Show saved profiles as one-click connect entries in the empty-state panel
      (`MainPage.xaml:449`). Clicking one connects without opening the dialog. Manually verify.
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
