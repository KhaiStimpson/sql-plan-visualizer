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
  - `EntraMfa` selects `SqlAuthenticationMethod.ActiveDirectoryInteractive` (done, tasks 2–3).
  - **`Microsoft.Data.SqlClient` upgraded 5.2.2 → 6.1.2** (decided 2026-09-01, done in commit
    `e7a3afd`). Kept as a maintenance/security update; launches clean.
  - **The "no hand-rolled MSAL" rule is LIFTED** (decided 2026-09-01, superseding the original
    plan). `Microsoft.Data.SqlClient` exposes **no** parent-window hook for interactive auth at
    any version (verified by reflection over 6.1.2 and 7.0.1 — `ActiveDirectoryAuthenticationProvider`
    has no `SetParentActivityOrWindowFunc` and no parent-window ctor). The only way to anchor the
    MFA popup to the app window is a custom `SqlAuthenticationProvider` that calls MSAL
    (`Microsoft.Identity.Client`) directly with `.WithParentActivityOrWindow(() => hwnd)`. Phase 2
    task 4 builds exactly that. Adding an explicit `Microsoft.Identity.Client` package reference
    is now acceptable if the transitive one is not directly usable.
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

The headline auth requirement. `SqlAuthenticationMethod.ActiveDirectoryInteractive` (done), the
`Microsoft.Data.SqlClient` 6.1.2 bump (done), and a **custom `SqlAuthenticationProvider` over
MSAL** to anchor the popup to the app window (task 4 — the "no hand-rolled MSAL" rule was lifted
for this; see Ground rules). Manual steps need a real Entra-secured Azure SQL / Managed Instance
target; without it, tasks 4 (end-to-end), 5 and 6 go on the handoff pending list and the phase
hands off part-done.

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
- [x] Add a custom `SqlAuthenticationProvider` (`src/SqlPlanViz/Capture/InteractiveAuthProvider.cs`)
      for `SqlAuthenticationMethod.ActiveDirectoryInteractive` that calls MSAL directly:
      `PublicClientApplicationBuilder.Create(parameters.ClientId)` (fall back to the documented
      SqlClient client id `2fd908ad-0664-4344-b9be-cd3e8b574c38` / `a94f9c62-97fe-4d19-b06d-472bed8d2bcf`
      if `ClientId` is empty) `.WithAuthority(parameters.Authority).WithRedirectUri("http://localhost")`,
      then `AcquireTokenSilent` → on `MsalUiRequiredException` `AcquireTokenInteractive(scopes)`
      `.WithParentActivityOrWindow(() => _hwnd)` `.WithLoginHint(parameters.UserId)`, scopes =
      `{ parameters.Resource.TrimEnd('/') + "/.default" }`. Register it once at startup
      (`App`/`MainWindow`) via `SqlAuthenticationProvider.SetProvider(...)`, passing the app HWND
      from `MainWindow` (`WinRT.Interop.WindowNative.GetWindowHandle`). Add an explicit
      `Microsoft.Identity.Client` `PackageReference` if the transitive one is not directly usable.
      Build gate + clean `dotnet run` launch. Append to the handoff pending list: against a real
      Entra-secured target the popup appears anchored to the app window and "Test connection"
      succeeds. **If MSAL is not directly referenceable and adding the package pulls a large new
      subtree, or the provider signature differs from the above, stop and ask.**
      *(Done — `InteractiveAuthProvider` as specced; registered in `App.OnLaunched` via
      `InteractiveAuthProvider.Register(() => App.WindowHandle)`. `SqlAuthenticationParameters`
      has no `ClientId` in 6.1.2 so the documented SqlClient app id is always used. Explicit
      `Microsoft.Identity.Client` 4.73.1 ref = the version already transitive via `Azure.Identity`
      1.14.2, no new subtree. Build green, launches clean. Live-target popup-anchor check pending
      in handoff.)*
- [ ] Manually verify each path end to end against the real target: Entra MFA connect from the
      new command-strip Connect button makes the Query Store browser live; capture-from-server
      with Entra MFA still produces a plan.
- [ ] Verify repeat connects within one session do not re-prompt for MFA (MSAL's in-memory
      cache), and record the observed behaviour as a comment near `AuthMode` in
      `ConnectionSettings.cs`. This is what decides whether the persistent-token-cache item in
      Open questions becomes a phase.
- [x] Update the `ConnectView` `InfoBar` copy so it does not claim credentials are unused beyond
      the connection in a way that misleads for the interactive flow (tokens are cached in memory
      by MSAL for the session). Keep it one sentence. Build gate + visual check.
      *(Done — now "Credentials are used for this connection only and are never written to disk,
      though Microsoft Entra sign-in tokens stay cached in memory until the app closes." One
      sentence. Build green, app launches clean.)*

## Phase 3 — Connection-string mode

Paste a full ADO.NET string and connect verbatim — parity with "it works in SSMS, here's the
string", and the escape hatch for every auth/option combo the form does not model. **Deliberately
a short phase (4 tasks) — not padded.**

- [x] Add `RawConnectionString` to `ConnectionSettings` and a mode toggle to `ConnectView`
      ("Enter details" / "Paste connection string") that swaps the form grid for a single
      multiline `ConnectionStringBox`. Constructor prefills it; `Commit()` persists the string and
      which mode is active. Manually verify the toggle shows/hides the right controls.
      *(`ConnectionSettings.RawConnectionString` + `UseConnectionString` (both cleared by
      `Reset()`). `ConnectView`: `EntryModeBox` ComboBox toggles `DetailsPanel` (wraps the form
      controls) vs `ConnectionStringBox` (collapsed multiline TextBox) via `ApplyEntryMode()`;
      constructor prefills both and calls `ApplyEntryMode()`; `Commit()` persists string + mode.
      Build green, app launches clean. Interactive toggle show/hide check pending in handoff.)*
- [x] Branch `PlanCaptureService.BuildConnectionString`: when `RawConnectionString` is non-empty,
      build `new SqlConnectionStringBuilder(raw)` (this validates and normalises), inject
      `ApplicationName` / `ConnectTimeout` only if absent, and return it; a parse failure becomes
      a `PlanCaptureException` with a readable message. Otherwise the existing field path. Build
      gate only.
      *(New `BuildFromRawConnectionString(raw)` helper, called first thing in
      `BuildConnectionString` when the raw string is non-empty. `new SqlConnectionStringBuilder(raw)`
      wrapped in try/catch (`ArgumentException`/`FormatException`/`KeyNotFoundException` →
      `PlanCaptureException`). `ApplicationName`/`ConnectTimeout` set only when
      `builder.ContainsKey` is false. Build green.)*
- [x] Make connection-string mode authoritative when active: `Commit()` records only
      `RawConnectionString`, and `Describe()` shows the `DataSource` / `InitialCatalog` parsed
      from it. Manually verify the status readout shows the right server/db after connecting via a
      pasted string.
      *(`ConnectView.Commit()` in raw mode now calls `_settings.Reset()` then sets only
      `UseConnectionString` + `RawConnectionString`; details mode clears both. `Describe()` gains
      `DescribeRawConnectionString()` — parses via `SqlConnectionStringBuilder`, returns
      `DataSource · InitialCatalog · connection string` (or "Connection string" on parse
      failure / no DataSource). Build green, app launches clean. Readout-after-connect check
      pending in handoff.)*
- [ ] Manually verify: a known-good Entra string and a known-good SQL-auth string both connect
      via "Test connection"; a malformed string shows a clear error; toggling back to details
      mode restores form editing.

## Phase 4 — Remember recent connections

Stop retyping the server every session. First write of connection info to disk.

- [x] Add a `RecentConnectionsStore` service that reads/writes a JSON file under the per-user
      local app-data folder (the app is **unpackaged** — use
      `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + an app subfolder, not
      `ApplicationData.Current`, which throws unpackaged — see Open questions). Up to 10 entries
      of `{ Server, Database, UserId, Auth }`, most-recent-first, de-duplicated by
      `Server` + `Database`. Never the password. Build gate only.
      *(`src/SqlPlanViz/Capture/RecentConnectionsStore.cs` — `RecentConnection` record
      (Server/Database/UserId/Auth, no password) + `RecentConnectionsStore`. File at
      `%LOCALAPPDATA%\SqlPlanViz\recent-connections.json` via
      `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`; no `ApplicationData.Current`.
      `Load()` (missing/corrupt → empty, never throws), `Record()` (front-insert, dedup by
      Server+Database OrdinalIgnoreCase, cap 10, blank server ignored), `Save()` (creates the
      subfolder, swallows IO failures). Enum persisted as string. Ctor overload takes an explicit
      path for exercising. Build green; read/write/dedup/cap-10/most-recent-first reasoned through
      — no server needed.)*
- [x] Call the store from `ConnectView.Commit()` to record the current connection on every
      successful commit; skip or redact when connection-string mode is active. Build gate only.
      *(`ConnectView` gains a `RecentConnectionsStore _recent = new()`; the details-mode path of
      `Commit()` ends with `_recent.Record(new RecentConnection(Server, Database, UserId, Auth))`
      (no-ops on blank server, caps at 10). Connection-string mode returns early before the
      Record call — comment notes the pasted string may embed a password. Build green.)*
- [x] Change `ServerBox` and `DatabaseBox` from `TextBox` to `AutoSuggestBox`, sourced from the
      store and loaded when the dialog opens; selecting a suggestion prefills Database, Login, and
      Auth from the matching entry. Manually verify suggestions appear and prefill.
      *(Both boxes are now `AutoSuggestBox` (`.Text` API unchanged, so `Commit()` is untouched).
      Constructor calls `_recent.Load()` into `_recentConnections` and seeds `ServerBox.ItemsSource`.
      `OnServerTextChanged`/`OnDatabaseTextChanged` refilter on `UserInput` (`DistinctServers` /
      `DistinctDatabases`, the latter narrowed to the typed server). `OnServerSuggestionChosen`
      looks up the matching `RecentConnection` and prefills Database + Login + Auth. Build green,
      app launches clean. Real suggestion/prefill round-trip on the pending list — needs entries
      written by an actual connect.)*
- [x] Reword the `ConnectView` `InfoBar` to state what is now true — recent servers and logins
      are remembered on this PC, passwords are not. Build gate + visual check.
      *(Now "Recent servers and logins are remembered on this PC, but passwords are never written
      to disk, though Microsoft Entra sign-in tokens stay cached in memory until the app closes."
      One sentence; keeps the Phase 2 task 7 Entra-token clause. Build green.)*
- [ ] Manually verify: connect to two different servers, relaunch the app, open Connect, both
      appear as suggestions and picking one prefills server/database/login/auth.

## Phase 5 — Optional password storage

The one piece that reverses "nothing persisted" — strictly opt-in, OS-backed, revocable.

- [x] Add a "Remember password" `CheckBox` to `SqlAuthPanel` in `ConnectView.xaml`, visible for
      `SqlLogin`, unchecked by default. Build gate only.
      *(`SqlAuthPanel` changed from `Grid` to `StackPanel` wrapping the login/password `Grid` plus a
      new `RememberPasswordBox` `CheckBox` (unchecked by default). `OnAuthChanged` still toggles
      `SqlAuthPanel.Visibility` so the checkbox shows only for `SqlLogin` (index 1). Build green.)*
- [x] On `Commit()` when checked, write the password to
      `Windows.Security.Credentials.PasswordVault` keyed by `Server` + `UserId`; when unchecked,
      remove any existing entry for that key. Confirm `PasswordVault` works for an unpackaged app
      (see Open questions) as the first step of this task. Build gate only.
      *(**Open question RESOLVED — `PasswordVault` is used, not DPAPI.** A throwaway console app on
      the same TFM (`net8.0-windows10.0.19041.0`, unpackaged) did `vault.Add` → `Retrieve` →
      `RetrievePassword` → `Remove` and the round-trip succeeded, so no package identity is needed
      here and the DPAPI `ProtectedData` fallback was not required. Recorded in a class comment on
      `PasswordVaultStore`. New `src/SqlPlanViz/Capture/PasswordVaultStore.cs` —
      `Save`/`Remove`/`Retrieve`/`Has` keyed by resource `"SqlPlanViz"` + account `Server|UserId`,
      every path try/catch so a vault failure never breaks connecting. `ConnectView.Commit()`
      details path: for `SqlLogin` with `RememberPasswordBox` checked → `Save`; otherwise (or any
      other auth) → `Remove`. Build green; credential write/read/delete verified via the snippet.)*
- [x] When the dialog opens or a recent-connection suggestion is selected, if a vault credential
      exists for that `Server` + `UserId`, read it back and prefill `PasswordBox`. Manually
      verify prefill after relaunch.
      *(New `TryPrefillPassword(server, userId)` — on a hit sets `PasswordBox.Password` and ticks
      `RememberPasswordBox` so a re-commit keeps the entry. Called from the constructor (with the
      incoming `settings`) and from `OnServerSuggestionChosen` (with the matched `RecentConnection`).
      Build green. Prefill-after-real-relaunch on the live-server pending list.)*
- [x] Add a "Forget saved password" button beside the checkbox, enabled only when a credential
      exists for the current `Server` + `UserId`, that removes it from the vault. Manually verify
      the next launch no longer prefills.
      *(`ForgetPasswordButton` added next to `RememberPasswordBox`, `IsEnabled="False"` by default.
      `UpdateForgetPasswordState()` sets `IsEnabled = _passwords.Has(ServerBox.Text, UserBox.Text)`
      and is called from `OnServerTextChanged`, the new `OnUserTextChanged`, and after a prefill
      hit. `OnForgetPassword` calls `_passwords.Remove`, clears `PasswordBox`, unchecks the box,
      re-evaluates. Build green. No-prefill-after-relaunch on the live pending list.)*
- [x] Reword the `ConnectView` `InfoBar` once more to cover the opt-in stored-password case.
      Build gate + visual check.
      *(Now: "Recent servers and logins are remembered on this PC, a SQL password is never written
      to disk and is kept only in Windows Credential Manager when you tick &quot;Remember
      password&quot;, and Microsoft Entra sign-in tokens stay cached in memory until the app
      closes." One sentence. Build green, app launches clean.)*
- [ ] Manually verify end to end: check "Remember password", connect, relaunch, pick the server
      from suggestions, password prefills; "Forget saved password", relaunch, no prefill.

## Phase 6 — Named connection profiles

One-click reconnect to a named server config. The "reassess" item from the brainstorm — Phases
1–5 are the core; this can be dropped or deferred without stranding anything.

- [x] Add a `ConnectionProfileStore` (separate persistence from the recent list) for user-named
      profiles holding the full config: Server, Database, Auth (incl. `EntraMfa`), Encrypt,
      TrustServerCertificate, UserId, and flags for "password is vaulted" and "is a raw
      connection string". Build gate only.
      *(`src/SqlPlanViz/Capture/ConnectionProfileStore.cs` — `ConnectionProfile` record
      (Name, Server, Database, Auth, UserId, Encrypt, TrustServerCertificate, PasswordIsVaulted,
      IsRawConnectionString, RawConnectionString — no password) + `ConnectionProfileStore`. JSON at
      `%LOCALAPPDATA%\SqlPlanViz\connection-profiles.json` via
      `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`, enum as string. `Load()`
      (name-ordered, never throws), `Get(name)`, `Save(profile)` (replace-by-name), `Rename(old,new)`
      (no-ops on missing source / blank / colliding name), `Delete(name)`; all writes swallow
      IO failures. Explicit-path ctor overload. Build green.)*
- [x] Add a "Save as…" affordance to `ConnectView` that prompts for a profile name and writes the
      current form (or raw string) as a profile. Manually verify a profile is saved.
      *(`ProfileNameBox` TextBox + "Save as profile" button (`OnSaveProfile`) added above the
      InfoBar, with a `ProfileResult` status TextBlock. `OnSaveProfile` requires a non-blank name,
      calls `Commit()` then `_profiles.Save(BuildProfile(name))`. `BuildProfile` snapshots the
      just-committed `_settings` — `PasswordIsVaulted` = SqlLogin + "Remember password" checked,
      `IsRawConnectionString`/`RawConnectionString` from the entry mode. Build green, app launches
      clean. `ConnectionProfileStore` exercised in isolation: empty-load, save, name-ordered load,
      case-insensitive Get + enum round-trip, save-replaces-by-name, rename, rename-collision-noop,
      delete, blank-name-ignored all pass.)*
- [x] Add a profile picker to `ConnectView` — selecting a profile loads every field, pulling the
      vaulted password when the flag is set. Manually verify a saved profile round-trips.
      *(`ProfileBox` ComboBox ("Load a saved profile") at the top of the view, populated by
      `RefreshProfiles()` in the ctor and after each save. `OnProfileSelected` loads a raw-string
      profile into connection-string mode, else switches to details mode and sets
      Server/Database/Login/Encrypt/Trust/Auth, calls `ApplyEntryMode()`, and when
      `PasswordIsVaulted` calls `TryPrefillPassword`. Build green, app launches clean.)*
- [x] Add rename + delete for profiles (inline list or a small secondary dialog). Manually verify
      rename and delete.
      *(Inline "Rename" / "Delete" buttons under `ProfileBox`. `OnRenameProfile` renames the
      selected profile to the text in `ProfileNameBox` (reports failure when the store rejects a
      colliding name); `OnDeleteProfile` deletes the selected profile; both `RefreshProfiles()` and
      report via `ProfileResult`. Store-level rename/delete/collision behaviour already exercised
      in the t2 isolation run. Build green, app launches clean.)*
- [x] Show saved profiles as one-click connect entries in the empty-state panel (the button
      stack around `MainPage.xaml:501`). Clicking one connects without opening the dialog.
      Manually verify.
      *(`ProfilesPanel` (an "Or connect to a saved profile" label + an `ItemsControl` of buttons)
      added under the empty-state button row, bound to a new
      `MainPage.ConnectionProfiles` `ObservableCollection<ConnectionProfile>`. `RefreshConnectionProfiles()`
      reloads it (ctor + after every Connect/Capture dialog commit) and shows/hides the panel by
      count. `OnConnectProfile` resolves the button's `DataContext`, pulls the vaulted password via
      `PasswordVaultStore` when `PasswordIsVaulted`, calls new `ConnectionSettings.ApplyProfile(profile,
      password)` then `ViewModel.NotifyConnectionChanged()` — no dialog. `ConnectionProfile` had to
      become a mutable class (was a positional record) so the WinUI XAML type-info generator, which
      assigns each property, compiles. Build green, app launches clean; store `Load` round-trip
      re-exercised.)*
- [ ] Manually verify end to end: create a prod (Entra MFA) profile and a local (SQL auth +
      remembered password) profile, relaunch, connect to each from both the dialog picker and the
      empty-state list.

## Open questions

- [x] **Blocking for Phase 4 task 1 / Phase 5 task 2:** unpackaged WinUI 3 storage APIs.
      *(RESOLVED. Phase 4: plain `LocalApplicationData` JSON file works. Phase 5 task 2:
      `PasswordVault` verified working unpackaged on this target — used directly, no DPAPI fallback.)*
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
