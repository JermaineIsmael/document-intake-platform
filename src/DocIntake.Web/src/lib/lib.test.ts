import { describe, expect, it } from 'vitest'
import type { DocumentStatusResponse } from '../api/types'
import { canDelete, canRestore, isInFlight, normalizeDocumentId, timelineState } from './documentStatus'
import { validateFile } from './fileValidation'
import { parseSource } from './format'
import { safeReturnPath } from '../auth/authContext'
import { pdfFile } from '../test/render'

const baseDocument: DocumentStatusResponse = {
  documentId: '3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f',
  status: 'indexed',
  processingStatus: 'indexed',
  statusUpdatedAt: null,
  chunkCount: 4,
  createdAt: '2026-10-29T00:00:00Z',
  indexedAt: null,
  deletedAt: null,
  recoverableUntil: null,
  failure: null,
}

describe('documentStatus', () => {
  it('identifies in-flight statuses', () => {
    expect(isInFlight('queued')).toBe(true)
    expect(isInFlight('extracting')).toBe(true)
    expect(isInFlight('processing')).toBe(true)
    expect(isInFlight('indexed')).toBe(false)
    expect(isInFlight('failed')).toBe(false)
    expect(isInFlight('deleted')).toBe(false)
  })

  it('maps processing status to timeline steps', () => {
    expect(timelineState('queued')).toEqual(['current', 'pending', 'pending', 'pending'])
    expect(timelineState('processing')).toEqual(['done', 'done', 'current', 'pending'])
    expect(timelineState('indexed')).toEqual(['done', 'done', 'done', 'done'])
    expect(timelineState('noText')).toEqual(['done', 'done', 'done', 'error'])
    expect(timelineState('failed')).toEqual(['done', 'error', 'pending', 'pending'])
  })

  it('allows restore only for deleted documents inside the recovery window', () => {
    const now = new Date('2026-10-29T12:00:00Z')
    const deleted = { ...baseDocument, status: 'deleted' as const, deletedAt: '2026-10-29T00:00:00Z' }
    expect(canRestore({ ...deleted, recoverableUntil: '2026-11-28T00:00:00Z' }, now)).toBe(true)
    expect(canRestore({ ...deleted, recoverableUntil: '2026-10-28T00:00:00Z' }, now)).toBe(false)
    expect(canRestore({ ...deleted, recoverableUntil: null }, now)).toBe(false)
    expect(canRestore(baseDocument, now)).toBe(false)
    expect(canDelete(baseDocument)).toBe(true)
    expect(canDelete(deleted)).toBe(false)
  })

  it('normalizes document IDs', () => {
    expect(normalizeDocumentId(' 3F2B8C1E-5D4A-4E7B-9C0D-1A2B3C4D5E6F ')).toBe(baseDocument.documentId)
    expect(normalizeDocumentId('{3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f}')).toBe(baseDocument.documentId)
    expect(normalizeDocumentId('not-an-id')).toBeNull()
    expect(normalizeDocumentId('')).toBeNull()
  })
})

describe('validateFile', () => {
  it('accepts a PDF with a valid signature', async () => {
    expect(await validateFile(pdfFile(), 25)).toBeNull()
  })

  it('accepts a DOCX with a ZIP signature', async () => {
    const docx = new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x00])], 'notes.DOCX')
    expect(await validateFile(docx, 25)).toBeNull()
  })

  it('rejects unsupported extensions', async () => {
    expect(await validateFile(new File(['hello'], 'notes.txt'), 25)).toMatch(/Only PDF/)
  })

  it('rejects empty files', async () => {
    expect(await validateFile(new File([], 'empty.pdf'), 25)).toMatch(/empty/)
  })

  it('rejects files over the limit', async () => {
    expect(await validateFile(pdfFile('big.pdf', 2 * 1024 * 1024), 1)).toMatch(/1 MB limit/)
  })

  it('rejects content that does not match the extension', async () => {
    expect(await validateFile(new File(['not a pdf'], 'fake.pdf'), 25)).toMatch(/valid PDF/)
  })
})

describe('parseSource', () => {
  it('extracts the document ID and file name from a blob URL', () => {
    const source = parseSource(
      'https://acct.blob.core.windows.net/documents/3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f/Supplier%20Agreement.pdf',
      0,
    )
    expect(source).toEqual({
      label: 'source1',
      documentId: baseDocument.documentId,
      fileName: 'Supplier Agreement.pdf',
    })
  })

  it('falls back to the raw value for unexpected formats', () => {
    expect(parseSource('not a url', 1)).toEqual({ label: 'source2', documentId: null, fileName: 'not a url' })
    expect(parseSource('https://example.com/a/b.pdf', 0).documentId).toBeNull()
  })
})

describe('safeReturnPath', () => {
  it('only accepts same-app relative paths', () => {
    expect(safeReturnPath('/documents/1?x=1')).toBe('/documents/1?x=1')
    expect(safeReturnPath('//evil.example')).toBe('/')
    expect(safeReturnPath('https://evil.example')).toBe('/')
    expect(safeReturnPath('/\\evil.example')).toBe('/')
    expect(safeReturnPath(undefined)).toBe('/')
  })
})
