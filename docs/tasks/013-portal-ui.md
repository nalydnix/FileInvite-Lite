# 013: Portal UI — the client's page
**Phase:** 1   **Depends on:** 010-phase1-schema   **Parallel-safe with:** 011, 012

## Goal
The page a client reaches from a magic link: it exchanges the token, shows what is outstanding,
and uploads a file per item.

## Working without the API

Task 012 builds the portal endpoints in parallel with this one, so they will not exist while you
work. Build against `docs/api-contract.md` — it is the agreed shape and it is authoritative. Mock
at `apiClient` in tests, exactly as `web/tests/RequestListPage.test.tsx` does. **Do not** wait for
012, and do not reach into `api/` to see what it is doing. If the contract is unclear, write the
question in Progress notes rather than inventing a shape.

## Scope

**May touch**
- `web/src/features/portal/` — `PortalPage.tsx`, `UploadCard.tsx`, `routes.tsx`
- `web/src/app/PortalSessionContext.tsx` and `web/src/app/routes.tsx` (the one import line)
- `web/tests/PortalPage.test.tsx`, `web/tests/portalToken.test.ts`
- `web/src/index.css` — additions only, no edits to existing rules

**Must not touch**
`api/**`, `web/src/features/requests/**`, `web/src/features/auth/**`, `web/src/app/apiClient.ts`,
`web/src/app/AuthContext.tsx`, `package.json`, `docs/api-contract.md`.

## Decisions (already made — implement, do not revisit)

- **Token handling is the whole point of this task.** On mount, read the token from
  `window.location.hash`, call `POST /api/portal/exchange` **once**, then remove the fragment with
  `history.replaceState` before any further navigation (S9.3).
- **Nothing is persisted.** The magic-link token is never written to `localStorage`,
  `sessionStorage` or a cookie, and neither is the portal session token (S9.4). The session lives
  in `PortalSessionContext`, in memory, like the staff JWT.
- **No new dependency.** Use the existing `useApi` hook and `apiClient`.
- **The portal has no login.** A missing, failed or expired exchange shows a plain "ask whoever
  sent this for a new link" message — never a login form, never a raw status code.
- Uploads go through a normal `<input type="file">` with `multipart/form-data`. No drag-and-drop,
  no preview, no client-side image processing.

## Acceptance criteria

- [ ] **S9.3** The token is read from the fragment, exchanged once, and the fragment is gone from
      `window.location` afterwards. Assert the exchange is called exactly once across a re-render
- [ ] **S9.4** After a successful exchange, neither token appears in `localStorage`,
      `sessionStorage` or `document.cookie` — assert on the literal token strings
- [ ] **S9.7** Outstanding, Uploaded, Approved and Rejected items are visually distinct, by
      something other than colour alone, and a rejected item shows its comment
- [ ] **S9.8** Type, size and server rejections each produce a readable message taken from the
      problem `detail`, never a status code. Cover `415`, `413` and `409`
- [ ] A failed exchange (401) shows the "ask for a new link" state and no upload controls
- [ ] Loading, empty and error states exist
- [ ] The file input is labelled, keyboard-operable, and the upload result is announced to a
      screen reader
- [ ] An upload in progress disables the control and cannot be double-submitted
- [ ] `npm run typecheck` and `npm run lint` clean — no `any`, no non-null assertion to silence
      the compiler

## Follow the pattern in

`web/src/features/requests/RequestListPage.tsx`, `web/src/app/useApi.ts`,
`web/tests/CreateRequestPage.test.tsx`.

## Done when

`npm --prefix web run typecheck`, `lint` and `test` all green, CI green on the PR. Once 012 has
merged, the human runs the two together and clicks through a real link before this is called done
— note in the PR that it has not been run against a live API.

## Progress notes

(append here as you go)

### Progress notes: portal UI (agent session)

- Tests written first (`web/tests/PortalPage.test.tsx`, `web/tests/portalToken.test.ts`); they failed on
  unresolved imports, then went green once the implementation existed. Built against
  `docs/api-contract.md` only; nothing under `api/` was read. NOT run against a live API.
- Friction: `apiClient.apiRequest` can only send `Authorization: Bearer` and JSON, and does not export
  its base URL or `readProblem`. The portal needs the `Portal` scheme and multipart, so
  `portalRequest` lives in `web/src/app/portalSessionState.ts` (sibling of PortalSessionContext.tsx,
  per the Fast Refresh rule) and calls `fetch` itself. It duplicates the base-URL line and the small
  problem parser, and throws the same `ApiError`. The exchange call still goes through `apiRequest`.
  This bends the "all calls through apiClient" rule; a later sequential task could add an `authScheme`
  option and FormData support to apiClient and delete `portalRequest`. `useApi` was not usable
  (hardwired to staff `useAuth`), so PortalPage has its own small fetch effect.
- `portalSessionState.ts` is a new file outside the task's listed scope, added only because the
  Fast Refresh rule requires a sibling `.ts` for the context/hook/helper (approved by the task owner).
- Contract reading: item statuses taken from spec (`Outstanding|Uploaded|Approved|Rejected`); the
  contract example only shows `Rejected`. Fragment format assumed `#t=<token>` (contract link example).
  Any failed exchange (401, 429, network) shows the "ask for a new link" state; a 429 gets no
  distinct wording. If the server sends no problem `detail` on 409/413/415, the UI uses its own
  readable fallback text.
- Uploads: file input plus an explicit Upload button (not upload-on-change); Approved items have no
  upload form; Uploaded and Rejected items can be re-uploaded (contract only 409s on Approved).
- index.css: appended `.portal-*` rules only.

### Orchestrator follow-up, 2026-10-23

The `portalRequest` friction recorded above is resolved, in a sequential pass over the shared
files that 013 was rightly forbidden from touching:

- `apiRequest` in `web/src/app/apiClient.ts` now takes a `scheme` option (`'Bearer'` by default,
  `'Portal'` for portal calls) and sends a `FormData` body as multipart without setting
  `Content-Type`. That was the whole of what the portal needed.
- `portalRequest` and its duplicated `readProblem` and base-URL line are deleted from
  `web/src/app/portalSessionState.ts`, which is back to holding the context, the hook and
  `readPortalToken`. `PortalPage` and `UploadCard` now call `apiRequest` with `scheme: 'Portal'`.
- `PortalPage.test.tsx` mocks one spy on `apiRequest`, routed by path, instead of two spies on two
  transports — `web/CLAUDE.md` asks for the network to be mocked at `apiClient`, not at `fetch`.
- The two transport assertions from `portalToken.test.ts` (the `Portal` scheme word, and multipart
  without a forced `Content-Type`) moved to `apiClient.test.ts`, where the transport now lives.
  Its third test duplicated an assertion `apiClient.test.ts` already made, so it was not carried
  over. `portalToken.test.ts` keeps the `readPortalToken` cases.

Verified after the change: `typecheck` clean, `lint` clean, `test` 45 passed in 6 files.

`web/CLAUDE.md` now documents the `scheme` option and the FormData behaviour, so the next agent
does not rediscover the gap and write a second transport.
