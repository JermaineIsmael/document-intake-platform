import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiClient, ApiError } from './client'

function mockFetch(response: Response | Error) {
  const fetchMock = vi.fn(async () => {
    if (response instanceof Error) throw response
    return response
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('ApiClient', () => {
  it('sends the bearer token and parses JSON', async () => {
    const fetchMock = mockFetch(Response.json({ documentId: 'id', status: 'indexed' }))
    const client = new ApiClient('https://api.example.com', async () => 'abc')

    const result = await client.getDocument('id')

    expect(result.status).toBe('indexed')
    expect(fetchMock).toHaveBeenCalledWith(
      'https://api.example.com/documents/id',
      expect.objectContaining({ method: 'GET', headers: { Authorization: 'Bearer abc' } }),
    )
  })

  it('omits the Authorization header when no token is available', async () => {
    const fetchMock = mockFetch(new Response(null, { status: 204 }))
    const client = new ApiClient('/api', async () => null)

    await client.deleteDocument('id')

    expect(fetchMock).toHaveBeenCalledWith('/api/documents/id', expect.objectContaining({ headers: {} }))
  })

  it('posts the question as JSON', async () => {
    const fetchMock = mockFetch(Response.json({ answer: 'a', sources: [] }))
    const client = new ApiClient('/api', async () => 't')

    await client.ask('What?')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/ask',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ question: 'What?' }),
        headers: { Authorization: 'Bearer t', 'Content-Type': 'application/json' },
      }),
    )
  })

  it('surfaces the API error message and classifies the status', async () => {
    mockFetch(Response.json({ error: 'Chat is disabled for this environment.' }, { status: 503 }))
    const client = new ApiClient('/api', async () => 't')

    const error = await client.ask('q').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 503, kind: 'unavailable', message: 'Chat is disabled for this environment.' })
  })

  it('falls back to a default message for non-JSON errors', async () => {
    mockFetch(new Response('Bad gateway', { status: 502 }))
    const client = new ApiClient('/api', async () => 't')

    await expect(client.getDocument('id')).rejects.toMatchObject({ kind: 'server', message: expect.any(String) })
  })

  it('notifies on 401 so the app can show the session-expired page', async () => {
    mockFetch(new Response(null, { status: 401 }))
    const onAuthFailure = vi.fn()
    const client = new ApiClient('/api', async () => 't', onAuthFailure)

    await expect(client.getDocument('id')).rejects.toMatchObject({ kind: 'unauthorized' })
    expect(onAuthFailure).toHaveBeenCalledWith(401)
  })

  it('notifies on 403 so the app can show the forbidden page', async () => {
    mockFetch(new Response(null, { status: 403 }))
    const onAuthFailure = vi.fn()
    const client = new ApiClient('/api', async () => 't', onAuthFailure)

    await expect(client.ask('q')).rejects.toMatchObject({ kind: 'forbidden' })
    expect(onAuthFailure).toHaveBeenCalledWith(403)
  })

  it('reports network failures as ApiError', async () => {
    mockFetch(new TypeError('Failed to fetch'))
    const client = new ApiClient('/api', async () => 't')

    await expect(client.getDocument('id')).rejects.toMatchObject({ kind: 'network', status: 0 })
  })

  it('treats 404 as notFound', async () => {
    mockFetch(new Response(null, { status: 404 }))
    const client = new ApiClient('/api', async () => 't')

    await expect(client.restoreDocument('id')).rejects.toMatchObject({ kind: 'notFound' })
  })

  it('treats a token acquisition failure as an expired session', async () => {
    const fetchMock = mockFetch(Response.json({}))
    const onAuthFailure = vi.fn()
    const client = new ApiClient(
      '/api',
      async () => {
        throw new Error('timed_out')
      },
      onAuthFailure,
    )

    await expect(client.getDocument('id')).rejects.toMatchObject({ kind: 'unauthorized' })
    expect(onAuthFailure).toHaveBeenCalledWith(401)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('does not send an upload cancelled while the token was being acquired', async () => {
    const send = vi.fn()
    vi.stubGlobal(
      'XMLHttpRequest',
      vi.fn(function (this: Record<string, unknown>) {
        Object.assign(this, { open: vi.fn(), setRequestHeader: vi.fn(), send, upload: {} })
      }),
    )
    const controller = new AbortController()
    const client = new ApiClient('/api', async () => {
      controller.abort()
      return 't'
    })

    await expect(
      client.uploadDocument(new File(['%PDF-'], 'a.pdf'), { signal: controller.signal }),
    ).rejects.toMatchObject({ name: 'AbortError' })
    expect(send).not.toHaveBeenCalled()
  })
})
