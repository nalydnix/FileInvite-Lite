# 031: Production data and the deploy pipeline
**Phase:** 3   **Depends on:** 030-observability   **Parallel-safe with:** 032 (docs only)

## Goal
The app runs on Azure App Service with its SQLite database and uploaded files on the persistent
`/home` volume, and both halves deploy from GitHub Actions only after CI is green on `main`.

## Hard limit on what this task does

**This task never performs a deployment.** `PLAN.md` §2 is explicit: agents write the pipeline,
humans run it. You write the workflows, the configuration and the tests, and you verify them by
reading and by what CI itself can check. Anything that needs a real Azure subscription, a Vercel
project, an OIDC federated credential or a secret in GitHub is a **human step**: list each one in
Progress notes as a numbered checklist the human can work through, and say plainly in your report
that the pipeline is unverified against real infrastructure.

## Scope

**May touch**
- `.github/workflows/deploy-api.yml`, `.github/workflows/deploy-web.yml` — new files
- `.github/workflows/ci.yml` — only if the deploy workflows need a published artifact or a
  reusable-workflow call; keep the change minimal and say why in Progress notes
- `api/src/FileInviteLite.Api/Program.cs` — journal mode, migrations at startup, production CORS
- `api/src/FileInviteLite.Api/appsettings.json`, `appsettings.Production.json` — **paths, URLs and
  flags only, never a secret**
- `api/src/FileInviteLite.Api/Common/Configuration/` — an options class for the storage/database
  paths if the validation needs one
- `api/tests/FileInviteLite.Api.Tests/Features/Configuration/`
- `web/.env.example`, `web/vite.config.ts` — only if the production API base URL needs it
- `scripts/backup-db.sh` — the dated-copy backup step

**Must not touch**
`AppDbContext`, `Data/Migrations/**` (the migrations themselves), any `.csproj`,
`docs/api-contract.md`, `Features/**`, `Jobs/**`, `Services/**`, `web/src/**`.

## Decisions (already made — implement, do not revisit)

From `PLAN.md` §2 "Deployed data + files" and ADR 0007:

- **SQLite in production**, on `/home`, single instance, journal mode **DELETE** (WAL is
  network-volume-unsafe); WAL stays the local default. Journal mode comes from
  `Database:JournalMode`, applied at startup, not hard-coded.
- **Uploaded files on `/home/files`**, from `Storage:RootPath`. Startup validates the path is
  writable and fails fast with a clear message if it is not.
- **Migrations are applied at startup** — already true in `Program.cs`; confirm it and make sure a
  failure is a clear startup failure rather than a half-migrated host.
- **Production CORS is locked to the configured Vercel origin**, with `localhost` allowed in
  Development only (S10.2). Assert the production host rejects `localhost`.
- **Backup is a dated file copy**, documented, not automated infrastructure: `scripts/backup-db.sh`
  copies the database file to `app-YYYY-MM-DD.db` beside it, and the runbook (task 032) says when
  to run it. SQLite in DELETE mode with the app idle is safe to copy; say so in the script's
  comment and note the caveat that a copy taken mid-write is not consistent.
- **Azure login uses OIDC federated credentials** — `permissions: id-token: write`, the
  `azure/login` action with `client-id`, `tenant-id`, `subscription-id` from repository variables,
  and **no long-lived secret**. App secrets (`Jwt:SigningKey`, `Webhook:Secret`) are App Service
  settings, set by the human, never in a workflow file.
- **Vercel deploys via the Vercel CLI** with a token from a GitHub secret, after CI passes, so the
  same gate applies to both halves.
- **Both deploy workflows run only on `push` to `main` and only after CI succeeds** — use
  `workflow_run` on the CI workflow with a `conclusion == 'success'` condition, or a job that
  `needs` the CI jobs if you fold them in. Whichever you choose, a red CI must make a deploy
  impossible, and a test or a comment must make that obvious to a reader.
- **A smoke test gates each deploy**: after the API deploy, `GET /health` must return `200` and
  `POST /api/auth/login` with deliberately wrong credentials must return `401` — which proves the
  app booted, the database opened and the auth pipeline is wired, without needing a real password
  in CI. After the web deploy, fetch the Vercel URL and assert a `200`. **A failed smoke test
  fails the workflow.**
- **Concurrency**: one deploy at a time per environment, and a new push supersedes a queued one.
- **No secret is ever echoed**, including into a workflow log. Use `::add-mask::` where a value is
  computed.

## Acceptance criteria

- [ ] `appsettings.Production.json` sets `Database:JournalMode` to `DELETE` and the `/home` paths,
      and contains **no** secret, connection string with credentials, or signing key
- [ ] A test asserts the configured journal mode is actually applied to the connection — read it
      back with `PRAGMA journal_mode`
- [ ] A test asserts an unwritable storage path fails startup with a clear message
- [ ] **S10.2** A test asserts the production CORS policy allows only the configured origin and
      rejects `http://localhost:5173`, while Development allows it
- [ ] `scripts/backup-db.sh` produces a dated copy, is idempotent for the same day, and fails
      loudly if the database file is missing
- [ ] `deploy-api.yml` and `deploy-web.yml` run only after CI succeeds on `main`, never on a PR —
      assert by reading the triggers, and state the reasoning in a comment at the top of each file
- [ ] Neither workflow contains a secret value, and Azure login uses OIDC with no client secret
- [ ] Each workflow's smoke test fails the workflow on a bad response, and the API smoke test
      covers both `/health` and a wrong-credentials `401`
- [ ] `actionlint` (or `yamllint` plus a careful read) reports no error on both workflows — say
      which you ran and paste the output
- [ ] Progress notes contain the numbered human checklist of everything needing real
      infrastructure, with the exact names of every variable and secret the workflows expect

## Follow the pattern in

`.github/workflows/ci.yml`, `api/src/FileInviteLite.Api/Program.cs` (startup work and
`EnsureDirectoryExists`), `api/src/FileInviteLite.Api/Common/Security/JwtOptions.cs` (options
validated at startup), `api/tests/.../Infrastructure/StartupValidationTests.cs`,
`docs/adr/0007-hosting.md`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer run and findings
resolved, and the report states plainly which parts are unverified against real infrastructure.

## Progress notes

(append here as you go)
