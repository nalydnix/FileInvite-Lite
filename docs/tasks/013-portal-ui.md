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
