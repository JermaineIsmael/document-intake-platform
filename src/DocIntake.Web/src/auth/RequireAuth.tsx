import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useAuth } from './authContext'

export function RequireAuth({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth()
  const location = useLocation()
  if (!isAuthenticated) {
    const returnTo = location.pathname + location.search
    return <Navigate to={`/signin?returnTo=${encodeURIComponent(returnTo)}`} replace />
  }
  return <>{children}</>
}
