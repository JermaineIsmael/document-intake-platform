# Front-end web application

Baseline: 29 October 2026. Status: **implemented in code and verified locally;
not yet deployed to Azure.** No Azure resources were created or changed while
building it, and the live sign-in, CORS, and Static Web App checks are pending
(see [verification](#verification)).

The React single-page application in [`src/DocIntake.Web`](../../src/DocIntake.Web)
is the first end-user UI for DocIntake. It is a client of the existing API
(`POST /documents`, `GET|DELETE /documents/{id}`, `POST /documents/{id}/restore`,
`POST /ask`). The API's behaviour is unchanged apart from optional CORS support.

## Scope

| # | Page | Route | Status | Backed by |
|---|---|---|---|---|
| 1 | Sign-in | `/signin` | Implemented | Microsoft Entra ID (MSAL, auth-code + PKCE redirect flow) |
| 2 | Upload (and open by ID) | `/` | Implemented | `POST /documents` |
| 3 | My Documents (list) | — | **Not implemented (out of scope)** | Needs a new `GET /documents` endpoint |
| 4 | Document detail / status | `/documents/:documentId` | Implemented | `GET`, `DELETE /documents/{id}`, `POST /documents/{id}/restore` |
| 5 | Ask | `/ask` | Implemented; answers need chat enabled | `POST /ask` |
| 6 | Error pages | `/session-expired`, `/forbidden`, `/not-found`, any unknown route | Implemented | — |

Without a list endpoint, the detail page is reached by the redirect after an
upload, the **Open an existing document** lookup on the Upload page (paste a
document ID), a source link on the Ask page, or a bookmarked URL.

## Pages

### Sign-in (`/signin`)

- Shown when a signed-out user opens any protected route. The original path is
  kept in `?returnTo=` and restored after Entra returns.
- **Sign in with Microsoft** starts an MSAL redirect requesting
  `api://<api-client-id>/access_as_user`.
- Only same-app relative paths are accepted as return targets (`//host`,
  absolute URLs, and backslashes fall back to `/`), so the redirect state
  cannot be used as an open redirect.
- The header shows the signed-in user's name and a **Sign out** button
  (`logoutRedirect`, returning to the site root).

### Upload (`/`)

- Drag-and-drop zone or file picker for one PDF or DOCX file.
- Client-side checks mirror the API's `FileValidator`: extension (`.pdf`,
  `.docx`), non-empty, size limit (`VITE_MAX_UPLOAD_MB`, default 25 MB), and file
  signature (`%PDF-` or the ZIP header for DOCX). The API still validates every
  upload; these checks only give earlier feedback.
- Upload progress bar (XMLHttpRequest progress events) with **Cancel**.
- API rejections (400/413/415/503) are shown using the API's `error` message.
- On `202 Accepted` it navigates to `/documents/{documentId}`, passing the
  original file name in router state because the API does not return it.
- **Open an existing document**: validates the GUID format (braces and case are
  normalised) and opens its detail page.

### Document detail (`/documents/:documentId`)

- Status badge and plain-language description for each API status: `queued`,
  `extracting`, `processing`, `indexed`, `noText`, `failed`, `deleted`.
- Progress timeline: Queued → Extracting → Indexing → Ready. `noText` and
  `failed` are shown as terminal steps; it is hidden for deleted documents.
- **Polling**: while the status is `queued`, `extracting`, or `processing`, the
  page re-reads the status every 3 seconds for the first minute, then every
  10 seconds, and stops on a terminal status or an error (a **Retry** button is
  shown for errors).
- Details: uploaded, last status change, indexed time, chunk count, deleted
  time, recoverable-until, and failure code.
- **Failed**: shows the API's caller-safe failure message and an **Upload
  again** action (or **Upload a different file** when `retryable` is false).
- **No text found**: explains that scans without a text layer cannot be used.
- **Delete**: confirmation dialog, then `DELETE /documents/{id}`; the page then
  shows the deletion time and the restore deadline.
- **Restore**: shown while `recoverableUntil` is in the future;
  `POST /documents/{id}/restore`. A 404 is reported as "recovery window ended".
- **Ask about your documents**: shown once the document is indexed.
- The file name heading is only available when arriving from an upload or an
  Ask source link (the API does not store or return file names); otherwise the
  heading is "Document".
- 404 from the API shows **Document not found** (wrong ID, another user's
  document, or permanently removed) with the ID lookup. A malformed ID in the
  URL redirects to `/not-found`.

### Ask (`/ask`)

- Question box (Ctrl+Enter to send). Each question and its answer are shown as
  a card, newest first, for the current page session only (nothing is stored).
- The answer is rendered as plain text (React escaping; no HTML or Markdown
  rendering), with `[sourceN]` citations as returned by the model.
- **Sources**: each source is the private blob URL of an original upload
  (`.../documents/<documentId>/<fileName>`). The UI shows the file name and links
  to the document's detail page; the blob URL itself is not opened because
  storage is private.
- API outcomes:
  - `503`: "Question answering is not available" (chat is disabled, as in the
    current development environment). Upload and status still work.
  - `404`: "No relevant content found".
  - `400`: question rejected; other errors show a retryable message.

### Error pages

| Route | When | Actions |
|---|---|---|
| `/session-expired` | Any API call returns 401, or no access token can be acquired, while sign-in is enabled | **Sign in again** (returns to the page you were on), **Go to upload** |
| `/forbidden` | Any API call returns 403 while sign-in is enabled (currently: the token has no `oid` claim) | **Go to upload** |
| `/not-found`, `*` | Unknown route or malformed document ID | **Go to upload** |
| (error boundary) | Unhandled rendering error | **Reload** |

Startup also shows a clear error if the build is missing required
configuration, or if the Entra redirect returns an error.

## Architecture

| Concern | Choice |
|---|---|
| Framework | React 19 + TypeScript, built with Vite 8 |
| UI components | Fluent UI React v9 (`@fluentui/react-components`, `@fluentui/react-icons`) with the `webLightTheme` |
| Routing | React Router 7 (`BrowserRouter`) |
| Authentication | `@azure/msal-browser` v5 used directly (no `msal-react`) behind a small `AuthContext` |
| Token cache | `sessionStorage` (cleared when the tab closes) |
| API calls | `ApiClient` (`fetch`; `XMLHttpRequest` for uploads so progress can be reported) |
| Tests | Vitest + Testing Library (jsdom); lint with oxlint |

```text
src/DocIntake.Web/
├── public/staticwebapp.config.json   SPA fallback and security headers for Azure Static Web Apps
├── src/
│   ├── main.tsx                      loads config, initialises MSAL, renders the app
│   ├── App.tsx                       routes
│   ├── config.ts / configContext.ts  build-time configuration (VITE_* variables)
│   ├── auth/                         AuthContext, MSAL bootstrap/token acquisition, RequireAuth
│   ├── api/                          ApiClient, ApiError, response types, ApiProvider
│   ├── hooks/useDocumentStatus.ts    status loading and polling
│   ├── lib/                          file validation, status mapping, source parsing, formatting
│   ├── components/                   layout, status badge/timeline, ID lookup, error boundary
│   ├── pages/                        Sign-in, Upload, Document, Ask, Status (error) pages
│   └── test/                         test setup and render helpers
├── .env.development                  local defaults (auth disabled, /api proxy)
└── .env.example                      all supported settings
```

### Authentication flow

1. `main.tsx` creates a `PublicClientApplication` (authority and client ID from
   configuration), calls `initialize()` and `handleRedirectPromise()`, and
   restores the active account.
2. `RequireAuth` redirects signed-out users to `/signin?returnTo=…`.
3. Sign-in uses `loginRedirect` with the API scope and the return path in
   `state`. After the redirect, `main.tsx` restores that path once with
   `history.replaceState` before the router starts.
4. Before each API call, `acquireTokenSilent` returns a cached access token or
   redeems the refresh token (`CacheLookupPolicy.AccessTokenAndRefreshToken`).
   Hidden-iframe renewal is deliberately not used: the site forbids framing and
   has no redirect bridge page, so it could only time out. When interaction is
   required (for example the 24-hour SPA refresh token expired),
   `acquireTokenRedirect` is started from the current page. Any other token
   failure is treated as an expired session.
5. A 401 from the API sends the user to `/session-expired`; a 403 sends them to
   `/forbidden`.

The SPA uses the **existing API app registration** (decision: reuse rather than
create a separate SPA registration). Its client ID is the API's `authAudience`,
and it requests that registration's own `access_as_user` scope, so the access
token's audience is the API client ID that the API already validates.

### Development mode without sign-in

`VITE_AUTH_ENABLED=false` (the default in `.env.development`) replaces MSAL
with a development provider that sends no token. This matches the API's
`Development:BypassAuth=true`, which is only honoured in the Development
environment (owner `00000000-0000-0000-0000-000000000001`). A warning banner is
shown on every page in this mode. Production builds default to sign-in enabled
and refuse to start without Entra settings.

## Configuration

Vite embeds `VITE_*` values into the static bundle at build time. None of them
is secret.

| Variable | Default | Purpose |
|---|---|---|
| `VITE_API_BASE_URL` | `/api` | API root. `/api` uses the Vite dev proxy; deployed builds use `https://<api-fqdn>` |
| `VITE_AUTH_ENABLED` | `true` (`false` in `.env.development`) | Disable only against an API running `Development:BypassAuth=true` |
| `VITE_AUTH_CLIENT_ID` | — (required when auth is enabled) | API app registration client ID (`authAudience`) |
| `VITE_AUTH_AUTHORITY` | — (required when auth is enabled) | `https://login.microsoftonline.com/<tenant-id>/v2.0` (`authAuthority`) |
| `VITE_API_SCOPE` | `api://<client-id>/access_as_user` | Delegated scope requested for API tokens |
| `VITE_MAX_UPLOAD_MB` | `25` | Client-side size check; keep equal to the API's `Upload:MaxFileSizeMb` |
| `API_PROXY_TARGET` | `http://localhost:5067` | Dev server only: where `/api` is proxied |

## Local development

Prerequisites: Node.js 22.12+ (24 LTS or newer recommended), the .NET 8 SDK,
and Azurite (see the [README](../../README.md#local-development)).

```powershell
# 1. Storage emulator (or the Docker image described in the README)
npx azurite --silent --location $env:TEMP\azurite --skipApiVersionCheck

# 2. API in Development with auth bypass (appsettings.Development.json)
dotnet run --project src\DocIntake.Api --launch-profile http

# 3. Front end
cd src\DocIntake.Web
npm install
npm run dev          # http://localhost:5173
```

The dev server proxies `/api/*` to the API (stripping `/api`) because SPA routes
such as `/documents/:id` share paths with the API. No CORS configuration is
needed locally.

Without the Worker and Extractor running, uploaded documents stay `queued`.
Azurite does not implement blob undelete, so **Restore returns 503 locally**;
restore needs a real storage account with blob soft delete.

To test real sign-in locally, create `.env.development.local` (git-ignored):

```dotenv
VITE_AUTH_ENABLED=true
VITE_AUTH_CLIENT_ID=<api-client-id>
VITE_AUTH_AUTHORITY=https://login.microsoftonline.com/<tenant-id>/v2.0
```

Then add `http://localhost:5173/` as a single-page-application redirect URI on
the app registration, and run the API with `Development__BypassAuth=false` and
`Auth__Authority`/`Auth__Audience` set (see
[Testing Entra authentication locally](../../README.md#testing-entra-authentication-locally)).

Scripts (from `src/DocIntake.Web`):

| Command | Purpose |
|---|---|
| `npm run dev` | Dev server with hot reload |
| `npm run lint` | oxlint (CI uses `--deny-warnings`) |
| `npm test` | Vitest unit and component tests |
| `npm run build` | Type-check (`tsc -b`) and production build into `dist/` |
| `npm run preview` | Serve the production build locally |

## API change: CORS

The deployed SPA is served from a different origin than the API, so the API now
supports an optional CORS allow-list:

- Setting: `Cors:AllowedOrigins` (array; environment variables
  `Cors__AllowedOrigins__0`, `__1`, …). Empty (the default) means CORS is not
  enabled, which keeps the previous behaviour.
- Validation at startup ([CorsSettings.cs](../../src/DocIntake.Api/Configuration/CorsSettings.cs)):
  origins must be absolute HTTPS (HTTP only for loopback), without a path,
  query, user info, or wildcard; otherwise the API fails to start.
- Policy: methods `GET`, `POST`, `DELETE`; headers `Authorization`,
  `Content-Type`; exposes `Location`; preflight cached for 10 minutes; no
  credentials (cookies) because the SPA uses bearer tokens.
- CORS runs before authentication, so token-less browser preflight requests are
  answered. Authentication and owner checks are unchanged; an unauthenticated
  cross-origin request still receives 401.
- The origins are read after the host is built (like the existing `Auth`
  checks), so test-host configuration applies.
- Tests: [CorsTests.cs](../../tests/DocIntake.Tests/CorsTests.cs) (18 cases).

## Azure hosting

### Infrastructure ([main.bicep](../../infra/main.bicep))

| Parameter | Default | Purpose |
|---|---|---|
| `deployWebApp` | `true` | Create the Static Web App `di-<env>-<suffix>-web` (Free tier, no linked repository, staging environments disabled) |
| `webAppLocation` | `eastus2` | Static Web App control-plane region (allowed: `centralus`, `eastus2`, `eastasia`, `westeurope`, `westus2`); content is served globally |
| `additionalCorsOrigins` | `[]` | Extra API CORS origins, such as a custom domain |

The Static Web App origin `https://<defaultHostname>` is added to the API as
`Cors__AllowedOrigins__0`, and additional origins follow. New outputs:
`webAppName` and `webAppHostname`. These parameters are also set explicitly in
[main.bicepparam](../../infra/main.bicepparam).

### Deployment

- [`scripts/deploy-azure.sh`](../../scripts/deploy-azure.sh) (existing step) now also:
  - publishes the `apiFqdn` and `webAppName` pipeline variables;
  - checks that `https://<webAppHostname>/` is a single-page-application redirect
    URI on the API app registration and adds it through Microsoft Graph if it is
    missing. If the pipeline identity lacks permission, it prints a warning with
    the manual step instead of failing.
- [`scripts/deploy-web.sh`](../../scripts/deploy-web.sh) (new pipeline step after
  the Extractor):
  1. Reads `authAuthority` and `authAudience` from `infra/main.bicepparam`, and the
     upload limit from the API's `appsettings.json`.
  2. Builds the SPA (`npm ci`, `npm run build`) with
     `VITE_API_BASE_URL=https://<apiFqdn>`.
  3. Publishes `dist/` with the Static Web Apps CLI, using the deployment token
     read with `az staticwebapp secrets list`. The token is passed through an
     environment variable and never printed.
  4. Runs smoke checks: `/` and a client-side route return 200, and an API CORS
     preflight from the site origin returns the matching
     `Access-Control-Allow-Origin`.

  Optional overrides: `WEB_CLIENT_ID`, `WEB_API_SCOPE`, `WEB_MAX_UPLOAD_MB`.
  The step is skipped when `deployWebApp` is false.
- [`infra/azure-pipelines.yml`](../../infra/azure-pipelines.yml): installs Node.js
  24, runs `npm ci`, lint (`--deny-warnings`), tests, and build after the .NET
  build and tests, and adds the **Build and publish front end** step.
- [`.dockerignore`](../../.dockerignore) excludes `src/DocIntake.Web`, so
  `node_modules` is not sent to the Worker and Migrations image builds.

### Static Web App configuration

[`public/staticwebapp.config.json`](../../src/DocIntake.Web/public/staticwebapp.config.json):

- `navigationFallback` to `index.html` for client-side routes (assets excluded).
- Security headers:
  - Content Security Policy: `script-src 'self'`; `connect-src` limited to the
    site, `*.azurecontainerapps.io`, and `login.microsoftonline.com`; no frames
    (`frame-ancestors 'none'`, and no `frame-src` allowance because MSAL iframe
    renewal is not used). `style-src` allows `'unsafe-inline'` because Fluent UI
    injects styles at runtime.
  - `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, and
    `X-Frame-Options`.
- Caching: `index.html` is `no-cache`; hashed `/assets/*` files are immutable.

If the API is moved to a custom domain, add that host to `connect-src`.

### One-time Entra setup (manual if the pipeline cannot do it)

On the API app registration (**Microsoft Entra ID > App registrations >
<API app> > Authentication**):

1. **Add a platform > Single-page application**, with redirect URI
   `https://<webAppHostname>/` (and `http://localhost:5173/` for local
   sign-in testing). Use the SPA platform, not Web: SPA enables PKCE and CORS
   for the token endpoint.
2. Keep the existing **Expose an API** scope `access_as_user`. Grant or consent
   to it according to tenant policy. Because the SPA and the API share the
   registration, no separate API permission entry is needed.

Equivalent CLI. This replaces the SPA redirect URI list, so include any
existing URIs:

```bash
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications(appId='<api-client-id>')" \
  --headers 'Content-Type=application/json' \
  --body '{"spa":{"redirectUris":["https://<webAppHostname>/"]}}'
```

## Security notes

- Access tokens are held by MSAL in `sessionStorage`, not in application code
  or `localStorage`. All API calls send them as bearer tokens over HTTPS. No
  cookies are used, so CORS does not allow credentials.
- All authorisation stays in the API (JWT validation and owner scoping). The
  UI only hides actions that would fail.
- Rendered API data (answers, file names, failure messages) is React-escaped
  text; there is no `dangerouslySetInnerHTML`.
- Return paths after sign-in are restricted to same-app relative paths.
- Build-time configuration contains no secrets. The Static Web App deployment
  token stays in the pipeline.

## Verification

Performed on 29 October 2026 on a development workstation:

| Check | Result |
|---|---|
| `dotnet test DocIntake.sln` | 267 passed, 5 skipped, 0 failed (includes the 18 new CORS cases) |
| `npm run lint -- --deny-warnings`, `tsc -b` | Clean |
| `npm test` | 45 passed (config, API client, file validation, status mapping, source parsing, return-path safety, page behaviour) |
| `npm run build` | Succeeds; one ~913 kB (257 kB gzip) JavaScript bundle |
| `az bicep build` / `build-params` | Succeed |
| ShellCheck (`-S warning`) on `deploy-web.sh`, `deploy-azure.sh` | Clean |
| Browser, local API (bypass) + Azurite + Vite proxy | Upload → redirect → queued status with polling; delete → deleted with restore deadline; Ask → chat-disabled message; unknown document, unknown route, and malformed ID pages |
| Browser, sign-in enabled (placeholder tenant) | Protected route → `/signin?returnTo=…`; sign-in builds an Entra authorize request with PKCE (`S256`), scope `api://<client-id>/access_as_user`, and redirect URI `<origin>/` |

**Pending live verification** (needs a pipeline run):

- Static Web App provisioning and SWA CLI publish.
- Redirect URI registration by the pipeline identity.
- Real Entra sign-in and token acquisition.
- Cross-origin calls to the deployed API.
- Restore against real blob soft delete.
- Ask answers once chat is enabled.

## Known limitations and next steps

- **No document list (page 3).** It needs `GET /documents` (owner-scoped,
  paged) and is out of scope for this change.
- **File names are not persisted.** The API stores only a sanitised blob name
  and does not return it. Adding `fileName` (and size) to the metadata and the
  status response would make the detail heading reliable.
- **`/ask` sources are blob URLs.** Returning `documentId` and `fileName` instead
  would remove the client-side URL parsing.
- **Bundle size.** Fluent UI and MSAL make a single ~900 kB chunk (Vite warns).
  Route-level code splitting can reduce first load if needed.
- **Chat** is off in the development environment, so Ask shows the "not
  available" message until `deployChat`/`OpenAI:ChatEnabled` are enabled.
