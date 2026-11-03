import { Button, Card, Text, Title2, makeStyles, tokens } from '@fluentui/react-components'
import { PersonRegular } from '@fluentui/react-icons'
import { useState } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { safeReturnPath, useAuth } from '../auth/authContext'

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground2,
  },
  card: {
    width: '420px',
    padding: tokens.spacingHorizontalXXL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    textAlign: 'center',
  },
})

export function SignInPage() {
  const styles = useStyles()
  const { isAuthenticated, signIn } = useAuth()
  const [params] = useSearchParams()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const returnTo = safeReturnPath(params.get('returnTo') ?? '/')

  if (isAuthenticated) {
    return <Navigate to={returnTo} replace />
  }

  const onSignIn = async () => {
    setBusy(true)
    setError(null)
    try {
      await signIn(returnTo)
    } catch {
      setError('Sign-in could not be started. Check that pop-ups and redirects are allowed and try again.')
      setBusy(false)
    }
  }

  return (
    <div className={styles.page}>
      <Card className={styles.card}>
        <Title2 as="h1">DocIntake</Title2>
        <Text>
          Upload PDF and Word documents, track their processing, and ask questions about their content.
        </Text>
        <Button appearance="primary" size="large" icon={<PersonRegular />} disabled={busy} onClick={onSignIn}>
          Sign in with Microsoft
        </Button>
        {error && (
          <Text role="alert" style={{ color: tokens.colorPaletteRedForeground1 }}>
            {error}
          </Text>
        )}
      </Card>
    </div>
  )
}
