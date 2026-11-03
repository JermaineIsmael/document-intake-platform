import { normalizeDocumentId } from './documentStatus'

export interface SourceReference {
  label: string
  documentId: string | null
  fileName: string
}

// /ask returns each source as the private blob URL of the original upload:
// https://<account>.blob.core.windows.net/<container>/<documentId>/<fileName>. The URL is not
// browsable (storage is private), so only the document ID and file name are shown.
export function parseSource(source: string, index: number): SourceReference {
  const label = `source${index + 1}`
  try {
    const segments = new URL(source).pathname.split('/').filter(Boolean).map(decodeURIComponent)
    for (let i = 0; i < segments.length - 1; i++) {
      const documentId = normalizeDocumentId(segments[i])
      if (documentId) {
        return { label, documentId, fileName: segments.slice(i + 1).join('/') }
      }
    }
    return { label, documentId: null, fileName: segments.at(-1) ?? source }
  } catch {
    return { label, documentId: null, fileName: source }
  }
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? '—'
    : date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
