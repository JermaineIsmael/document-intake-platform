import { Button, Field, Input, makeStyles, tokens } from '@fluentui/react-components'
import { SearchRegular } from '@fluentui/react-icons'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { normalizeDocumentId } from '../lib/documentStatus'

const useStyles = makeStyles({
  form: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: tokens.spacingHorizontalS,
  },
  field: { flexGrow: 1 },
})

export function DocumentLookup() {
  const styles = useStyles()
  const navigate = useNavigate()
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)

  const onSubmit = (event: FormEvent) => {
    event.preventDefault()
    const documentId = normalizeDocumentId(value)
    if (!documentId) {
      setError('Enter a document ID in the format xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.')
      return
    }
    setError(null)
    navigate(`/documents/${documentId}`)
  }

  return (
    <form className={styles.form} onSubmit={onSubmit} noValidate>
      <Field
        className={styles.field}
        label="Document ID"
        validationState={error ? 'error' : 'none'}
        validationMessage={error ?? undefined}
      >
        <Input
          value={value}
          onChange={(_, data) => setValue(data.value)}
          placeholder="e.g. 3f2b8c1e-5d4a-4e7b-9c0d-1a2b3c4d5e6f"
          autoComplete="off"
          spellCheck={false}
        />
      </Field>
      <Button type="submit" icon={<SearchRegular />}>
        Open
      </Button>
    </form>
  )
}
