import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import type { DocumentStatusResponse } from '../api/types'
import { RequireAuth } from '../auth/RequireAuth'
import { fakeApi, fakeAuth, pdfFile, renderPage } from '../test/render'
import { AskPage } from './AskPage'
import { DocumentPage } from './DocumentPage'
import { SignInPage } from './SignInPage'
import { StatusPage } from './StatusPage'
import { UploadPage } from './UploadPage'

const documentId = '3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f'

function documentStatus(overrides: Partial<DocumentStatusResponse> = {}): DocumentStatusResponse {
  return {
    documentId,
    status: 'indexed',
    processingStatus: 'indexed',
    statusUpdatedAt: '2026-10-29T00:01:00Z',
    chunkCount: 7,
    createdAt: '2026-10-29T00:00:00Z',
    indexedAt: '2026-10-29T00:01:00Z',
    deletedAt: null,
    recoverableUntil: null,
    failure: null,
    ...overrides,
  }
}

describe('UploadPage', () => {
  it('rejects unsupported files before uploading', async () => {
    const user = userEvent.setup({ applyAccept: false })
    const { api } = renderPage(<UploadPage />)

    await user.upload(screen.getByLabelText('Choose a PDF or DOCX file'), new File(['x'], 'notes.txt'))

    expect(await screen.findByText(/Only PDF/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled()
    expect(api.uploadDocument).not.toHaveBeenCalled()
  })

  it('uploads a valid file and redirects to the document page', async () => {
    const user = userEvent.setup()
    const api = fakeApi({
      uploadDocument: vi.fn(async () => ({ documentId, status: 'queued' })),
    })
    renderPage(<UploadPage />, { api })

    await user.upload(screen.getByLabelText('Choose a PDF or DOCX file'), pdfFile('contract.pdf'))
    await user.click(await screen.findByRole('button', { name: 'Upload' }))

    expect(await screen.findByTestId('location')).toHaveTextContent(`/documents/${documentId}`)
    expect(api.uploadDocument).toHaveBeenCalledWith(expect.any(File), expect.any(Object))
  })

  it('shows the API error when the upload is rejected', async () => {
    const user = userEvent.setup()
    const api = fakeApi({
      uploadDocument: vi.fn(async () => {
        throw new ApiError(415, 'The file content does not match its extension.')
      }),
    })
    renderPage(<UploadPage />, { api })

    await user.upload(screen.getByLabelText('Choose a PDF or DOCX file'), pdfFile())
    await user.click(await screen.findByRole('button', { name: 'Upload' }))

    expect(await screen.findByText('The file content does not match its extension.')).toBeInTheDocument()
  })

  it('opens a document by ID and validates the format', async () => {
    const user = userEvent.setup()
    renderPage(<UploadPage />)

    await user.type(screen.getByLabelText('Document ID'), 'nope')
    await user.click(screen.getByRole('button', { name: 'Open' }))
    expect(await screen.findByText(/Enter a document ID/)).toBeInTheDocument()

    await user.clear(screen.getByLabelText('Document ID'))
    await user.type(screen.getByLabelText('Document ID'), documentId.toUpperCase())
    await user.click(screen.getByRole('button', { name: 'Open' }))
    expect(await screen.findByTestId('location')).toHaveTextContent(`/documents/${documentId}`)
  })
})

describe('DocumentPage', () => {
  const route = (state?: unknown) => ({ pathname: `/documents/${documentId}`, state })
  const path = '/documents/:documentId'

  it('shows an indexed document with its details', async () => {
    const api = fakeApi({ getDocument: vi.fn(async () => documentStatus()) })
    renderPage(<DocumentPage />, { api, route: route({ fileName: 'contract.pdf' }), path })

    expect(await screen.findByText(/has been indexed and can be used/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'contract.pdf' })).toBeInTheDocument()
    expect(screen.getByText('7')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Ask about your documents/ })).toBeInTheDocument()
  })

  it('explains failures and offers to upload again', async () => {
    const api = fakeApi({
      getDocument: vi.fn(async () =>
        documentStatus({
          status: 'failed',
          processingStatus: 'failed',
          failure: { code: 'ExtractionFailed', message: 'Text extraction failed. Upload the document again.', retryable: true },
        }),
      ),
    })
    renderPage(<DocumentPage />, { api, route: route(), path })

    expect(await screen.findByText('Text extraction failed. Upload the document again.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Upload again/ })).toBeInTheDocument()
  })

  it('polls while the document is processing', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const getDocument = vi
        .fn()
        .mockResolvedValueOnce(documentStatus({ status: 'processing', processingStatus: 'processing', indexedAt: null }))
        .mockResolvedValue(documentStatus())
      renderPage(<DocumentPage />, { api: fakeApi({ getDocument }), route: route(), path })

      expect(await screen.findByText(/being split, embedded, and indexed/)).toBeInTheDocument()
      await vi.advanceTimersByTimeAsync(3_100)
      expect(await screen.findByText(/has been indexed and can be used/)).toBeInTheDocument()
      expect(getDocument).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('deletes after confirmation and offers restore', async () => {
    const user = userEvent.setup()
    const getDocument = vi
      .fn()
      .mockResolvedValueOnce(documentStatus())
      .mockResolvedValue(
        documentStatus({
          status: 'deleted',
          deletedAt: '2026-10-29T01:00:00Z',
          recoverableUntil: new Date(Date.now() + 86_400_000).toISOString(),
        }),
      )
    const api = fakeApi({ getDocument, deleteDocument: vi.fn(async () => {}), restoreDocument: vi.fn(async () => {}) })
    renderPage(<DocumentPage />, { api, route: route(), path })

    await user.click(await screen.findByRole('button', { name: 'Delete' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }))

    await waitFor(() => expect(api.deleteDocument).toHaveBeenCalledWith(documentId))
    await user.click(await screen.findByRole('button', { name: 'Restore' }))
    expect(api.restoreDocument).toHaveBeenCalledWith(documentId)
  })

  it('shows not found for unknown documents', async () => {
    const api = fakeApi({ getDocument: vi.fn(async () => Promise.reject(new ApiError(404, ''))) })
    renderPage(<DocumentPage />, { api, route: route(), path })

    expect(await screen.findByText('Document not found')).toBeInTheDocument()
  })

  it('redirects malformed IDs to the not-found page', async () => {
    renderPage(<DocumentPage />, { route: '/documents/not-a-guid', path })

    expect(await screen.findByTestId('location')).toHaveTextContent('/not-found')
  })
})

describe('AskPage', () => {
  it('shows the answer with links to its source documents', async () => {
    const user = userEvent.setup()
    const api = fakeApi({
      ask: vi.fn(async () => ({
        answer: 'The notice period is 30 days [source1].',
        sources: [`https://acct.blob.core.windows.net/documents/${documentId}/contract.pdf`],
      })),
    })
    renderPage(<AskPage />, { api })

    await user.type(screen.getByRole('textbox', { name: /Question/ }), 'What is the notice period?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    expect(await screen.findByText('The notice period is 30 days [source1].')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'contract.pdf' })).toHaveAttribute('href', `/documents/${documentId}`)
    expect(api.ask).toHaveBeenCalledWith('What is the notice period?', expect.any(AbortSignal))
  })

  it('explains when chat is disabled', async () => {
    const user = userEvent.setup()
    const api = fakeApi({
      ask: vi.fn(async () => {
        throw new ApiError(503, 'Chat is disabled for this environment.')
      }),
    })
    renderPage(<AskPage />, { api })

    await user.type(screen.getByRole('textbox', { name: /Question/ }), 'Hello?{Control>}{Enter}{/Control}')

    expect(await screen.findByText('Question answering is not available')).toBeInTheDocument()
    expect(screen.getByText(/Chat is disabled for this environment/)).toBeInTheDocument()
  })

  it('explains when no relevant content is found', async () => {
    const user = userEvent.setup()
    const api = fakeApi({
      ask: vi.fn(async () => {
        throw new ApiError(404, 'No relevant content found to answer your question.')
      }),
    })
    renderPage(<AskPage />, { api })

    await user.type(screen.getByRole('textbox', { name: /Question/ }), 'Unknown topic')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    expect(await screen.findByText('No relevant content found')).toBeInTheDocument()
  })
})

describe('authentication pages', () => {
  it('redirects signed-out users to sign-in with a return path', async () => {
    renderPage(
      <RequireAuth>
        <div>secret</div>
      </RequireAuth>,
      { route: '/ask', path: '/ask', auth: fakeAuth({ isAuthenticated: false, user: null }) },
    )

    expect(await screen.findByTestId('location')).toHaveTextContent('/signin?returnTo=%2Fask')
    expect(screen.queryByText('secret')).not.toBeInTheDocument()
  })

  it('starts sign-in with the sanitized return path', async () => {
    const user = userEvent.setup()
    const auth = fakeAuth({ isAuthenticated: false, user: null })
    renderPage(<SignInPage />, { route: '/signin?returnTo=%2F%2Fevil.example', path: '/signin', auth })

    await user.click(screen.getByRole('button', { name: /Sign in with Microsoft/ }))

    expect(auth.signIn).toHaveBeenCalledWith('/')
  })

  it('offers to sign in again after the session expires', async () => {
    const user = userEvent.setup()
    const auth = fakeAuth()
    renderPage(<StatusPage variant="sessionExpired" />, {
      route: { pathname: '/session-expired', state: { from: '/ask' } },
      path: '/session-expired',
      auth,
    })

    await user.click(screen.getByRole('button', { name: 'Sign in again' }))

    expect(auth.signIn).toHaveBeenCalledWith('/ask')
  })
})
