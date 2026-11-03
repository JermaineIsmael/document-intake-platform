import {
  Button,
  Card,
  Field,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components'
import { SendRegular } from '@fluentui/react-icons'
import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Link } from 'react-router-dom'
import { useApi } from '../api/apiContext'
import { ApiError } from '../api/client'
import type { AskResponse } from '../api/types'
import { parseSource } from '../lib/format'

const useStyles = makeStyles({
  card: {
    padding: tokens.spacingHorizontalXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  formActions: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  question: {
    fontWeight: tokens.fontWeightSemibold,
  },
  answer: {
    whiteSpace: 'pre-wrap',
    lineHeight: tokens.lineHeightBase400,
  },
  sources: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  sourceLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
    marginRight: tokens.spacingHorizontalS,
  },
})

interface Exchange {
  id: number
  question: string
  response?: AskResponse
  error?: { title: string; message: string; intent: 'error' | 'warning' | 'info' }
}

function describeError(error: unknown): NonNullable<Exchange['error']> {
  if (error instanceof ApiError) {
    switch (error.kind) {
      case 'notFound':
        return {
          title: 'No relevant content found',
          message:
            'None of your indexed documents seem to answer this question. Rephrase it, or upload a document that covers the topic.',
          intent: 'info',
        }
      case 'unavailable':
        return {
          title: 'Question answering is not available',
          message: `${error.message} Uploading and document status still work.`,
          intent: 'warning',
        }
      case 'validation':
        return { title: 'Question rejected', message: error.message, intent: 'error' }
      default:
        return { title: 'Could not get an answer', message: error.message, intent: 'error' }
    }
  }
  return { title: 'Could not get an answer', message: 'An unexpected error occurred. Please retry.', intent: 'error' }
}

export function AskPage() {
  const styles = useStyles()
  const api = useApi()
  const [question, setQuestion] = useState('')
  const [exchanges, setExchanges] = useState<Exchange[]>([])
  const [pending, setPending] = useState(false)
  const abortRef = useRef<AbortController | null>(null)
  const nextId = useRef(1)

  useEffect(() => () => abortRef.current?.abort(), [])

  const submit = async () => {
    const trimmed = question.trim()
    if (!trimmed || pending) return

    const id = nextId.current++
    const controller = new AbortController()
    abortRef.current = controller
    setPending(true)
    setExchanges((current) => [{ id, question: trimmed }, ...current])
    setQuestion('')

    try {
      const response = await api.ask(trimmed, controller.signal)
      setExchanges((current) => current.map((item) => (item.id === id ? { ...item, response } : item)))
    } catch (error) {
      if (controller.signal.aborted) return
      if (error instanceof ApiError && error.kind === 'unauthorized') return
      setExchanges((current) =>
        current.map((item) => (item.id === id ? { ...item, error: describeError(error) } : item)),
      )
    } finally {
      if (!controller.signal.aborted) setPending(false)
    }
  }

  const onSubmit = (event: FormEvent) => {
    event.preventDefault()
    void submit()
  }

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault()
      void submit()
    }
  }

  return (
    <>
      <Title2 as="h1">Ask about your documents</Title2>

      <Card className={styles.card}>
        <form className={styles.form} onSubmit={onSubmit}>
          <Field
            label="Question"
            hint="Answers are generated only from documents you uploaded and that finished indexing."
          >
            <Textarea
              value={question}
              onChange={(_, data) => setQuestion(data.value)}
              onKeyDown={onKeyDown}
              placeholder="e.g. What is the notice period in the supplier agreement?"
              resize="vertical"
              rows={3}
              disabled={pending}
            />
          </Field>
          <div className={styles.formActions}>
            <Text size={200}>Press Ctrl+Enter to send.</Text>
            <Button type="submit" appearance="primary" icon={<SendRegular />} disabled={!question.trim() || pending}>
              Ask
            </Button>
          </div>
        </form>
      </Card>

      {exchanges.map((exchange) => (
        <ExchangeCard key={exchange.id} exchange={exchange} />
      ))}
    </>
  )
}

function ExchangeCard({ exchange }: { exchange: Exchange }) {
  const styles = useStyles()
  const sources = exchange.response?.sources.map(parseSource) ?? []

  return (
    <Card className={styles.card} aria-live="polite">
      <Text className={styles.question}>{exchange.question}</Text>

      {!exchange.response && !exchange.error && <Spinner size="small" label="Searching your documents…" />}

      {exchange.error && (
        <MessageBar intent={exchange.error.intent}>
          <MessageBarBody>
            <MessageBarTitle>{exchange.error.title}</MessageBarTitle>
            {exchange.error.message}
          </MessageBarBody>
        </MessageBar>
      )}

      {exchange.response && (
        <>
          <Text className={styles.answer}>{exchange.response.answer}</Text>
          {sources.length > 0 && (
            <>
              <Title3 as="h2">Sources</Title3>
              <ol className={styles.sources}>
                {sources.map((source) => (
                  <li key={source.label}>
                    <span className={styles.sourceLabel}>[{source.label}]</span>
                    {source.documentId ? (
                      <Link to={`/documents/${source.documentId}`} state={{ fileName: source.fileName }}>
                        {source.fileName}
                      </Link>
                    ) : (
                      <Text>{source.fileName}</Text>
                    )}
                  </li>
                ))}
              </ol>
            </>
          )}
        </>
      )}
    </Card>
  )
}
