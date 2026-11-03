import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { App } from './App'
import { DevAuthProvider, MsalAuthProvider } from './auth/AuthProvider'
import { bootstrapMsal } from './auth/msal'
import { Shell, StartupError } from './components/Bootstrap'
import { loadConfig, type AppConfig } from './config'
import { ConfigContext } from './configContext'
import './index.css'

const root = createRoot(document.getElementById('root')!)

async function start() {
  let config: AppConfig
  try {
    config = loadConfig()
  } catch (error) {
    root.render(
      <Shell>
        <StartupError title="The application is not configured" message={(error as Error).message} />
      </Shell>,
    )
    return
  }

  if (!config.authEnabled) {
    root.render(
      <Shell>
        <ConfigContext.Provider value={config}>
          <BrowserRouter>
            <DevAuthProvider>
              <App />
            </DevAuthProvider>
          </BrowserRouter>
        </ConfigContext.Provider>
      </Shell>,
    )
    return
  }

  try {
    const { instance, returnPath } = await bootstrapMsal(config)
    // Restore the page the user was on before the Entra redirect once, before the router reads the URL.
    if (returnPath && returnPath !== window.location.pathname + window.location.search) {
      window.history.replaceState(null, '', returnPath)
    }
    root.render(
      <Shell>
        <ConfigContext.Provider value={config}>
          <BrowserRouter>
            <MsalAuthProvider instance={instance} apiScope={config.apiScope}>
              <App />
            </MsalAuthProvider>
          </BrowserRouter>
        </ConfigContext.Provider>
      </Shell>,
    )
  } catch (error) {
    console.error('Sign-in initialisation failed', error)
    root.render(
      <Shell>
        <StartupError
          title="Sign-in could not be completed"
          message="Microsoft sign-in returned an error. Reload the page to try again; if it persists, contact your administrator."
        />
      </Shell>,
    )
  }
}

void start()
