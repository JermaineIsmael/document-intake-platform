import {
  Button,
  MessageBar,
  MessageBarBody,
  Text,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components'
import { ChatRegular, DocumentArrowUpRegular, SignOutRegular } from '@fluentui/react-icons'
import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/authContext'

const useStyles = makeStyles({
  root: {
    minHeight: '100vh',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground2,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  brand: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase500,
    marginRight: tokens.spacingHorizontalL,
  },
  nav: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexGrow: 1,
  },
  navLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForegroundOnBrand,
    textDecoration: 'none',
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover },
  },
  navLinkActive: {
    backgroundColor: tokens.colorBrandBackgroundPressed,
  },
  user: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  signOut: {
    color: tokens.colorNeutralForegroundOnBrand,
  },
  main: {
    flexGrow: 1,
    width: '100%',
    maxWidth: '960px',
    margin: '0 auto',
    padding: tokens.spacingHorizontalXXL,
    boxSizing: 'border-box',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
})

export function Layout() {
  const styles = useStyles()
  const { user, enabled, signOut } = useAuth()
  const linkClass = ({ isActive }: { isActive: boolean }) =>
    isActive ? `${styles.navLink} ${styles.navLinkActive}` : styles.navLink

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <span className={styles.brand}>DocIntake</span>
        <nav className={styles.nav} aria-label="Main">
          <NavLink to="/" end className={linkClass}>
            <DocumentArrowUpRegular aria-hidden /> Upload
          </NavLink>
          <NavLink to="/ask" className={linkClass}>
            <ChatRegular aria-hidden /> Ask
          </NavLink>
        </nav>
        {user && (
          <div className={styles.user}>
            <Text>{user.name}</Text>
            {enabled && (
              <Tooltip content="Sign out" relationship="label">
                <Button
                  appearance="transparent"
                  className={styles.signOut}
                  icon={<SignOutRegular />}
                  onClick={() => void signOut()}
                />
              </Tooltip>
            )}
          </div>
        )}
      </header>
      <main className={styles.main}>
        {!enabled && (
          <MessageBar intent="warning">
            <MessageBarBody>
              Sign-in is disabled for local development. The API must run with Development:BypassAuth=true.
            </MessageBarBody>
          </MessageBar>
        )}
        <Outlet />
      </main>
    </div>
  )
}
