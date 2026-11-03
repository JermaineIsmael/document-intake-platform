import {
  BrowserCacheLocation,
  CacheLookupPolicy,
  InteractionRequiredAuthError,
  PublicClientApplication,
  type AccountInfo,
  type IPublicClientApplication,
} from '@azure/msal-browser'
import type { AppConfig } from '../config'
import { safeReturnPath } from './authContext'

export interface MsalBootstrap {
  instance: IPublicClientApplication
  // Path the user was on before the sign-in redirect, restored after Entra returns.
  returnPath: string | null
}

export async function bootstrapMsal(config: AppConfig): Promise<MsalBootstrap> {
  const instance = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri: `${window.location.origin}/`,
      // Same URI as the sign-in redirect, so each origin needs only one registered SPA redirect URI.
      postLogoutRedirectUri: `${window.location.origin}/`,
    },
    cache: {
      // Tokens are kept per tab and cleared when the tab closes.
      cacheLocation: BrowserCacheLocation.SessionStorage,
    },
  })
  await instance.initialize()

  const result = await instance.handleRedirectPromise()
  if (result?.account) {
    instance.setActiveAccount(result.account)
  } else if (!instance.getActiveAccount()) {
    const [account] = instance.getAllAccounts()
    if (account) instance.setActiveAccount(account)
  }

  return { instance, returnPath: result ? safeReturnPath(result.state) : null }
}

export async function acquireApiToken(
  instance: IPublicClientApplication,
  scope: string,
  account: AccountInfo | null,
): Promise<string | null> {
  if (!account) return null
  try {
    // Hidden-iframe renewal is excluded: the site forbids framing (X-Frame-Options/frame-ancestors) and has
    // no redirect bridge page, so it could only time out. An expired refresh token instead raises
    // InteractionRequiredAuthError and falls through to an interactive redirect.
    const result = await instance.acquireTokenSilent({
      scopes: [scope],
      account,
      cacheLookupPolicy: CacheLookupPolicy.AccessTokenAndRefreshToken,
    })
    return result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await instance.acquireTokenRedirect({
        scopes: [scope],
        account,
        state: safeReturnPath(window.location.pathname + window.location.search),
      })
      return null
    }
    throw error
  }
}
