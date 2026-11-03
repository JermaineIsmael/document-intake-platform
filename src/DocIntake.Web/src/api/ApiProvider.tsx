import { useMemo, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/authContext'
import { ApiContext } from './apiContext'
import { ApiClient } from './client'

export function ApiProvider({ baseUrl, children }: { baseUrl: string; children: ReactNode }) {
  const { getAccessToken, enabled } = useAuth()
  const navigate = useNavigate()

  const client = useMemo(
    () =>
      new ApiClient(baseUrl, getAccessToken, (status) => {
        // With auth bypassed these mean the API is not running in bypass mode; pages show the error inline.
        if (!enabled) return
        navigate(status === 401 ? '/session-expired' : '/forbidden', {
          replace: true,
          state: { from: window.location.pathname + window.location.search },
        })
      }),
    [baseUrl, getAccessToken, enabled, navigate],
  )

  return <ApiContext.Provider value={client}>{children}</ApiContext.Provider>
}
