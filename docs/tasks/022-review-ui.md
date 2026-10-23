# 022: Review UI — the staff reviewer's panel
**Phase:** 2   **Depends on:** 020-phase2-schema   **Parallel-safe with:** 021, 023, 024, 025

## Goal
The staff-side panel on a request's detail page: see each item, download the uploaded file,
approve it, or reject it with a comment.

## Working without the API

Task 021 builds the review endpoints in parallel with this one, so they will not exist while you
work. Build against `docs/api-contract.md` §Review — it is the agreed shape and it is
authoritative. Mock at `apiClient` in tests, exactly as `web/tests/RequestListPage.test.tsx` does.
**Do not** wait for 021 and do not read anything under `api/`. If the contract is unclear, write
the question in Progress notes and take the contract's most literal reading.

## Scope

**May touch**
- `web/src/features/review/` — `ReviewPanel.tsx`, and any supporting files
- `web/src/features/requests/RequestDetailPage.tsx` — to mount the panel. **This is the only file
  outside your feature folder you may edit, and only to render the panel and pass it props.**
- `web/tests/ReviewPanel.test.tsx`
- `web/src/index.css` — additions only, no edits to existing rules

**Must not touch**
`api/**`, `web/src/features/portal/**`, `web/src/features/auth/**`, `web/src/features/audit/**`,
`web/src/app/**` (including `routes.tsx` and `apiClient.ts`), `package.json`,
`docs/api-contract.md`.

> `web/src/app/routes.tsx` is off-limits because task 024 needs it too. The panel mounts inside
> `RequestDetailPage`, which is already routed, so you do not need a new route. If you think you
> do, stop and say so in Progress notes.

## Decisions (already made — implement, do not revisit)

- **No new dependency.** Use the existing `useApi` hook and `apiClient`.
- **The reject comment is required in the UI**, not only on the server: the submit control is
  disabled while the comment is empty or whitespace (S9.6). The server's `400` is still handled —
  client validation is a convenience, never the enforcement.
- **Download goes through `apiClient`** so the bearer header is attached, then is handed to the
  browser as a blob download with the original filename. No bare anchor pointing at the API,
  because that sends no `Authorization` header and is also how a token ends up in a URL.
- **After a decision the panel refetches** rather than guessing the new state, except that it may
  use the `requestStatus` the response already returns so the request header updates without a
  second round trip — that is why the contract returns it.
- A `409` ("already approved", "nothing uploaded") renders the problem `detail`, not the status
  code (S9.8).

## Acceptance criteria

- [ ] **S9.6** Staff can download a file, approve an item, and reject an item with a comment; the
      UI blocks submitting a reject with an empty or whitespace comment
- [ ] Each item shows its status distinctly, by something other than colour alone
- [ ] The latest uploaded file's name, size and upload time are shown; an item with no upload shows
      no download control
- [ ] **S9.8** `409` and `400` responses render the problem `detail` as a readable message, never a
      raw status code
- [ ] A decision in flight disables the controls and cannot be double-submitted
- [ ] Loading, empty and error states exist
- [ ] Controls are labelled and keyboard-operable; the result of a decision is announced to a
      screen reader
- [ ] The download control does not put a token in a URL
- [ ] `npm run typecheck` and `npm run lint` clean — no `any`, no non-null assertion to silence the
      compiler, no new eslint-disable

## Follow the pattern in

`web/src/features/requests/RequestListPage.tsx`, `web/src/features/requests/CreateRequestPage.tsx`,
`web/src/app/useApi.ts`, `web/tests/CreateRequestPage.test.tsx`.

## Done when

`npm --prefix web run typecheck`, `lint` and `test` all green, CI green. Note in your report that
it has not been run against a live API.

## Progress notes

(append here as you go)
