# 032: Runbook, simulated incident, README
**Phase:** 3   **Depends on:** Phase 2 merged   **Parallel-safe with:** 030, 031 (it touches no code)

## Goal
The documents a person needs to operate, demonstrate and understand the prototype.

## Scope

**May touch**
- `docs/runbook.md` — new
- `docs/incidents/0001-<slug>.md` — new, the simulated incident write-up
- `README.md`
- `docs/ai-log.md` — appending entries only

**Must not touch**
Any file under `api/`, `web/`, `.github/` or `scripts/`. **This task writes no code.** If
documenting something reveals a bug, record it in Progress notes and report it; do not fix it here.

## Decisions (already made — implement, do not revisit)

- **The runbook is written for someone who did not build this** and is looking at it during an
  incident. It is a list of procedures, not prose about architecture — that is
  `docs/architecture.md`'s job, and the runbook links to it rather than repeating it.
- **Every command in the runbook must be one you have actually run**, or clearly marked as
  requiring infrastructure that does not exist yet (task 031 lists those). A runbook with an
  untested command in it is worse than a short one.
- **The simulated incident is a real exercise, not fiction.** Pick something the code genuinely
  makes possible, reproduce it locally, then write it up. Good candidates, in order of how much
  they teach:
  1. **The F1 instance slept and the reminder job did not run for nine hours.** Reproduce by
     leaving `Enabled` on and moving the clock / the due dates; the system should catch up from
     state (`docs/architecture.md` §7) and not double-send inside the cool-down.
  2. **A magic link was re-sent, and a client used the old link.** Reproduce with two sends; the
     old token must fail to exchange with the same `401` as an unknown one.
  3. **A webhook receiver returned 500 for an hour.** Reproduce with a failing listener; attempts
     and backoff must be visible in the row, and the delivery must stop at `MaxAttempts`.
  Write up whichever you reproduce with: detect, diagnose, fix, prevent — and if the system
  behaved correctly, say so and record what *would* have gone wrong without the guardrail. An
  incident write-up where nothing broke is a legitimate result and is more honest than inventing a
  failure.
- **The README opens with the disclaimer, verbatim**, as `PLAN.md` §5 requires:
  "An independent learning prototype inspired by FileInvite; not affiliated with or endorsed by
  FileInvite."
- **Screenshots and the demo video are a human step.** You cannot take them. Put the headings and
  the image placeholders in place, with a short note of exactly which screens to capture and in
  what order, and list them in Progress notes as human steps. Do not generate, fake or describe a
  screenshot as though it exists.

## The runbook covers, at minimum

- How to run it locally from a fresh clone (`npm run setup`, `npm run dev`), and where the demo
  sign-in lands (`.local/dev-credentials.txt`).
- Where the data lives in each environment, and how to take and restore a backup
  (`scripts/backup-db.sh`), including the mid-write caveat.
- The configuration keys and which are secrets, pointing at `docs/architecture.md` §10 rather than
  duplicating the table.
- How to read the logs: what a correlation id is, and how to go from a user's `traceId` to the
  request's log lines (task 030).
- The background jobs: what each does, how to tell whether a pass ran, how to drive one by hand,
  and why a skipped pass is expected on F1.
- Magic links: how to re-send, what re-sending does to the old link, and what a client sees when
  their link has expired.
- The webhook: how to see pending and failed deliveries, what the retry schedule is, and how a
  receiver verifies a signature.
- Rate limits and what a `429` means for a user who hit one.
- A short "things that look like bugs and are not" list: a reload logs staff out (ADR 0003), an
  all-optional request completes on one approval (S4.12), cross-request access returns `404` not
  `403` (S2.9).

## Acceptance criteria

- [ ] `docs/runbook.md` covers every bullet above, and every command in it has been run or is
      marked as needing infrastructure that does not exist yet
- [ ] `docs/incidents/0001-*.md` documents an incident that was actually reproduced, with detect /
      diagnose / fix / prevent, and says honestly whether the system behaved correctly
- [ ] `README.md` opens with the disclaimer verbatim
- [ ] `README.md` explains what the project is, how to run it, how it is laid out, and what is
      deliberately out of scope, and links to `docs/spec.md`, `docs/architecture.md` and
      `docs/adr/`
- [ ] `README.md` has screenshot and demo-video sections with placeholders and a capture list, and
      no claim that either exists
- [ ] No file outside this task's scope is modified — check with `git status`
- [ ] Progress notes list the human steps as a numbered checklist

## Done when

The documents are written, `git status` shows only this task's files, and the report states which
steps need a human.

## Progress notes

(append here as you go)
