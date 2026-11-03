import { useCallback, useEffect, useState } from 'react'
import { useApi } from '../api/apiContext'
import { ApiError } from '../api/client'
import type { DocumentStatusResponse } from '../api/types'
import { isInFlight } from '../lib/documentStatus'

export const fastPollMs = 3_000
export const slowPollMs = 10_000
// Most documents finish quickly; after this the page polls less often to limit API load.
export const fastPollWindowMs = 60_000

export interface DocumentStatusState {
  document: DocumentStatusResponse | null
  error: ApiError | null
  loading: boolean
  polling: boolean
  refresh: () => void
}

// Loads a document's status and keeps polling while it is queued, extracting, or processing.
export function useDocumentStatus(documentId: string): DocumentStatusState {
  const api = useApi()
  const [document, setDocument] = useState<DocumentStatusResponse | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [loading, setLoading] = useState(true)
  const [polling, setPolling] = useState(false)
  const [refreshKey, setRefreshKey] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    const startedAt = Date.now()
    let timer: ReturnType<typeof setTimeout> | undefined

    const load = async () => {
      try {
        const next = await api.getDocument(documentId, controller.signal)
        if (controller.signal.aborted) return
        setDocument(next)
        setError(null)
        const keepPolling = isInFlight(next.status)
        setPolling(keepPolling)
        if (keepPolling) {
          timer = setTimeout(load, Date.now() - startedAt < fastPollWindowMs ? fastPollMs : slowPollMs)
        }
      } catch (caught) {
        if (controller.signal.aborted) return
        setPolling(false)
        setError(caught instanceof ApiError ? caught : new ApiError(500, 'Could not load the document status.'))
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }

    void load()
    return () => {
      controller.abort()
      if (timer) clearTimeout(timer)
    }
  }, [api, documentId, refreshKey])

  const refresh = useCallback(() => {
    setLoading(true)
    setRefreshKey((key) => key + 1)
  }, [])

  return { document, error, loading, polling, refresh }
}
