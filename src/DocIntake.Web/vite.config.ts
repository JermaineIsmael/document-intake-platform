/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  // Local API started with `dotnet run --project src/DocIntake.Api --launch-profile http`.
  const apiProxyTarget = env.API_PROXY_TARGET || 'http://localhost:5067'

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      // SPA routes such as /documents/:id overlap the API paths, so the API is proxied under /api.
      proxy: {
        '/api': {
          target: apiProxyTarget,
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/api/, ''),
        },
      },
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      css: false,
      restoreMocks: true,
    },
  }
})
