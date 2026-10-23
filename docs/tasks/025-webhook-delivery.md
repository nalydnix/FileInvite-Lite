# 025: Completion webhook — signing, posting, retrying
**Phase:** 2   **Depends on:** 020-phase2-schema   **Parallel-safe with:** 021, 022, 023, 024

## Goal
A background service that picks up pending `WebhookDelivery` rows, posts an HMAC-signed JSON
payload to the configured URL, and retries with exponential backoff up to a limit.

## Where the row comes from

Task 021 creates the `WebhookDelivery` row inside the transaction that completes a request (S7.1).
You never create one. You read pending rows and deliver them. 021 is running in parallel, so the
row may not be creatable through the API while you work — **create rows directly in your tests'
database through `AppDbContext`**, which is also the honest unit of what this job does.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Jobs/WebhookDeliveryService.cs` and supporting files under `Jobs/`
- `api/src/FileInviteLite.Api/Services/Webhooks/` — `IWebhookSigner`, `HmacWebhookSigner`,
  `WebhookOptions`, and the payload record
- `api/tests/FileInviteLite.Api.Tests/Features/Webhooks/`

**Must not touch**
`Program.cs`, `AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`,
`Features/**`, `Jobs/ReminderService.cs` (task 023 owns it), `Services/Email/**`,
`Services/Storage/**`, `web/**`.

> **`Program.cs` is forbidden.** You need the hosted service, the options binding with its
> startup validation, the signer registration and a named `HttpClient`. Write the exact lines in
> your Progress notes and in your final report; the orchestrator adds them. Your tests must drive
> a pass directly, so they pass without the registration.

## Decisions (already made — implement, do not revisit)

- **The payload shape is `docs/api-contract.md` §Webhook, exactly**, serialised with
  `System.Text.Json` defaults (camelCase property naming as the project already configures it —
  check what `CreateRequest`'s responses produce and match it). Task 021 hashes this same shape
  into `PayloadHash` using its own copy of the serialisation, because a feature folder may not
  reach sideways into another. **Your bytes and 021's bytes must match**, or the hash records
  nothing useful. Write a test that asserts the hash you compute for a known request equals the
  SHA-256 of your serialised payload, and flag the duplication in Progress notes so a later
  sequential task can move the payload record into `Services/Webhooks/` and have 021 use it.
- **Signature**: `X-Signature: sha256=<hex HMAC-SHA256 of the exact body bytes>`, keyed with
  `Webhook:Secret` (S7.3). Sign the **bytes you send**, not a re-serialisation — serialise once to
  a `byte[]`, sign that array, and post that array. Re-serialising is how a signature silently
  stops matching.
- **Headers** also carry `X-Delivery-Id` and `X-Attempt`, per the contract.
- **Options** in `WebhookOptions`, bound from a `Webhook` section: `Url` (optional — no URL
  configured means the job does nothing, which is the default prototype state), `Secret`,
  `MaxAttempts` (default 5), `IntervalMinutes` (default 1), `TimeoutSeconds` (default 10),
  `Enabled` (default `true`, `false` in tests).
- **Startup validation**: a configured `Url` that is not HTTPS **fails the host** with a clear
  message (S7.7). Validate it the way `JwtOptions` is validated — data annotations plus
  `ValidateOnStart` — so the failure happens at startup, not on the first delivery. `http://`
  anywhere but a configured-off state is a startup failure, including `http://localhost`: the
  prototype's own local listener is configured through the same key and must use HTTPS, or be left
  unset.
- **The URL comes from configuration only** (S7.7). No request body, query string or database row
  influences it. The `WebhookDelivery.Url` column records what was used at the time, for the audit
  trail; the job still reads the live configured URL for the attempt it is making. Write a test
  that a row whose `Url` column has been tampered with in the database is **not** posted to that
  tampered URL.
- **Backoff** is exponential from the interval (1 min, 5 min, 25 min, …) computed from `Attempts`
  and the row's last attempt time, so a sleeping host catches up from state rather than from a
  timer (`docs/architecture.md` §7). After `MaxAttempts` the row is marked failed and never
  retried (S7.6). `DeliveredAt` is set only on success.
- **Each attempt updates `Attempts` and `LastStatus`** (S7.5) and writes an audit event —
  `WebhookDelivered` on success, `WebhookFailed` on a final failure — with `ActorType.System`, in
  the same `SaveChangesAsync` as the row update (S6.1). An intermediate failure that will be
  retried updates the row without an audit event; only the final failure is auditable, so the
  trail does not fill with noise.
- **A timeout counts as a failed attempt**, with `LastStatus` null or 0 to distinguish it from an
  HTTP status. State which you chose in Progress notes.
- **The secret is never logged and never appears in a payload** (S7.8). Neither is the signature
  header value, and neither is the whole payload at `Debug` — log the delivery id and the status.
- **A failure never stops the host** (S5.6's rule, applied here too): wrap the pass, and wrap each
  row inside it, so one bad row does not abandon the others.

## Acceptance criteria

- [ ] **S7.2** A pass posts the payload as JSON to the configured URL
- [ ] **S7.3** `X-Signature` is `sha256=` plus the hex HMAC-SHA256 of the exact body bytes, keyed
      with the configured secret — assert by recomputing it in the test the way a receiver would,
      and assert a changed body no longer verifies
- [ ] **S7.4** The payload contains the request id, client name, client email, completion timestamp
      and per-item outcomes, and contains no file content, no token and no storage key — assert the
      serialised JSON against the absent strings, not just the object
- [ ] **S7.5** A non-2xx and a timeout are each retried with exponential backoff; `Attempts` and
      `LastStatus` are updated after every try
- [ ] **S7.6** After the final failed attempt the row is marked failed and a further pass does not
      retry it; `DeliveredAt` stays null
- [ ] `DeliveredAt` is set on success and the row is not delivered twice
- [ ] **S7.7** A configured non-HTTPS URL fails startup with a clear message — assert by starting
      the host the way `StartupValidationTests` does
- [ ] **S7.7** A tampered `Url` column is not posted to; the configured URL is used
- [ ] **S7.8** A full delivery driven through a capturing log provider logs neither the secret nor
      the signature
- [ ] No `Webhook:Url` configured means a pass does nothing and throws nothing
- [ ] A failing row does not prevent the other rows in the same pass from being delivered
- [ ] `Enabled = false` means the hosted loop never runs a pass
- [ ] Backoff is computed from database state, so a pass that is hours late behaves correctly

## Follow the pattern in

`api/src/FileInviteLite.Api/Common/Security/JwtOptions.cs` and
`api/tests/.../Infrastructure/StartupValidationTests.cs` (options validated at startup),
`docs/architecture.md` §7, `api/src/FileInviteLite.Api/Features/Auth/Login.cs` (audit in one
`SaveChangesAsync`).

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer run and every finding
resolved — this task signs outbound requests with a secret and does not merge unreviewed.

## Progress notes

(append here as you go)
