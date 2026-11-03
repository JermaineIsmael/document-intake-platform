import {
  Button,
  Card,
  CardHeader,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Text,
  Title2,
  Title3,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components'
import { ArrowUploadRegular, DismissRegular, DocumentRegular } from '@fluentui/react-icons'
import { useEffect, useRef, useState, type DragEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useApi } from '../api/apiContext'
import { ApiError } from '../api/client'
import { DocumentLookup } from '../components/DocumentLookup'
import { useConfig } from '../configContext'
import { allowedExtensions, formatFileSize, validateFile } from '../lib/fileValidation'

const useStyles = makeStyles({
  card: {
    padding: tokens.spacingHorizontalXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  dropZone: {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalXXXL} ${tokens.spacingHorizontalXL}`,
    border: `2px dashed ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    textAlign: 'center',
    ':hover': { border: `2px dashed ${tokens.colorBrandStroke1}` },
    ':focus-within': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
    },
  },
  dropZoneActive: {
    border: `2px dashed ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  dropIcon: {
    fontSize: '40px',
    color: tokens.colorBrandForeground1,
  },
  hiddenInput: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    opacity: 0,
  },
  selected: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: tokens.spacingHorizontalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  selectedName: {
    flexGrow: 1,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    overflowWrap: 'anywhere',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
})

export function UploadPage() {
  const styles = useStyles()
  const api = useApi()
  const { maxUploadMb } = useConfig()
  const navigate = useNavigate()
  const inputRef = useRef<HTMLInputElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  const [file, setFile] = useState<File | null>(null)
  const [validationError, setValidationError] = useState<string | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [dragActive, setDragActive] = useState(false)
  const [progress, setProgress] = useState<number | null>(null)

  useEffect(() => () => abortRef.current?.abort(), [])

  const uploading = progress !== null

  const selectFile = async (candidate: File | undefined) => {
    setUploadError(null)
    if (!candidate) return
    setFile(candidate)
    setValidationError(await validateFile(candidate, maxUploadMb))
  }

  const clearFile = () => {
    setFile(null)
    setValidationError(null)
    setUploadError(null)
    if (inputRef.current) inputRef.current.value = ''
  }

  const onDrop = (event: DragEvent<HTMLLabelElement>) => {
    event.preventDefault()
    setDragActive(false)
    if (!uploading) void selectFile(event.dataTransfer.files[0])
  }

  const onUpload = async () => {
    if (!file || validationError) return
    const controller = new AbortController()
    abortRef.current = controller
    setUploadError(null)
    setProgress(0)
    try {
      const accepted = await api.uploadDocument(file, {
        signal: controller.signal,
        onProgress: setProgress,
      })
      navigate(`/documents/${accepted.documentId}`, {
        state: { fileName: file.name, justUploaded: true },
      })
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        setUploadError(null)
      } else if (error instanceof ApiError) {
        if (error.kind !== 'unauthorized') setUploadError(error.message)
      } else {
        setUploadError('The upload failed unexpectedly. Please retry.')
      }
      setProgress(null)
    } finally {
      abortRef.current = null
    }
  }

  return (
    <>
      <Title2 as="h1">Upload a document</Title2>

      <Card className={styles.card}>
        <label
          className={mergeClasses(styles.dropZone, dragActive && styles.dropZoneActive)}
          onDragOver={(event) => {
            event.preventDefault()
            if (!uploading) setDragActive(true)
          }}
          onDragLeave={() => setDragActive(false)}
          onDrop={onDrop}
        >
          <ArrowUploadRegular className={styles.dropIcon} aria-hidden />
          <Text weight="semibold">Drag and drop a file here, or click to browse</Text>
          <Text size={200}>
            PDF or Word (.docx), up to {maxUploadMb} MB. Scanned images without a text layer will report
            &ldquo;No text found&rdquo;.
          </Text>
          <input
            ref={inputRef}
            className={styles.hiddenInput}
            type="file"
            accept={[...allowedExtensions, 'application/pdf'].join(',')}
            disabled={uploading}
            onChange={(event) => void selectFile(event.target.files?.[0])}
            aria-label="Choose a PDF or DOCX file"
          />
        </label>

        {file && (
          <div className={styles.selected}>
            <DocumentRegular fontSize={24} aria-hidden />
            <div className={styles.selectedName}>
              <Text weight="semibold">{file.name}</Text>
              <Text size={200}>{formatFileSize(file.size)}</Text>
            </div>
            {!uploading && (
              <Button appearance="subtle" icon={<DismissRegular />} onClick={clearFile} aria-label="Remove file" />
            )}
          </div>
        )}

        {validationError && (
          <MessageBar intent="error">
            <MessageBarBody>{validationError}</MessageBarBody>
          </MessageBar>
        )}

        {uploadError && (
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Upload failed</MessageBarTitle>
              {uploadError}
            </MessageBarBody>
          </MessageBar>
        )}

        {uploading && (
          <ProgressBar
            value={progress / 100}
            thickness="large"
            aria-label="Upload progress"
            aria-valuetext={`${progress}%`}
          />
        )}

        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<ArrowUploadRegular />}
            disabled={!file || !!validationError || uploading}
            onClick={() => void onUpload()}
          >
            {uploading ? `Uploading… ${progress}%` : 'Upload'}
          </Button>
          {uploading && <Button onClick={() => abortRef.current?.abort()}>Cancel</Button>}
        </div>
      </Card>

      <Card className={styles.card}>
        <CardHeader header={<Title3 as="h2">Open an existing document</Title3>} />
        <Text size={200}>
          Paste the document ID returned when the file was uploaded to check its status.
        </Text>
        <DocumentLookup />
      </Card>
    </>
  )
}
