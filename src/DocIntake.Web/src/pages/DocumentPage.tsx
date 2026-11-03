import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Title2,
  Title3,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components'
import {
  ArrowLeftRegular,
  ArrowUndoRegular,
  ArrowUploadRegular,
  ChatRegular,
  CopyRegular,
  DeleteRegular,
} from '@fluentui/react-icons'
import { useState } from 'react'
import { Link, Navigate, useLocation, useNavigate, useParams } from 'react-router-dom'
import { useApi } from '../api/apiContext'
import { ApiError } from '../api/client'
import type { DocumentStatusResponse } from '../api/types'
import { DocumentLookup } from '../components/DocumentLookup'
import { StatusBadge } from '../components/StatusBadge'
import { StatusTimeline } from '../components/StatusTimeline'
import { useDocumentStatus } from '../hooks/useDocumentStatus'
import { canDelete, canRestore, normalizeDocumentId, statusDescriptions } from '../lib/documentStatus'
import { formatDateTime } from '../lib/format'

const useStyles = makeStyles({
  back: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorBrandForegroundLink,
    textDecoration: 'none',
    ':hover': { textDecoration: 'underline' },
  },
  heading: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  idRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontFamily: tokens.fontFamilyMonospace,
  },
  card: {
    padding: tokens.spacingHorizontalXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  details: {
    display: 'grid',
    gridTemplateColumns: 'max-content 1fr',
    columnGap: tokens.spacingHorizontalXXL,
    rowGap: tokens.spacingVerticalS,
    margin: 0,
  },
  term: {
    color: tokens.colorNeutralForeground3,
  },
  definition: {
    margin: 0,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
})

interface LocationState {
  fileName?: string
  justUploaded?: boolean
}

export function DocumentPage() {
  const { documentId: rawId = '' } = useParams()
  const documentId = normalizeDocumentId(rawId)
  if (!documentId) return <Navigate to="/not-found" replace />
  return <DocumentView key={documentId} documentId={documentId} />
}

function DocumentView({ documentId }: { documentId: string }) {
  const styles = useStyles()
  const location = useLocation()
  const { fileName, justUploaded } = (location.state as LocationState | null) ?? {}
  const { document, error, loading, polling, refresh } = useDocumentStatus(documentId)
  const [copied, setCopied] = useState(false)

  const copyId = async () => {
    try {
      await navigator.clipboard.writeText(documentId)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // Clipboard access can be blocked; the ID stays visible for manual copying.
    }
  }

  return (
    <>
      <Link to="/" className={styles.back}>
        <ArrowLeftRegular aria-hidden /> Upload another document
      </Link>

      <div className={styles.heading}>
        <Title2 as="h1">{fileName ?? 'Document'}</Title2>
        <div className={styles.idRow}>
          <Text size={200}>ID: {documentId}</Text>
          <Tooltip content={copied ? 'Copied' : 'Copy document ID'} relationship="label">
            <Button size="small" appearance="subtle" icon={<CopyRegular />} onClick={() => void copyId()} />
          </Tooltip>
        </div>
      </div>

      {justUploaded && document && document.status !== 'deleted' && (
        <MessageBar intent="success">
          <MessageBarBody>
            Upload received. Keep this document ID to check on it later; this page updates automatically while
            the document is processed.
          </MessageBarBody>
        </MessageBar>
      )}

      {loading && !document && <Spinner label="Loading document status…" />}

      {error && error.kind === 'notFound' && <NotFound />}

      {error && error.kind !== 'notFound' && error.kind !== 'unauthorized' && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Could not load the document status</MessageBarTitle>
            {error.message}
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={refresh}>Retry</Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {document && (!error || error.kind !== 'notFound') && (
        <DocumentDetails document={document} polling={polling} onChanged={refresh} />
      )}
    </>
  )
}

function NotFound() {
  const styles = useStyles()
  return (
    <Card className={styles.card}>
      <Title3 as="h2">Document not found</Title3>
      <Text>
        No document with this ID exists for your account. It may belong to another user, the ID may be mistyped, or
        it may have been permanently removed after its recovery window ended.
      </Text>
      <DocumentLookup />
    </Card>
  )
}

interface DocumentDetailsProps {
  document: DocumentStatusResponse
  polling: boolean
  onChanged: () => void
}

function DocumentDetails({ document, polling, onChanged }: DocumentDetailsProps) {
  const styles = useStyles()
  const api = useApi()
  const navigate = useNavigate()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [busy, setBusy] = useState<'delete' | 'restore' | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  const run = async (action: 'delete' | 'restore') => {
    setBusy(action)
    setActionError(null)
    try {
      if (action === 'delete') await api.deleteDocument(document.documentId)
      else await api.restoreDocument(document.documentId)
      onChanged()
    } catch (error) {
      if (error instanceof ApiError && error.kind === 'unauthorized') return
      if (error instanceof ApiError && error.kind === 'notFound') {
        setActionError(
          action === 'restore'
            ? 'The document can no longer be restored; its recovery window may have ended.'
            : 'The document no longer exists.',
        )
        onChanged()
      } else {
        setActionError(error instanceof ApiError ? error.message : 'The action failed. Please retry.')
      }
    } finally {
      setBusy(null)
      setConfirmDelete(false)
    }
  }

  const restorable = canRestore(document)

  return (
    <>
      <Card className={styles.card}>
        <div className={styles.statusRow}>
          <StatusBadge status={document.status} />
          <Text>{statusDescriptions[document.status]}</Text>
          {polling && <Spinner size="tiny" label="Checking for updates…" labelPosition="after" />}
        </div>

        {document.status !== 'deleted' && <StatusTimeline processingStatus={document.processingStatus} />}

        {document.status === 'failed' && document.failure && (
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Processing failed</MessageBarTitle>
              {document.failure.message}
            </MessageBarBody>
            <MessageBarActions>
              <Button icon={<ArrowUploadRegular />} onClick={() => navigate('/')}>
                {document.failure.retryable ? 'Upload again' : 'Upload a different file'}
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {document.status === 'noText' && (
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>No text found</MessageBarTitle>
              The document cannot be used to answer questions. If it is a scan, upload a version with selectable
              text.
            </MessageBarBody>
          </MessageBar>
        )}

        {document.status === 'deleted' && (
          <MessageBar intent={restorable ? 'info' : 'warning'}>
            <MessageBarBody>
              <MessageBarTitle>Deleted {formatDateTime(document.deletedAt)}</MessageBarTitle>
              {restorable
                ? `You can restore it until ${formatDateTime(document.recoverableUntil)}.`
                : 'The recovery window has ended; the document can no longer be restored.'}
            </MessageBarBody>
          </MessageBar>
        )}

        {actionError && (
          <MessageBar intent="error">
            <MessageBarBody>{actionError}</MessageBarBody>
          </MessageBar>
        )}

        <dl className={styles.details}>
          <Detail term="Uploaded" value={formatDateTime(document.createdAt)} />
          <Detail term="Last status change" value={formatDateTime(document.statusUpdatedAt)} />
          <Detail term="Indexed" value={formatDateTime(document.indexedAt)} />
          <Detail term="Indexed chunks" value={String(document.chunkCount)} />
          {document.deletedAt && <Detail term="Deleted" value={formatDateTime(document.deletedAt)} />}
          {document.recoverableUntil && (
            <Detail term="Recoverable until" value={formatDateTime(document.recoverableUntil)} />
          )}
          {document.failure && <Detail term="Failure code" value={document.failure.code} />}
        </dl>

        <div className={styles.actions}>
          {document.status === 'indexed' && (
            <Button appearance="primary" icon={<ChatRegular />} onClick={() => navigate('/ask')}>
              Ask about your documents
            </Button>
          )}
          {restorable && (
            <Button
              appearance="primary"
              icon={<ArrowUndoRegular />}
              disabled={busy !== null}
              onClick={() => void run('restore')}
            >
              {busy === 'restore' ? 'Restoring…' : 'Restore'}
            </Button>
          )}
          {canDelete(document) && (
            <Button icon={<DeleteRegular />} disabled={busy !== null} onClick={() => setConfirmDelete(true)}>
              Delete
            </Button>
          )}
        </div>
      </Card>

      <Dialog open={confirmDelete} onOpenChange={(_, data) => !busy && setConfirmDelete(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete this document?</DialogTitle>
            <DialogContent>
              The document is removed from search results and answers immediately. You can restore it from this page
              until its recovery window ends; after that it is permanently removed.
            </DialogContent>
            <DialogActions>
              <Button disabled={busy !== null} onClick={() => setConfirmDelete(false)}>
                Cancel
              </Button>
              <Button appearance="primary" disabled={busy !== null} onClick={() => void run('delete')}>
                {busy === 'delete' ? 'Deleting…' : 'Delete'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  )
}

function Detail({ term, value }: { term: string; value: string }) {
  const styles = useStyles()
  return (
    <>
      <dt className={styles.term}>{term}</dt>
      <dd className={styles.definition}>{value}</dd>
    </>
  )
}
