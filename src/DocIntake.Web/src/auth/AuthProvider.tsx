import type { IPublicClientApplication } from '@azure/msal-browser'
import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { AuthContext, safeReturnPath, type AuthState } from './authContext'
import { acquireApiToken } from './msal'

interface MsalAuthProviderProps {
  instance: IPublicClientApplication
  apiScope: string
  children: ReactNode
}

export function MsalAuthProvider({ instance, apiScope, children }: MsalAuthProviderProps) {
  const [account, setAccount] = useState(() => instance.getActiveAccount())

  const signIn = useCallback(
    async (returnTo?: string) => {
      await instance.loginRedirect({ scopes: [apiScope], state: safeReturnPath(returnTo) })
    },
    [instance, apiScope],
  )

  const signOut = useCallback(async () => {
    const current = instance.getActiveAccount()
    setAccount(null)
    await instance.logoutRedirect({ account: current ?? undefined })
  }, [instance])

  const getAccessToken = useCallback(
    () => acquireApiToken(instance, apiScope, instance.getActiveAccount()),
    [instance, apiScope],
  )

  const value = useMemo<AuthState>(
    () => ({
      enabled: true,
      isAuthenticated: account !== null,
      user: account ? { name: account.name ?? account.username, username: account.username } : null,
      signIn,
      signOut,
      getAccessToken,
    }),
    [account, signIn, signOut, getAccessToken],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// Local development against an API running with Development:BypassAuth=true. Requests carry no token.
export function DevAuthProvider({ children }: { children: ReactNode }) {
  const value = useMemo<AuthState>(
    () => ({
      enabled: false,
      isAuthenticated: true,
      user: { name: 'Local developer', username: 'auth disabled' },
      signIn: async () => {},
      signOut: async () => {},
      getAccessToken: async () => null,
    }),
    [],
  )
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
