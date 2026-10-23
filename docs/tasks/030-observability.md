# 030: Observability — structured logging, correlation ids, error tracking
**Phase:** 3   **Depends on:** Phase 2 merged   **Parallel-safe with:** nothing — sequential, it edits `Program.cs`

## Goal
Every request and every background pass is traceable through structured logs keyed by a
correlation id, and nothing sensitive is ever in them.

## Why this is sequential

It edits `Program.cs`, `appsettings*.json` and the shared logging setup, which are the hotspots
`CLAUDE.md` rule 2 reserves for sequential tasks. It also touches nothing feature-specific, so
there is nothing to parallelise.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Program.cs`
- `api/src/FileInviteLite.Api/Common/Observability/` — correlation id middleware, the log scope,
  and a redaction helper if one is needed
- `api/src/FileInviteLite.Api/appsettings.json`, `appsettings.Development.json` — logging levels
  and the new section only; **no secret, no connection string**
- `api/tests/FileInviteLite.Api.Tests/Features/Observability/`
- `api/tests/FileInviteLite.Api.Tests/Infrastructure/LogCapture.cs` — extend only; task 012 built
  it and other tests depend on it

**Must not touch**
`AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`, `Features/**`,
`Jobs/**`, `Services/**`, `web/**`.

> If a feature or a job needs changing to log correctly, **do not change it**. Write what is
> needed in Progress notes and report it; a follow-up task makes that edit.

## Decisions (already made — implement, do not revisit)

- **The correlation id is the trace id already in the problem shape** (S10.1), so a user reporting
  an error quotes a `traceId` that appears verbatim in the logs. Do not invent a second id. Read
  `Activity.Current?.Id` / `HttpContext.TraceIdentifier` the way `ProblemResults` already does —
  check that file and use the same source, or the two will diverge.
- **One log scope per request**, carrying the correlation id, the method, the route template and
  the authenticated actor *kind* (`Staff`, `Client`, `Anonymous`). Not the staff id, not the client
  email, not the request id — the actor kind is enough to read a log, and ADR 0006 is easier to
  honour if personal data was never written.
- **No new logging dependency.** `Microsoft.Extensions.Logging` with the built-in JSON console
  formatter in production and the plain formatter in Development. Serilog and friends are out of
  scope for a prototype.
- **Background passes get the same treatment**: each pass opens a scope with a generated
  correlation id, so a reminder or a delivery is traceable without an HTTP request.
- **Redaction is not a filter, it is a rule.** Do not add a scrubber that strips token-shaped
  strings from log output — that would make it safe to log a token, which is exactly the habit the
  project forbids (`CLAUDE.md` rule 11). Instead the test below pins that nothing logs one.
- **Error tracking is the exception handler that already exists**, plus one logged event per
  unhandled exception carrying the correlation id and the exception type. No third-party service:
  no account to configure, and a prototype that posts stack traces off-box is a worse trade than
  one that does not. Record that choice in a short ADR, `docs/adr/0008-observability.md`, with the
  upgrade path (an OpenTelemetry exporter behind configuration).

## Acceptance criteria

- [ ] A request's log lines and the `traceId` in its problem response carry the same value —
      assert by driving a failing request and matching the body against the captured logs
- [ ] Log scopes carry the actor kind and never the staff id, the client email or the request id
- [ ] An unhandled exception is logged once, with the correlation id and the exception type, and
      the response body still carries only `traceId` (S10.1) and no exception text
- [ ] A background pass logs under its own correlation id
- [ ] **S2.13, extended to the whole app:** one test drives the full happy path — login, create,
      send, exchange, upload, download, approve, a reminder pass and a webhook pass — through
      `LogCapture` at `Trace` level, and asserts that no captured line contains the magic-link
      token, the portal session token, the staff JWT, the demo password, the webhook secret, a
      storage key or any uploaded file's bytes. This is the single most valuable test in Phase 3;
      write it first
- [ ] **S10.5** `GET /health` still returns `200` anonymously and reveals no version, environment
      or connection detail
- [ ] JSON console formatting in Production, readable formatting in Development — assert by
      starting the host in each environment the way `UnreadableBodyTests` does
- [ ] `docs/adr/0008-observability.md` exists and records the no-third-party decision

## Follow the pattern in

`api/src/FileInviteLite.Api/Common/Results/ProblemResults.cs` (where the trace id comes from),
`api/src/FileInviteLite.Api/Common/Validation/UnreadableBodyHandling.cs` (shared pipeline
behaviour registered from `Program.cs`), `api/tests/.../Infrastructure/LogCapture.cs`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer run and findings
resolved.

## Progress notes

(append here as you go)
