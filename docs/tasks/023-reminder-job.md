# 023: Reminder job — chase overdue requests
**Phase:** 2   **Depends on:** 020-phase2-schema   **Parallel-safe with:** 021, 022, 024, 025

## Goal
A background service that finds overdue requests on an interval and sends one reminder per request
per cool-down window, recording each in the database and the audit log.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Jobs/ReminderService.cs` and any supporting file under `Jobs/`
- `api/src/FileInviteLite.Api/Services/Reminders/` — a `ReminderOptions` class and, if you need
  one, a small pass type that the job and the tests both call
- `api/tests/FileInviteLite.Api.Tests/Features/Reminders/`

**Must not touch**
`Program.cs`, `AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`,
`Features/**`, `Services/Email/**` (task 011 owns `IEmailSender`; **use** it, do not change it),
`Services/Storage/**`, `Jobs/WebhookDeliveryService.cs` (task 025 owns it), `web/**`.

> **`Program.cs` is forbidden.** You need two registration lines — the hosted service and the
> options binding — and the orchestrator will add them. Put the exact lines you need in your
> Progress notes and in your final report, and write the code so those lines are all that is
> missing. Your tests must drive a pass directly rather than waiting for the host loop, so they
> pass without the registration.

## Decisions (already made — implement, do not revisit)

- **A pass is a method, not the loop.** `ExecuteAsync` is a thin loop that creates an
  `IServiceScope` per pass and calls the pass method, wrapped in try/catch so one failure cannot
  stop the host (S5.6). Tests call the pass method directly against a real SQLite file. Never hold
  a scoped `DbContext` in a field (`api/CLAUDE.md`).
- **Overdue is `DocumentRequest.IsOverdue(today)`**, which already exists as an `Expression` so it
  translates to SQL. Use it in the query. **Do not restate the rule.**
- **Options** in `ReminderOptions`, bound from a `Reminders` configuration section:
  `IntervalMinutes` (default 60), `CoolDownHours` (default 24), `Enabled` (default true).
  `Enabled` is false in test configuration so the loop never races a test — that is the flag
  `api/CLAUDE.md` promises.
- **Cool-down in SQL** (S5.2): a request is skipped when its most recent `Reminder.SentAt` is
  within `CoolDownHours` of now. The comparison happens in the query, not after loading rows.
- **Never remind on `Complete` or `Draft`** (S5.4). `Draft` means it was never sent, so there is
  nobody to chase.
- **Each reminder writes a `Reminder` row and a `ReminderSent` audit event with `ActorType.System`
  and a null `ActorId`, in one `SaveChangesAsync`** (S5.3, S6.1). One transaction per request, not
  one for the whole pass: a single failing request must not lose the reminders already sent in that
  pass.
- **`Reason`** is a short stable string, `"Overdue"`, not a sentence assembled from data. It is
  read by humans in an audit trail and compared by tests.
- **Sending goes through `IEmailSender`** (task 011's). In Development that is the dev sink; outside
  Development it is the no-op, and the `Reminder` row records that it was suppressed (S5.5) — put
  that in `Reason` as `"Overdue (suppressed)"`. Decide which it is from something the injected
  sender already exposes if it exposes one; if it does not, read `IHostEnvironment.IsDevelopment()`
  in the job and note the friction in Progress notes. **Do not edit `Services/Email/**`.**
- **Catch up from state, never from a clock** (`docs/architecture.md` §7). App Service F1 sleeps,
  so a pass may be hours late. The query must be correct for "everything overdue right now whose
  cool-down has elapsed", with no memory of when the last pass ran.
- **No reminder logs a magic-link token.** The link is not regenerated here; the reminder refers
  the client to the link they already have.

## Acceptance criteria

- [ ] **S5.1** A pass sends a reminder for every request that is overdue per
      `DocumentRequest.IsOverdue`
- [ ] **S5.2** A second pass inside the cool-down window sends nothing; a pass after it sends again
- [ ] **S5.3** Each reminder writes a `Reminder` row (`RequestId`, `SentAt`, `Reason`) and a
      `ReminderSent` audit event with `ActorType = System`, in the same transaction — prove the
      atomicity by forcing a failure
- [ ] **S5.4** No reminder for a `Complete` or a `Draft` request, even when the due date has passed
- [ ] **S5.5** In Development the reminder reaches the dev sink; outside Development nothing is
      sent and the `Reminder` row records the suppression. Assert both, by running the host in each
      environment the way `UnreadableBodyTests` does
- [ ] **S5.6** A failure on one request is logged and does not stop the pass or the host; the next
      pass retries it
- [ ] The cool-down comparison translates to SQL — assert with `ToQueryString()`, as
      `DocumentRequestRulesTests` does
- [ ] A pass over an empty database does nothing and throws nothing
- [ ] `Enabled = false` means the hosted loop never runs a pass
- [ ] **S2.13** A pass driven through a capturing log provider logs no token and no password

## Follow the pattern in

`api/src/FileInviteLite.Api/Features/Auth/Login.cs` (audit in one `SaveChangesAsync`),
`docs/architecture.md` §7, `api/tests/.../Features/Requests/RequestAtomicityTests.cs`,
`api/tests/.../Features/Requests/UnreadableBodyTests.cs` (hosting in a named environment).

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer findings resolved, and
the exact `Program.cs` registration lines are written in Progress notes.

## Progress notes

(append here as you go)
