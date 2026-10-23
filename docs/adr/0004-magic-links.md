# ADR 0004: Magic links for client access, exchanged for a request-scoped session

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

Clients upload documents without an account — that is the product. So a URL sent by email is the
only credential, and it must grant access to **exactly one request** and nothing else
(`PLAN.md` §4).

The risks are specific:

1. A URL in an email ends up in inboxes, forwarded threads, browser history, and — if it is in the
   path or query string — **server access logs, proxy logs and `Referer` headers**.
2. A token stored as-is means a database leak is an immediate access leak.
3. A client endpoint that reads an id from the route rather than the session is a horizontal
   privilege escalation waiting to happen: change the id, read someone else's documents.

## Decision

**Magic link → one-time exchange → short-lived, request-scoped portal session.**

1. **Token generation.** At least 256 bits from a cryptographic RNG, URL-safe base64.
2. **Storage.** Only `SHA-256(token)` is persisted, in `MagicLink.TokenHash` (unique index).
   Lookup hashes the presented token and matches on the hash. The token itself never reaches the
   database.
3. **Transport.** The token travels in the **URL fragment**: `https://app/portal#t=<token>`.
   A fragment is never sent to a server, so it cannot appear in access logs, and it is not
   included in a `Referer` header.
4. **Exchange.** The React app reads the fragment, calls `POST /api/portal/exchange` once, then
   strips the fragment with `history.replaceState`. The magic-link token is never stored by the
   browser.
5. **Portal session.** The exchange returns a separate short-lived (30 min) session token carrying
   a `request_id` claim, held in memory. It uses its own authentication scheme and its own
   authorisation policy, so **a staff JWT cannot authorise a portal endpoint and a portal token
   cannot authorise a staff endpoint**.
6. **Scoping.** Every portal query filters by `CurrentPortal.RequestId` **before** any route
   parameter is applied. There is no portal endpoint that takes a request id.
7. **Not found, not forbidden.** A portal session asking about another request's item gets `404`,
   so it cannot confirm the item exists.
8. **Lifecycle.** Links have an expiry and a `RevokedAt`. Re-sending a request revokes the previous
   link and issues a new one.
9. **Uniform failures.** Unknown, malformed, expired and revoked tokens all return the same `401`
   body.
10. **Rate limiting.** 10 exchange attempts per IP per minute.
11. **Headers.** Portal responses send `Referrer-Policy: no-referrer`.
12. **Logging.** Tokens are never logged at any level; an integration test drives a full happy path
    through a capturing log provider and asserts no token appears.

## Consequences

### What this buys

- The token never reaches a server log, a proxy log or a third-party site via `Referer`.
- A database dump yields hashes, not access.
- The scope check cannot be forgotten per-endpoint, because there is no request id to pass — the
  only source is the session claim.
- Uniform failures and `404`-over-`403` give an attacker no oracle.

### What it costs

- **The link is still bearer access.** Anyone with the URL is the client. Forwarding the email
  forwards the access. Mitigated by expiry, revocation and audit, not eliminated. A real system
  would add an email-confirmation step or a second factor.
- **Fragment handling is JavaScript-only.** The portal cannot be a server-rendered page, and the
  app must strip the fragment before any navigation. Covered by S9.3 and S9.4.
- **Two token types to keep straight.** Mitigated by separate schemes and policies, and by tests
  that cross them deliberately (S2.7, S2.8).
- **A revoked link is a dead end for the client**, who must be re-sent a new one. Acceptable, and
  it is how re-sending is specified to behave.
- No token rotation during a portal session; it simply expires at 30 minutes.

## Alternatives considered

| Option | Why not |
|---|---|
| **Token in the path or query string** | Simplest, and the reason this ADR exists: it lands in access logs, proxy logs, browser history and `Referer` headers. Rejected. |
| **Store the token in plaintext** | A leaked backup is a leaked portal. Hashing costs one SHA-256 per exchange. |
| **Use the magic-link token as the session token on every call** | Keeps the long-lived credential in flight on every request, with no way to shorten its exposure or revoke a session without revoking the link. |
| **One-time token, consumed on first use** | Too brittle for the real workflow: clients open the link on a phone, come back later, upload one document at a time. Expiry plus revocation gives most of the benefit without breaking the product. |
| **Client accounts with passwords** | Out of scope, and the product's point is that a client does not need one. |
| **Signed, stateless token (JWT) as the magic link** | No revocation without server state, and the whole token — not just a hash — would be reconstructable from a leak of the signing key. Server-side records give revocation for one table. |
