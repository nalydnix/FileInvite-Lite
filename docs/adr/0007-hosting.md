# ADR 0007: Azure App Service for the API, Vercel for the web app, OIDC-authenticated deploys

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

The prototype needs to be reachable on the internet to demonstrate it, at no cost, with deploys
that an agent writes but never performs (`PLAN.md` §2: agents write the pipeline, deploys run only
on merge to `main` after all checks pass).

Two halves with different needs: a .NET API with a persistent volume and background services, and
a static React bundle.

## Decision

**API on Azure App Service (Linux, .NET 9, F1 free tier), single instance. Web app on Vercel. Both
deployed by GitHub Actions, only after CI passes on `main`. Azure authentication by OIDC federated
credentials.**

### API — Azure App Service F1

- Linux, .NET 9, **one instance** (required by ADR 0001 and ADR 0005: SQLite and uploaded files
  share the `/home` volume).
- The database file and the uploads live under `/home`, which persists across restarts and
  deploys.
- SQLite journal mode `DELETE` in production, because `/home` is network-backed.
- EF migrations are applied at startup. With one instance there is no concurrent-migration race.
- Secrets (`Jwt:SigningKey`, `Webhook:Secret`) are App Service settings, optionally backed by Key
  Vault references. Never in the repository, never in the workflow file.

### Web — Vercel

- Built and deployed by GitHub Actions via the Vercel CLI, **not** by Vercel's own Git
  integration. One gate for both halves: Vercel's integration would deploy on push regardless of
  whether CI passed.
- `VITE_API_URL` points at the App Service URL.

### Deploy authentication

- Azure: **OIDC federated credentials**. The workflow requests a short-lived token from GitHub's
  OIDC provider and exchanges it for Azure access. **No long-lived Azure secret exists in GitHub.**
- Vercel: a scoped project token in GitHub secrets. Vercel does not offer OIDC for CLI deploys, so
  this is the one long-lived credential, scoped to one project.

### Pipeline

```
PR            → build · test · typecheck · lint · vulnerability scan
merge to main → the same checks, then
                deploy-api  (OIDC → App Service) → smoke test /health and /api/auth/login
                deploy-web  (Vercel CLI)
```

A failed smoke test fails the workflow (`PLAN.md` §5, Phase 3).

### Cross-origin

Two origins, so the API's CORS allows exactly the Vercel production URL (plus `localhost:5173` in
Development), methods `GET, POST, OPTIONS`, headers `Authorization, Content-Type`, and
**credentials disabled** — no cookie crosses origins (ADR 0003). Magic links point at the Vercel
URL (`Portal:BaseUrl`).

## Consequences

### What this buys

- Free, for both halves.
- A persistent volume, which is what makes the SQLite-and-local-files decision viable at all.
- Long-running `BackgroundService`s, which a serverless or static host could not run.
- No long-lived cloud credential in GitHub for the half that holds the data.
- One quality gate: nothing reaches either host without green CI on `main`.

### What it costs

- **F1 sleeps when idle.** Background passes stop while the app sleeps, and the first request
  after a sleep is slow. Both jobs are written to catch up from database state (architecture §7),
  and the demo needs a warm-up request.
- **F1 has a 60-minute-per-day CPU quota.** Exceeding it returns 403s until the quota resets. Fine
  for a demo, fatal for anything real.
- **1 GB of storage**, shared by the database and the uploads.
- **Single instance, permanently.** Scaling out requires moving off SQLite and local files first.
- **No staging environment.** `main` deploys straight to the one environment; the smoke test is
  the only gate behind it.
- **Two deploy targets** means two workflows and two sets of failure modes.
- **One long-lived Vercel token** remains. Scoped to a single project, and the blast radius is a
  static bundle, not the data.
- **Backups are manual** (runbook): a dated copy of the SQLite file.
- **The app is internet-reachable with one demo account**, so the login rate limit and the
  password hashing in ADR 0003 are load-bearing, not decorative.

## Alternatives considered

| Option | Why not |
|---|---|
| **Azure Container Apps** | Scale-to-zero and a real container story, but the free grant is consumption-based and ephemeral storage conflicts with SQLite-on-disk. Needs a volume mount or a move to Blob + Azure SQL — a bigger change than the prototype needs. |
| **Everything on Vercel (API as serverless functions)** | No .NET first-class support, no persistent disk, no long-running background services. Three blockers. |
| **Azure Static Web Apps for the frontend** | Keeps one cloud and one credential, which is genuinely attractive. Rejected narrowly: Vercel's preview deploys and build feedback are better for a demo, and the OIDC decision already removes Azure's credential risk. |
| **A VM** | Full control, no free tier worth relying on, and it adds patching and TLS management to a prototype. |
| **Vercel's Git integration for the web app** | One click to set up, but it deploys on push with no CI gate, which breaks the rule that deploys follow green checks. |
| **A long-lived Azure service principal secret** | What OIDC exists to avoid: a credential in GitHub that grants cloud access until someone remembers to rotate it. |
