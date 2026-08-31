# Handoff — interactive-connect

Branch: `claude/interactive-connect` (cut fresh off `main` this session; goes in its own PR).

## Done this session
- Phase 1 task 1: `ConnectView.ConnectOnly` settable property that collapses `QueryBox` and
  `ModeButtons`. Build green.

## Next task
Phase 1 task 2 — split `OnConnect` from capture in `MainPage` (rename current handler to
`OnCapture`, add a new connect-only `OnConnect`). Has a manual UI verification step against a
real server.

## Do not re-litigate
- No test project — build is the gate (plan Ground rules).
- Entra via `Microsoft.Data.SqlClient` `Authentication`, no hand-rolled MSAL, no new package.
- Single shared `MainViewModel.Connection` instance.

## Open questions
None outstanding.
