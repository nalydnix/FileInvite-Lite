# ADR 0003: JWT bearer tokens for staff authentication, held in memory in the browser

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

Staff sign in; clients never do (they use magic links, ADR 0004). The frontend is on Vercel and the
API is on Azure App Service, so **the two are on different origins**. That rules out the simplest
cookie story: a same-site cookie does not cross origins, and a cross-site cookie needs
`SameSite=None; Secure`, CORS with credentials, and CSRF protection — three more things to get
right in a prototype.

The prototype has one seeded demo user and no user management. Session lifetime matters less than
not leaking credentials.

## Decision

**HS256 JWT bearer tokens**, issued by `POST /api/auth/login`.

- Signing key from user secrets locally and App Service settings / Key Vault in production. Never
  committed. Startup fails if the key is missing or shorter than 32 bytes.
- 60-minute expiry. **No refresh tokens** (out of scope, `PLAN.md` §1).
- Passwords hashed with ASP.NET Core's `PasswordHasher<T>`.
- Login is rate-limited to 5 attempts per IP per minute.
- Wrong password and unknown email return an identical `401` body.
- **In the browser, the JWT is held in a React context in memory only** — never `localStorage`,
  never `sessionStorage`, never a cookie.
- The token is sent as `Authorization: Bearer`, so no cookie crosses origins and CORS runs without
  credentials.

## Consequences

### What this buys

- No CSRF surface: there is no ambient credential for a browser to attach automatically.
- No XSS-readable token store. A script injected into the page cannot read a token out of
  `localStorage`, because there is nothing there. (It could still read the in-memory token while
  the page runs — in-memory is better, not immune.)
- Cross-origin works with plain CORS and no credentials.
- Stateless validation: no session table, which suits a single-instance SQLite deployment.

### What it costs

- **A page reload logs the user out.** Accepted for a prototype, and it is the direct price of not
  persisting the token. It is also visible in the demo, so it must be called out there.
- **No revocation.** A stolen token is valid until it expires. Mitigated only by the 60-minute
  lifetime. A real system needs refresh tokens plus a revocation list.
- **The key is a single shared secret.** Rotating it invalidates every live token at once.
- Multiple tabs each need their own login.

### The trade-off, stated plainly

| Store | XSS exposure | CSRF exposure | Survives reload | Cross-origin cost |
|---|---|---|---|---|
| `localStorage` | Readable by any injected script | None | Yes | None |
| httpOnly cookie | Not readable by script | **Yes** — needs CSRF tokens | Yes | `SameSite=None`, CORS credentials |
| **In memory (chosen)** | Readable only while the page runs | None | **No** | None |

For a prototype with one demo account, losing the session on reload is the cheapest of the three
costs, and the only one that cannot silently become a vulnerability.

### If this went to production

Short-lived access token in memory, refresh token in an httpOnly `SameSite=Strict` cookie on a
shared parent domain, with CSRF protection on the refresh endpoint and a server-side revocation
list. That is a different design, not a tweak to this one.

## Alternatives considered

| Option | Why not |
|---|---|
| **httpOnly cookie session** | The strongest option, but it needs a shared parent domain or cross-site cookies plus CSRF defence. Too much machinery for a two-origin prototype. |
| **JWT in `localStorage`** | Survives reload, but any XSS reads it and it persists after the tab closes. The reload cost is cheaper than that risk. |
| **ASP.NET Core Identity with cookies** | Brings user management, lockout, 2FA — all out of scope — and the same cross-origin cookie problem. |
| **External IdP (Entra ID, Auth0)** | SSO is explicitly out of scope, and it would make a clone-and-run prototype depend on an external tenant. |
