import { createContext, useContext } from 'react'
import type { AppConfig } from './config'

export const ConfigContext = createContext<AppConfig | null>(null)

export function useConfig(): AppConfig {
  const config = useContext(ConfigContext)
  if (!config) throw new Error('useConfig must be used inside a ConfigContext provider.')
  return config
}
