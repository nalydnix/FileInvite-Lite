import { createContext, useContext } from 'react'

/**
 * The portal session's context object and its hook, kept apart from the provider component so
 * PortalSessionContext.tsx exports only a component (react-refresh/only-export-components).
 */
export interface PortalSessionState {
  /** The exchanged session token. In memory only: never storage, never a cookie (S9.4). */
  sessionToken: string | null
  expiresAt: string | null
  start: (sessionToken: string, expiresAt: string) => void
  end: () => void
}

export const PortalSessionContext = createContext<PortalSessionState | undefined>(undefined)

export function usePortalSession(): PortalSessionState {
  const context = useContext(PortalSessionContext)
  if (context === undefined) {
    throw new Error('usePortalSession must be used inside a PortalSessionProvider.')
  }
  return context
}

/** The magic-link token from a `#t=<token>` fragment, or null when there is none to use. */
export function readPortalToken(hash: string): string | null {
  const token = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash).get('t')
  return token === null || token === '' ? null : token
}
