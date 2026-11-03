export interface AppConfig {
  apiBaseUrl: string
  authEnabled: boolean
  clientId: string
  authority: string
  apiScope: string
  maxUploadMb: number
}

function readBoolean(value: string | undefined, fallback: boolean): boolean {
  if (value === undefined || value.trim() === '') return fallback
  return value.trim().toLowerCase() === 'true'
}

export function loadConfig(env: Record<string, string | undefined> = import.meta.env): AppConfig {
  const authEnabled = readBoolean(env.VITE_AUTH_ENABLED, true)
  const clientId = env.VITE_AUTH_CLIENT_ID?.trim() ?? ''
  const authority = env.VITE_AUTH_AUTHORITY?.trim() ?? ''
  const apiScope = env.VITE_API_SCOPE?.trim() || (clientId ? `api://${clientId}/access_as_user` : '')
  const maxUploadMb = Number(env.VITE_MAX_UPLOAD_MB ?? '25')

  if (authEnabled && (!clientId || !authority)) {
    throw new Error(
      'VITE_AUTH_CLIENT_ID and VITE_AUTH_AUTHORITY are required when VITE_AUTH_ENABLED is not false.',
    )
  }
  if (authEnabled && !authority.startsWith('https://')) {
    throw new Error('VITE_AUTH_AUTHORITY must be an absolute HTTPS URL.')
  }
  if (!Number.isFinite(maxUploadMb) || maxUploadMb <= 0) {
    throw new Error('VITE_MAX_UPLOAD_MB must be a positive number.')
  }

  return {
    apiBaseUrl: (env.VITE_API_BASE_URL ?? '/api').trim().replace(/\/+$/, ''),
    authEnabled,
    clientId,
    authority,
    apiScope,
    maxUploadMb,
  }
}
