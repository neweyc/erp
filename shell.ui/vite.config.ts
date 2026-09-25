import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    // Same-origin in development too, so the auth cookie behaves exactly as it does in
    // production. A CORS setup here would hide the cookie problems until deployment.
    proxy: {
      '/api/core': { target: 'http://localhost:5100', changeOrigin: true },
      '/api/tickets': { target: 'http://localhost:5102', changeOrigin: true },
      '/api/ledger': { target: 'http://localhost:5104', changeOrigin: true },
    },
  },
  test: {
    environment: 'happy-dom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
})
