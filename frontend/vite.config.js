import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { mockApi } from './dev/mockApi.js'

const API_TARGET = 'http://localhost:5258'

export default defineConfig(({ command, mode }) => ({
  // `npm run dev:mock` serves canned /api answers so the UI can be exercised without the backend.
  // Dev-server only: plugins with configureServer never run during `vite build`.
  plugins: [react(), command === 'serve' && mode === 'mock' && mockApi()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: API_TARGET,
        changeOrigin: true,
        configure(proxy) {
          // When the backend is not running, answer with a marked 502 so the API client can
          // tell "API not running" apart from a real server error (Vite would send a bare 502).
          proxy.on('error', (_err, _req, res) => {
            if (!('req' in res) || res.headersSent || res.writableEnded) return
            res.writeHead(502, {
              'Content-Type': 'application/problem+json',
              'X-Dev-Proxy-Error': 'backend-unreachable',
            })
            res.end(
              JSON.stringify({
                status: 502,
                title: 'API not reachable',
                detail: `The dev server could not reach the backend at ${API_TARGET}.`,
                code: 'dev.backend_unreachable',
              }),
            )
          })
        },
      },
    },
  },
}))
