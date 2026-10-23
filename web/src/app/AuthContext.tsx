import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { api } from './apiClient'
import { AuthContext, type AuthState } from './authState'

/**
 * The staff session, held in memory and nowhere else.
 *
 * Not localStorage, not sessionStorage, not a cookie (S9.2, ADR 0003). A reload therefore logs
 * the user out. That is the intended behaviour and the price of having no token an injected
 * script can read back later — do not "fix" it by persisting the token.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(null)
  const [name, setName] = useState<string | null>(null)

  const signIn = useCallback(async (email: string, password: string) => {
    const result = await api.login(email, password)
    setToken(result.token)
    setName(result.name)
  }, [])

  const signOut = useCallback(() => {
    setToken(null)
    setName(null)
  }, [])

  const value = useMemo<AuthState>(
    () => ({ token, name, isAuthenticated: token !== null, signIn, signOut }),
    [token, name, signIn, signOut],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
