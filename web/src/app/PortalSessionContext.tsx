import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { PortalSessionContext, type PortalSessionState } from './portalSessionState'

/**
 * The client's portal session, held in memory and nowhere else (S9.4). Neither the magic-link
 * token nor this session token is ever written to storage or a cookie; reloading the page means
 * using the link again.
 */
export function PortalSessionProvider({ children }: { children: ReactNode }) {
  const [sessionToken, setSessionToken] = useState<string | null>(null)
  const [expiresAt, setExpiresAt] = useState<string | null>(null)

  const start = useCallback((token: string, expires: string) => {
    setSessionToken(token)
    setExpiresAt(expires)
  }, [])

  const end = useCallback(() => {
    setSessionToken(null)
    setExpiresAt(null)
  }, [])

  const value = useMemo<PortalSessionState>(
    () => ({ sessionToken, expiresAt, start, end }),
    [sessionToken, expiresAt, start, end],
  )

  return <PortalSessionContext.Provider value={value}>{children}</PortalSessionContext.Provider>
}
