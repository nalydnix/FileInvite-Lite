#!/usr/bin/env bash
# Stop hook: run the checks that are relevant to what changed in this working tree.
#
# Why this exists: an agent that believes it is finished is the moment a regression is cheapest to
# catch. This hook makes "I'm done" mean "the tests that cover what I touched are green", without
# waiting for CI.
#
# Behaviour:
#   - decides what to run from `git status` / `git diff`, so a web-only change does not pay for a
#     dotnet build and vice versa;
#   - exit 2 feeds stderr back to the agent and stops it from ending the turn, so it sees the
#     failure and fixes it;
#   - exit 0 lets the turn end.
#
# It is deliberately conservative: when it cannot tell what changed, or the toolchain is missing,
# it gets out of the way rather than blocking on a false signal.

set -uo pipefail

payload="$(cat 2>/dev/null || true)"

# Claude Code sets stop_hook_active when the agent was resumed by this same hook. Running again
# would loop forever, so stand down and let the agent report.
if printf '%s' "$payload" | grep -q '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  exit 0
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
cd "$repo_root" || exit 0

# Files changed in the working tree, plus anything committed on this branch but not on main.
changed="$(
  {
    git status --porcelain=v1 2>/dev/null | cut -c4-
    base="$(git merge-base HEAD main 2>/dev/null)"
    [ -n "$base" ] && git diff --name-only "$base"...HEAD 2>/dev/null
  } | sed 's/^"//; s/"$//' | sort -u
)"

[ -z "$changed" ] && exit 0

# Documentation is not code. A CLAUDE.md or a task file lives under api/ or web/ but changing it
# cannot break a test, and running the suite for it wastes a minute of every turn.
changed="$(printf '%s\n' "$changed" | grep -vEi '\.(md|txt)$|^docs/|^\.github/')"

[ -z "$changed" ] && exit 0

api_changed=0
web_changed=0
printf '%s\n' "$changed" | grep -q '^api/' && api_changed=1
printf '%s\n' "$changed" | grep -q '^web/' && web_changed=1

if [ "$api_changed" -eq 0 ] && [ "$web_changed" -eq 0 ]; then
  exit 0   # nothing in either half changed
fi

# Before the Phase 0b scaffold exists there is nothing to build. Say nothing rather than failing
# the turn on a missing project file.
[ -f api/FileInviteLite.sln ] || api_changed=0
[ -f web/package.json ]       || web_changed=0

if [ "$api_changed" -eq 0 ] && [ "$web_changed" -eq 0 ]; then
  exit 0
fi

failures=""
run() {
  local label="$1"; shift
  local out
  if ! out="$("$@" 2>&1)"; then
    failures="${failures}
=== ${label} failed ===
$(printf '%s' "$out" | tail -n 60)
"
  fi
}

if [ "$api_changed" -eq 1 ] && command -v dotnet >/dev/null 2>&1; then
  # Narrow to the touched feature's tests when exactly one feature changed; otherwise run the lot.
  features="$(printf '%s\n' "$changed" \
    | sed -n 's#^api/\(src\|tests\)/[^/]*/Features/\([^/]*\)/.*#\2#p' \
    | sort -u)"
  if [ "$(printf '%s\n' "$features" | grep -c .)" = "1" ] && [ -n "$features" ]; then
    run "dotnet test --filter $features" \
      dotnet test api/FileInviteLite.sln --nologo -v q \
        --filter "FullyQualifiedName~${features}"
  else
    run "dotnet test" dotnet test api/FileInviteLite.sln --nologo -v q
  fi
fi

if [ "$web_changed" -eq 1 ] && [ -d web/node_modules ] && command -v npm >/dev/null 2>&1; then
  run "npm run typecheck" npm --prefix web run typecheck --silent
  run "npm run test"      npm --prefix web run test --silent
fi

if [ -n "$failures" ]; then
  {
    echo "Checks failed for the files you changed. Fix these before finishing the task."
    echo "Do not weaken, skip or delete a test to get to green (CLAUDE.md rule 4)."
    echo "$failures"
  } >&2
  exit 2
fi

exit 0
