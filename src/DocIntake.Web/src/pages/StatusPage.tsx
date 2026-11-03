import { Button, Card, Text, Title2, makeStyles, tokens } from '@fluentui/react-components'
import {
  DocumentQuestionMarkRegular,
  ErrorCircleRegular,
  LockClosedRegular,
  PersonClockRegular,
} from '@fluentui/react-icons'
import type { ReactElement } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/authContext'

export type StatusPageVariant = 'notFound' | 'sessionExpired' | 'forbidden' | 'unexpected'

const content: Record<StatusPageVariant, { title: string; message: string; icon: ReactElement }> = {
  notFound: {
    title: 'Page not found',
    message: 'The page you are looking for does not exist or has moved.',
    icon: <DocumentQuestionMarkRegular />,
  },
  sessionExpired: {
    title: 'Your session has expired',
    message: 'For your security you were signed out. Sign in again to continue where you left off.',
    icon: <PersonClockRegular />,
  },
  forbidden: {
    title: 'Access denied',
    message:
      'Your account is signed in but is not allowed to use this application. Contact your administrator if you believe this is a mistake.',
    icon: <LockClosedRegular />,
  },
  unexpected: {
    title: 'Something went wrong',
    message: 'An unexpected error occurred. Reload the page and try again.',
    icon: <ErrorCircleRegular />,
  },
}

const useStyles = makeStyles({
  card: {
    maxWidth: '520px',
    margin: `${tokens.spacingVerticalXXXL} auto`,
    padding: tokens.spacingHorizontalXXL,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    textAlign: 'center',
    gap: tokens.spacingVerticalM,
  },
  icon: {
    fontSize: '48px',
    color: tokens.colorBrandForeground1,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
  },
})

interface StatusPageProps {
  variant: StatusPageVariant
  onRetry?: () => void
}

export function StatusPage({ variant, onRetry }: StatusPageProps) {
  const styles = useStyles()
  const { title, message, icon } = content[variant]

  return (
    <Card className={styles.card} role="alert">
      <span className={styles.icon} aria-hidden>
        {icon}
      </span>
      <Title2 as="h1">{title}</Title2>
      <Text>{message}</Text>
      <div className={styles.actions}>
        {variant === 'sessionExpired' ? <SignInAgainButton /> : null}
        {variant === 'unexpected' && onRetry ? (
          <Button appearance="primary" onClick={onRetry}>
            Reload
          </Button>
        ) : null}
        {variant !== 'unexpected' && <GoHomeButton primary={variant !== 'sessionExpired'} />}
      </div>
    </Card>
  )
}

function GoHomeButton({ primary }: { primary: boolean }) {
  const navigate = useNavigate()
  return (
    <Button appearance={primary ? 'primary' : 'secondary'} onClick={() => navigate('/')}>
      Go to upload
    </Button>
  )
}

function SignInAgainButton() {
  const { signIn } = useAuth()
  const location = useLocation()
  const from = (location.state as { from?: string } | null)?.from ?? '/'
  return (
    <Button appearance="primary" onClick={() => void signIn(from)}>
      Sign in again
    </Button>
  )
}
