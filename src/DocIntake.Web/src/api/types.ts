// Mirrors DocIntake.Shared.DocumentStatusResponse / DocumentAccepted and the /ask response.

export type ProcessingStatus = 'queued' | 'extracting' | 'processing' | 'indexed' | 'noText' | 'failed'

export type DocumentStatus = ProcessingStatus | 'deleted'

export interface DocumentFailure {
  code: string
  message: string
  retryable: boolean
}

export interface DocumentStatusResponse {
  documentId: string
  status: DocumentStatus
  processingStatus: ProcessingStatus
  statusUpdatedAt: string | null
  chunkCount: number
  createdAt: string
  indexedAt: string | null
  deletedAt: string | null
  recoverableUntil: string | null
  failure: DocumentFailure | null
}

export interface DocumentAccepted {
  documentId: string
  status: ProcessingStatus
}

export interface AskResponse {
  answer: string
  sources: string[]
}
