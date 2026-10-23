# AI incident log

What went wrong when agents wrote this codebase, how it was caught, and what guardrail was added
so it cannot happen the same way again.

This is the feedback loop that makes the rest of the process worth anything. A failure that is
only fixed is a failure that will come back in the next session; a failure that produces a
guardrail does not.

**Write an entry when:**

- an agent produced something wrong that reached review, CI or further,
- a guardrail (hook, agent, rule in a `CLAUDE.md`, scope list) fired and saved you,
- an agent reported a task as done when it was not,
- the same kind of mistake has now happened twice.

**Do not write an entry for** an ordinary bug an agent found and fixed inside its own turn. That
is the process working.

Rule of thumb for the guardrail column: if the answer is "tell the agent to be more careful", it
is not a guardrail. A guardrail is a test, a hook, a scope restriction, a checklist line, or a
sentence added to a `CLAUDE.md` that an agent must read before it can go wrong.

New reusable agents are added only when this log shows a recurring need (`PLAN.md` §7).

---

## Entry template

```markdown
### YYYY-MM-DD — <one-line title>

- **Task / session:** 012-portal-upload, Sonnet, worktree `portal-upload`
- **What happened:** what the agent did, concretely. Quote the code or the claim.
- **How it was caught:** hook / CI / security-reviewer / human review / only noticed later
- **Why it happened:** the missing context, ambiguous instruction, or wrong assumption — not
  "the model hallucinated"
- **Blast radius:** caught before commit / in CI / reached main / would have shipped
- **Guardrail added:** the test, hook, scope rule or CLAUDE.md line. Link the commit.
- **Still open:** anything not fixed by the guardrail
```

---

## Log

### 2026-10-23 — The Stop hook failed the turn on a documentation-only change

- **Task / session:** Phase 0a, Opus, main worktree
- **What happened:** the hook fired on its first real run and blocked the turn with
  `MSBUILD : error MSB1009: Project file does not exist. Switch: api/FileInviteLite.sln`. Two
  defects: it decided "the backend changed" from any path under `api/`, which included the newly
  written `api/CLAUDE.md`; and it ran `dotnet test` without checking that the solution exists,
  which it does not until the Phase 0b scaffold.
- **How it was caught:** the hook itself, on the turn that created it. Nothing reached review.
- **Why it happened:** the hook was written against the repository as it will look after Phase 0b
  and tested only for shell syntax, not against the repository as it is now. "Changed under
  `api/`" was treated as a proxy for "changed backend code", which is wrong for every `.md` file
  in the tree.
- **Blast radius:** caught before commit. Cost was one blocked turn.
- **Guardrail added:** the hook now filters `*.md`, `*.txt`, `docs/**` and `.github/**` out of the
  changed-file list before deciding what to run, and skips each half unless
  `api/FileInviteLite.sln` / `web/package.json` actually exists. Verified by running the hook
  against the current tree (exit 0) and against a sample file list.
- **Still open:** nothing. Closed during Phase 0b: with a deliberately failing xUnit assertion in
  place the hook exited 2 and named the failing test, and with `stop_hook_active: true` it exited
  0 without running anything, so it cannot loop. The temporary test was removed afterwards.

### 2026-10-23 — The Stop hook reported two failures and said nothing about either

- **Task / session:** Phase 0b (web scaffold), fork of the Opus 0b session, shared checkout
- **What happened:** the hook blocked a turn with `npm run typecheck failed` and
  `npm run test failed`, and no output under either heading. Unreadable as a report: an empty
  failure looks like a broken test when it is just as likely a missing script or an unfinished
  install.
- **How it was caught:** a human asked what the failure meant, which it was not possible to answer
  from the hook's own output.
- **Why it happened:** the hook invoked npm with `--silent`, which suppresses npm's error text
  entirely. Reproduced directly: `npm run <missing-script> --silent` exits 1 having written zero
  bytes, while the same command without `--silent` prints `npm error Missing script: …`. The flag
  was added to keep the output tidy, and it removed the only diagnostic the report had.
- **Blast radius:** caught before commit. Cost was one blocked turn and one unanswerable question.
- **Guardrail added:** `--silent` dropped; every failure now carries its exit code; a failure with
  no output says so explicitly rather than printing an empty section; and the web checks are
  skipped unless `web/node_modules/.package-lock.json` exists, so a partial install cannot be
  reported as a code failure. Verified by reproducing both npm behaviours side by side and by
  exercising the empty-output path against a command that exits non-zero silently.
- **Still open:** what actually made `typecheck` and `test` fail is still unknown. Those files were
  being written by another session in the same checkout at the time, so re-running the checks here
  would have fought that session for the same build outputs. The next run of the hook in that
  session will now say.

---

## Recurring patterns

Fill this in as the log grows — three entries that rhyme are worth a rule of their own.

| Pattern | Entries | Standing guardrail |
|---|---|---|
| A guardrail written against the repository as it *will* look, verified only for syntax, and wrong about the repository as it *is* | 2026-10-23 (×2, both the Stop hook) | Before a hook or script is committed, run it against the current tree and against a tree where the thing it inspects is missing. `bash -n` is not a test. |
| Tool output suppressed for tidiness, removing the only diagnostic a failure had | 2026-10-23 | Never pass `--silent`, `-q` or `>/dev/null` to a command whose failure is meant to be read by someone. Report the exit code always. |
