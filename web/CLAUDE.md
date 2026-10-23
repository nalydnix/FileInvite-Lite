# web/ — frontend conventions

React + TypeScript + Vite. Read the root `CLAUDE.md` first; this file adds only what is specific to
the frontend. Layout lives in `docs/architecture.md` §8.

## Commands

```bash
npm ci
npm run dev          # http://localhost:5173
npm run build
npm run test         # Vitest
npm run typecheck    # tsc --noEmit
npm run lint
```

The API is expected at `VITE_API_URL` (`http://localhost:5080` in development).

## Structure

```
src/
├── app/            AuthContext, PortalSessionContext, apiClient, routes
├── components/     shared presentational components only
└── features/
    ├── auth/  requests/  review/  audit/  portal/
    └── <feature>/  pages + components + routes.ts
```

- **Each feature exports its own routes** from `features/<name>/routes.ts`. Adding a page touches
  one feature folder plus one import line in `app/routes.tsx`.
- A feature never imports from another feature. Shared UI goes to `components/`, shared state to
  `app/`.
- Components are function components with typed props. No default exports except pages.

## Rules

### Network

- **All calls go through `app/apiClient.ts`.** No component calls `fetch` directly — the bearer
  header, the base URL and the problem-details parsing live in exactly one place.
- The API returns RFC 7807 problem details (`docs/api-contract.md`). `apiClient` turns them into a
  typed error; components show `detail`, never a raw status code (S9.8).
- Never put a token, an email or any personal data in a URL, a query string or an analytics call.

### Tokens

- **The staff JWT lives in `AuthContext`, in memory only.** Never `localStorage`, never
  `sessionStorage`, never a cookie (S9.2, ADR 0003). A reload logging the user out is intended
  behaviour, not a bug to fix.
- **The portal reads the magic-link token from `window.location.hash`, exchanges it once, then
  strips the fragment** with `history.replaceState` before any further navigation (S9.3).
- The magic-link token is never stored anywhere. Only the exchanged portal session token is held,
  in memory (S9.4).
- A `401` clears the in-memory token and returns the user to login (staff) or an
  "ask for a new link" message (portal).

### State and data

- Local component state by default; context only for auth and portal session.
- No global state library in the MVP.
- Dates arrive as UTC ISO-8601 strings and are formatted at the edge, in the component.

### Types

- `strict` is on. No `any`, no non-null assertion to silence the compiler.
- API response types are declared next to the feature that calls the endpoint, matching
  `docs/api-contract.md`. If the shape disagrees with the contract, the contract is right — stop
  and report it rather than adapting the type.

### Accessibility and UI

- Labelled form controls, visible focus, keyboard-operable actions.
- Errors are announced, not only coloured.
- Loading and empty states exist for every list and every async action.
- The reject dialog blocks submission with an empty comment (S9.6).

## Tests

`web/tests/`, Vitest + React Testing Library.

- Query by role and accessible name, not by test id or class.
- Mock the network at `apiClient`, not at `fetch`.
- Test behaviour, not implementation: "shows the rejection comment on a rejected item", not
  "calls setState".
- Name tests after the criterion where there is one: `portal strips the token fragment after
  exchange (S9.3)`.

**Never weaken or delete a test to get to green.**
