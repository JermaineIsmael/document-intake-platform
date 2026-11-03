import { FluentProvider, webLightTheme } from '@fluentui/react-components'
import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { vi } from 'vitest'
import { ApiContext } from '../api/apiContext'
import type { ApiClient } from '../api/client'
import { AuthContext, type AuthState } from '../auth/authContext'
import type { AppConfig } from '../config'
import { ConfigContext } from '../configContext'

export const testConfig: AppConfig = {
  apiBaseUrl: '/api',
  authEnabled: true,
  clientId: 'client-id',
  authority: 'https://login.microsoftonline.com/tenant/v2.0',
  apiScope: 'api://client-id/access_as_user',
  maxUploadMb: 25,
}

export function fakeAuth(overrides: Partial<AuthState> = {}): AuthState {
  return {
    enabled: true,
    isAuthenticated: true,
    user: { name: 'Test User', username: 'test@example.com' },
    signIn: vi.fn(async () => {}),
    signOut: vi.fn(async () => {}),
    getAccessToken: vi.fn(async () => 'token'),
    ...overrides,
  }
}

export function fakeApi(overrides: Partial<Record<keyof ApiClient, unknown>> = {}): ApiClient {
  return {
    getDocument: vi.fn(),
    deleteDocument: vi.fn(),
    restoreDocument: vi.fn(),
    ask: vi.fn(),
    uploadDocument: vi.fn(),
    ...overrides,
  } as unknown as ApiClient
}

export function LocationProbe() {
  const location = useLocation()
  return <div data-testid="location">{location.pathname + location.search}</div>
}

interface RenderOptions {
  route?: string | { pathname: string; state?: unknown }
  path?: string
  api?: ApiClient
  auth?: AuthState
}

export function renderPage(element: ReactElement, options: RenderOptions = {}) {
  const api = options.api ?? fakeApi()
  const auth = options.auth ?? fakeAuth()
  const route = options.route ?? '/'

  const result = render(
    <FluentProvider theme={webLightTheme}>
      <ConfigContext.Provider value={testConfig}>
        <AuthContext.Provider value={auth}>
          <ApiContext.Provider value={api}>
            <MemoryRouter initialEntries={[route]}>
              <Routes>
                <Route path={options.path ?? '/'} element={element} />
                <Route path="*" element={<LocationProbe />} />
              </Routes>
            </MemoryRouter>
          </ApiContext.Provider>
        </AuthContext.Provider>
      </ConfigContext.Provider>
    </FluentProvider>,
  )
  return { ...result, api, auth }
}

export function pdfFile(name = 'report.pdf', size?: number): File {
  const header = new TextEncoder().encode('%PDF-1.7\n')
  const body = size && size > header.length ? new Uint8Array(size - header.length) : new Uint8Array(0)
  return new File([header, body], name, { type: 'application/pdf' })
}
