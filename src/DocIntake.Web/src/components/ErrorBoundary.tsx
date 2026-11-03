import { Component, type ErrorInfo, type ReactNode } from 'react'
import { StatusPage } from '../pages/StatusPage'

interface State {
  hasError: boolean
}

// Last-resort handler for rendering errors so users see a recoverable page instead of a blank screen.
export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { hasError: false }

  static getDerivedStateFromError(): State {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Unhandled UI error', error, info.componentStack)
  }

  render() {
    if (this.state.hasError) {
      return <StatusPage variant="unexpected" onRetry={() => window.location.assign('/')} />
    }
    return this.props.children
  }
}
