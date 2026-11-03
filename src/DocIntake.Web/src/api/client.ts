import type { AskResponse, DocumentAccepted, DocumentStatusResponse } from './types'

export type ApiErrorKind =
  | 'unauthorized'
  | 'forbidden'
  | 'notFound'
  | 'validation'
  | 'unavailable'
  | 'server'
  | 'network'

export class ApiError extends Error {
  readonly status: number
  readonly kind: ApiErrorKind

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.kind = classify(status)
  }
}

function classify(status: number): ApiErrorKind {
  if (status === 0) return 'network'
  if (status === 401) return 'unauthorized'
  if (status === 403) return 'forbidden'
  if (status === 404) return 'notFound'
  if (status === 503) return 'unavailable'
  if (status >= 400 && status < 500) return 'validation'
  return 'server'
}

const defaultMessages: Record<ApiErrorKind, string> = {
  unauthorized: 'Your session has expired. Please sign in again.',
  forbidden: 'You are not allowed to perform this action.',
  notFound: 'The requested item was not found.',
  validation: 'The request was rejected.',
  unavailable: 'The service is temporarily unavailable. Please retry.',
  server: 'Something went wrong on the server. Please retry.',
  network: 'Could not reach the API. Check your connection and retry.',
}

function parseErrorBody(status: number, body: string): ApiError {
  let message = ''
  try {
    const parsed: unknown = JSON.parse(body)
    if (parsed && typeof parsed === 'object' && 'error' in parsed && typeof parsed.error === 'string') {
      message = parsed.error
    }
  } catch {
    // Non-JSON error bodies (for example proxy errors) fall back to the default message.
  }
  return new ApiError(status, message || defaultMessages[classify(status)])
}

export type TokenProvider = () => Promise<string | null>

export type AuthFailureHandler = (status: 401 | 403) => void

export interface UploadOptions {
  onProgress?: (percent: number) => void
  signal?: AbortSignal
}

export class ApiClient {
  private readonly baseUrl: string
  private readonly getToken: TokenProvider
  private readonly onAuthFailure: AuthFailureHandler

  constructor(baseUrl: string, getToken: TokenProvider, onAuthFailure: AuthFailureHandler = () => {}) {
    this.baseUrl = baseUrl
    this.getToken = getToken
    this.onAuthFailure = onAuthFailure
  }

  getDocument(documentId: string, signal?: AbortSignal): Promise<DocumentStatusResponse> {
    return this.request<DocumentStatusResponse>(
      'GET',
      `/documents/${encodeURIComponent(documentId)}`,
      undefined,
      signal,
    )
  }

  async deleteDocument(documentId: string): Promise<void> {
    await this.request<void>('DELETE', `/documents/${encodeURIComponent(documentId)}`)
  }

  async restoreDocument(documentId: string): Promise<void> {
    await this.request<void>('POST', `/documents/${encodeURIComponent(documentId)}/restore`)
  }

  ask(question: string, signal?: AbortSignal): Promise<AskResponse> {
    return this.request<AskResponse>('POST', '/ask', { question }, signal)
  }

  // XMLHttpRequest is used instead of fetch because fetch cannot report upload progress.
  async uploadDocument(file: File, options: UploadOptions = {}): Promise<DocumentAccepted> {
    const token = await this.acquireToken()
    // An abort during token acquisition fires before the XHR listener below is attached.
    if (options.signal?.aborted) throw new DOMException('Upload cancelled.', 'AbortError')
    const form = new FormData()
    form.append('file', file, file.name)

    return new Promise<DocumentAccepted>((resolve, reject) => {
      const xhr = new XMLHttpRequest()
      xhr.open('POST', `${this.baseUrl}/documents`)
      if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable) options.onProgress?.(Math.round((event.loaded / event.total) * 100))
      }
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            resolve(JSON.parse(xhr.responseText) as DocumentAccepted)
          } catch {
            reject(new ApiError(500, 'The API returned an unexpected response.'))
          }
          return
        }
        if (xhr.status === 401 || xhr.status === 403) this.onAuthFailure(xhr.status)
        reject(parseErrorBody(xhr.status, xhr.responseText))
      }
      xhr.onerror = () => reject(new ApiError(0, defaultMessages.network))
      xhr.onabort = () => reject(new DOMException('Upload cancelled.', 'AbortError'))
      options.signal?.addEventListener('abort', () => xhr.abort(), { once: true })
      xhr.send(form)
    })
  }

  // A token that cannot be acquired (for example a failed silent renewal) is treated like an expired session.
  private async acquireToken(): Promise<string | null> {
    try {
      return await this.getToken()
    } catch {
      this.onAuthFailure(401)
      throw new ApiError(401, defaultMessages.unauthorized)
    }
  }

  private async request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
    const token = await this.acquireToken()
    const headers: Record<string, string> = {}
    if (token) headers.Authorization = `Bearer ${token}`
    if (body !== undefined) headers['Content-Type'] = 'application/json'

    let response: Response
    try {
      response = await fetch(`${this.baseUrl}${path}`, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal,
      })
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') throw error
      throw new ApiError(0, defaultMessages.network)
    }

    if (!response.ok) {
      if (response.status === 401 || response.status === 403) this.onAuthFailure(response.status)
      throw parseErrorBody(response.status, await response.text())
    }
    if (response.status === 204) return undefined as T
    return (await response.json()) as T
  }
}
