import { describe, expect, it } from 'vitest'
import { loadConfig } from './config'

describe('loadConfig', () => {
  const authEnv = {
    VITE_AUTH_CLIENT_ID: 'abc',
    VITE_AUTH_AUTHORITY: 'https://login.microsoftonline.com/tenant/v2.0',
  }

  it('defaults the API scope from the client ID and the base URL to the dev proxy', () => {
    const config = loadConfig(authEnv)
    expect(config.authEnabled).toBe(true)
    expect(config.apiScope).toBe('api://abc/access_as_user')
    expect(config.apiBaseUrl).toBe('/api')
    expect(config.maxUploadMb).toBe(25)
  })

  it('uses an explicit scope and trims a trailing slash from the API URL', () => {
    const config = loadConfig({
      ...authEnv,
      VITE_API_SCOPE: 'api://custom/scope',
      VITE_API_BASE_URL: 'https://api.example.com/',
    })
    expect(config.apiScope).toBe('api://custom/scope')
    expect(config.apiBaseUrl).toBe('https://api.example.com')
  })

  it('requires Entra settings when auth is enabled', () => {
    expect(() => loadConfig({})).toThrow(/VITE_AUTH_CLIENT_ID/)
    expect(() => loadConfig({ ...authEnv, VITE_AUTH_AUTHORITY: 'http://insecure' })).toThrow(/HTTPS/)
  })

  it('allows missing Entra settings when auth is disabled', () => {
    expect(loadConfig({ VITE_AUTH_ENABLED: 'false' }).authEnabled).toBe(false)
  })

  it('rejects an invalid upload limit', () => {
    expect(() => loadConfig({ VITE_AUTH_ENABLED: 'false', VITE_MAX_UPLOAD_MB: '0' })).toThrow(/MAX_UPLOAD/)
  })
})
