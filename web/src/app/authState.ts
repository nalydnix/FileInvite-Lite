import { createContext, useContext } from 'react'

/**
 * The context object and its hook, kept apart from the provider component so that AuthContext.tsx
 * exports only a component (react-refresh/only-export-components).
 */
export interface AuthState {
  token: string | null
  name: string | null
  isAuthenticated: boolean
  signIn: (email: string, password: string) => Promise<void>
  signOut: () => void
}

export const AuthContext = createContext<AuthState | undefined>(undefined)

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (context === undefined) {
    throw new Error('useAuth must be used inside an AuthProvider.')
  }
  return context
}
