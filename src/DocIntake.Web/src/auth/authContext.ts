import { createContext, useContext } from 'react'

export interface AuthUser {
  name: string
  username: string
}

export interface AuthState {
  // False when the API runs with Development:BypassAuth and the SPA was built with VITE_AUTH_ENABLED=false.
  enabled: boolean
  isAuthenticated: boolean
  user: AuthUser | null
  signIn: (returnTo?: string) => Promise<void>
  signOut: () => Promise<void>
  getAccessToken: () => Promise<string | null>
}

export const AuthContext = createContext<AuthState | null>(null)

export function useAuth(): AuthState {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside an AuthProvider.')
  return value
}

// Only same-app relative paths are accepted, so a crafted redirect state cannot send users elsewhere.
export function safeReturnPath(value: unknown): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return '/'
  }
  return value
}
