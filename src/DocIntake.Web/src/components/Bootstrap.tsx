import { FluentProvider, MessageBar, MessageBarBody, MessageBarTitle, webLightTheme } from '@fluentui/react-components'
import { StrictMode, type ReactNode } from 'react'
import { ErrorBoundary } from './ErrorBoundary'

export function Shell({ children }: { children: ReactNode }) {
  return (
    <StrictMode>
      <FluentProvider theme={webLightTheme}>
        <ErrorBoundary>{children}</ErrorBoundary>
      </FluentProvider>
    </StrictMode>
  )
}

export function StartupError({ title, message }: { title: string; message: string }) {
  return (
    <div style={{ maxWidth: 640, margin: '48px auto', padding: 16 }}>
      <MessageBar intent="error">
        <MessageBarBody>
          <MessageBarTitle>{title}</MessageBarTitle>
          {message}
        </MessageBarBody>
      </MessageBar>
    </div>
  )
}
