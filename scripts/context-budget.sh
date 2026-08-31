#!/bin/sh
# D2b - the context backstop. NOT the trigger.
#
# Phase boundaries end sessions (phase-boundary.sh). This catches the case a
# phase boundary structurally cannot: a single runaway task. In the measured
# corpus one loop iteration burned 257 turns and $124 inside one task, and no
# boundary would have interrupted it.
#
# It should almost never fire - phase-boundary.sh now makes the same check on the
# path the loop prompt walks, so this only catches a loop that never calls it.
# A session that trips it is evidence that the phase was sized wrong, which is
# worth saying out loud in the handoff.
#
# Like phase-boundary.sh, it speaks on STDERR with exit 2, because a `Stop` hook's
# stdout on exit 0 never reaches the model. The marker keeps it to once per
# session, so a session that is over budget can still end.
. "$(dirname "$0")/_common.sh"

slug=$(effort_slug) || exit 0
[ -n "$slug" ] || exit 0

plan=$(plan_file "$slug")

# Threshold from the plan's Ground rules, else the 250K default - the measured
# cost knee. CLAUDE_TRANSCRIPT_PATH is honoured when the harness supplies one;
# the mtime scan in transcript_file is the path that was actually verified.
budget=$(context_over_budget "$plan") || exit 0
ctx=${budget% *}
limit=${budget#* }

[ -f .flow/over-budget ] && exit 0
: > .flow/over-budget

{
  printf 'flow: CONTEXT BACKSTOP - this session is at %s, over its %s budget.\n' "$(fmt_k "$ctx")" "$(fmt_k "$limit")"
  printf '  A phase boundary should have ended this session before now, so the current\n'
  printf '  phase was sized wrong. Finish ONLY the task in hand, then:\n'
  printf '    1. Commit it.\n'
  printf '    2. Update HANDOFF-%s.md, and note there that this phase overran its budget.\n' "$slug"
  printf '    3. Stop the loop and run /flow:loop to continue on fresh context.\n'
} >&2
exit 2
