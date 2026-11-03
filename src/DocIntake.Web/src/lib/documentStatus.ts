import type { DocumentStatus, DocumentStatusResponse, ProcessingStatus } from '../api/types'

export type BadgeColor = 'informative' | 'brand' | 'success' | 'warning' | 'danger' | 'subtle'

export const statusLabels: Record<DocumentStatus, string> = {
  queued: 'Queued',
  extracting: 'Extracting text',
  processing: 'Indexing',
  indexed: 'Ready',
  noText: 'No text found',
  failed: 'Failed',
  deleted: 'Deleted',
}

export const statusColors: Record<DocumentStatus, BadgeColor> = {
  queued: 'informative',
  extracting: 'brand',
  processing: 'brand',
  indexed: 'success',
  noText: 'warning',
  failed: 'danger',
  deleted: 'subtle',
}

export const statusDescriptions: Record<DocumentStatus, string> = {
  queued: 'The document was uploaded and is waiting to be processed.',
  extracting: 'Text is being extracted from the document.',
  processing: 'The extracted text is being split, embedded, and indexed.',
  indexed: 'The document has been indexed and can be used to answer questions.',
  noText: 'Processing finished, but no readable text was found (for example a scanned image without a text layer).',
  failed: 'The document could not be processed.',
  deleted: 'The document was deleted and is excluded from answers.',
}

const inFlight: ReadonlySet<DocumentStatus> = new Set(['queued', 'extracting', 'processing'])

export function isInFlight(status: DocumentStatus): boolean {
  return inFlight.has(status)
}

export const timelineSteps: readonly { status: ProcessingStatus; label: string }[] = [
  { status: 'queued', label: 'Queued' },
  { status: 'extracting', label: 'Extracting' },
  { status: 'processing', label: 'Indexing' },
  { status: 'indexed', label: 'Ready' },
]

export type StepState = 'done' | 'current' | 'pending' | 'error'

// Maps the processing status onto the four-step happy path. noText and failed are terminal
// outcomes; the API does not record which stage failed, so failure is shown after "Queued".
export function timelineState(processingStatus: ProcessingStatus): StepState[] {
  const order: ProcessingStatus[] = ['queued', 'extracting', 'processing', 'indexed']
  if (processingStatus === 'indexed') return ['done', 'done', 'done', 'done']
  if (processingStatus === 'noText') return ['done', 'done', 'done', 'error']
  if (processingStatus === 'failed') return ['done', 'error', 'pending', 'pending']
  const current = order.indexOf(processingStatus)
  return order.map((_, i) => (i < current ? 'done' : i === current ? 'current' : 'pending'))
}

export function canDelete(document: DocumentStatusResponse): boolean {
  return document.status !== 'deleted'
}

export function canRestore(document: DocumentStatusResponse, now: Date = new Date()): boolean {
  return (
    document.status === 'deleted' &&
    document.recoverableUntil !== null &&
    new Date(document.recoverableUntil).getTime() > now.getTime()
  )
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function normalizeDocumentId(value: string): string | null {
  const trimmed = value.trim().replace(/^\{|\}$/g, '')
  return guidPattern.test(trimmed) ? trimmed.toLowerCase() : null
}
