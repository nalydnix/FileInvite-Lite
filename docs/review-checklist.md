# Human review checklist for AI-generated code

For the human reviewing a PR from an agent session. The `security-reviewer` agent runs first and
catches the mechanical issues; this list is for the things a reviewer has to judge.

**The premise:** agent-written code fails differently from human-written code. It is usually
syntactically clean, idiomatic, confidently commented and plausible. The failure modes are
*plausible and wrong*: a check that looks like the right check but tests the wrong variable, a
test that asserts something trivially true, a summary that claims more than the diff delivers.
So read for meaning, not for style — the style will be fine.

Budget the attention: **auth, scoping, uploads, audit writes and anything deleted** deserve
line-by-line reading. Everything else can be skimmed.

---

## 1. Claims versus diff (do this first, it is the cheapest)

- [ ] The summary says what the diff does. Anything claimed but missing?
- [ ] Anything in the diff that the summary does not mention?
- [ ] Did it say "all tests pass"? Check CI, not the sentence.
- [ ] Did it say anything is unverified, skipped or partial? Believe that part.
- [ ] Are the task file's Progress notes updated enough for a fresh session to resume?

## 2. Scope

- [ ] Every changed file is inside the task's declared "May touch" list.
- [ ] No change to `Program.cs`, `AppDbContext.cs`, `Data/Migrations/**`, a `.csproj`,
      `package.json`, `package-lock.json` or `docs/api-contract.md` — unless this is the
      sequential task that owns them.
- [ ] No new dependency. If there is one: why, is it maintained, does it have an advisory?
- [ ] No drive-by refactor of code the task did not need to change.

## 3. Tests — the part most worth your time

- [ ] **Nothing weakened.** Read every change to an existing test. An assertion loosened, a case
      removed, a `Skip`, a `[Fact]` turned into a comment, a strict equality turned into a
      "contains" — any of these is a stop.
- [ ] Do the tests actually fail if the feature is broken? Pick the most important test, mentally
      break the implementation, and check the test would catch it. If it would not, the test is
      decoration.
- [ ] Are the negative cases there? For this project they *are* the product: wrong request,
      expired token, oversized file, renamed executable, staff token on a portal endpoint.
- [ ] Does each state-change test assert the audit event as well as the state?
- [ ] Any test asserting something trivially true (`result.Should().NotBeNull()` and nothing else)?
- [ ] Are new tests named after the criteria they cover, and does every criterion in the task file
      have one?

## 4. Security (after `security-reviewer` has reported)

- [ ] Read the reviewer's report. Were findings fixed, or argued away? An argued-away finding
      needs your judgement, not the agent's.
- [ ] Portal handlers: does **every** query filter by `CurrentPortal.RequestId`, and is the filter
      applied before any route parameter is used?
- [ ] New endpoints: correct policy, no accidental anonymous route.
- [ ] Any new log statement — could it ever receive a token, password or file content?
- [ ] Any new configuration value — is it read from configuration, with no default in code?
- [ ] Cross-request failures return `404`, not `403`.

## 5. Audit integrity

- [ ] Every state change goes through `IAuditWriter` and commits in **one** transaction with its
      state change. Count the `SaveChangesAsync` calls per handler.
- [ ] Nothing updates or deletes an `AuditEvent`.
- [ ] No personal data, token or storage key in audit metadata.

## 6. Correctness, read for meaning

- [ ] Walk the happy path by hand with a concrete value. Does it do what the spec says?
- [ ] Walk one error path the same way.
- [ ] Off-by-one and boundary values: exactly 20 MB, due date exactly today, zero items, the last
      required item being approved.
- [ ] Null and empty: empty item list, missing description, no uploaded file, no magic link yet.
- [ ] Does the status roll-up still hold for every path that changes an item status?
- [ ] Are the comments true? An agent comment describing behaviour the code does not have is a
      common and expensive failure.

## 7. Data and queries

- [ ] UTC `DateTime`, never `DateTimeOffset`, in anything that reaches a query.
- [ ] `AsNoTracking()` on reads that do not feed an update.
- [ ] `CancellationToken` passed through.
- [ ] No N+1 introduced in a list endpoint.
- [ ] Migration (if this is the schema task): reviewed by hand, no data loss, applies to a copy of
      the existing database.

## 8. Frontend

- [ ] No token in `localStorage`, `sessionStorage` or a cookie — grep for all three.
- [ ] The portal strips the URL fragment after exchange.
- [ ] All network calls go through `apiClient`.
- [ ] Error states, loading states and empty states exist.
- [ ] No personal data in a URL or query string.

## 9. Before merging

- [ ] Rebased on the latest `main` and all checks re-run after the rebase.
- [ ] **Any conflict resolution made by an agent has been read line by line.** Treat it as
      untrusted: a resolution that silently drops one side's change is the classic failure.
- [ ] CI is green on the final commit, not an earlier one.
- [ ] If anything on this list went wrong in a way a guardrail could prevent, add an entry to
      `docs/ai-log.md` before merging.

---

## Stop and push back when you see

- A test deleted, skipped or loosened to make a build pass.
- A security check moved, commented out, or replaced with a `TODO`.
- A change outside the declared scope, however small and however sensible.
- A confident summary you cannot verify from the diff.
- A conflict resolution you did not read.
- "This is a prototype, so…" used to justify any of the above.
