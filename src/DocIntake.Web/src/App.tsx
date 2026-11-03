import { Navigate, Route, Routes } from 'react-router-dom'
import { ApiProvider } from './api/ApiProvider'
import { RequireAuth } from './auth/RequireAuth'
import { Layout } from './components/Layout'
import { useConfig } from './configContext'
import { AskPage } from './pages/AskPage'
import { DocumentPage } from './pages/DocumentPage'
import { SignInPage } from './pages/SignInPage'
import { StatusPage } from './pages/StatusPage'
import { UploadPage } from './pages/UploadPage'

export function App() {
  const { apiBaseUrl } = useConfig()

  return (
    <ApiProvider baseUrl={apiBaseUrl}>
      <Routes>
        <Route path="/signin" element={<SignInPage />} />
        <Route element={<Layout />}>
          <Route
            index
            element={
              <RequireAuth>
                <UploadPage />
              </RequireAuth>
            }
          />
          <Route
            path="documents/:documentId"
            element={
              <RequireAuth>
                <DocumentPage />
              </RequireAuth>
            }
          />
          <Route
            path="ask"
            element={
              <RequireAuth>
                <AskPage />
              </RequireAuth>
            }
          />
          <Route path="session-expired" element={<StatusPage variant="sessionExpired" />} />
          <Route path="forbidden" element={<StatusPage variant="forbidden" />} />
          <Route path="not-found" element={<StatusPage variant="notFound" />} />
          <Route path="upload" element={<Navigate to="/" replace />} />
          <Route path="*" element={<StatusPage variant="notFound" />} />
        </Route>
      </Routes>
    </ApiProvider>
  )
}
