import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // Same-origin in development too, so the auth cookie behaves exactly as it does in
    // production. A CORS setup here would hide the cookie problems until deployment.
    //
    // Core keeps the browser's Host header (changeOrigin: false): sign-in reads which tenant it is
    // for from the host, so acme-fire.localhost:5173 must reach core as acme-fire.localhost, not as
    // localhost:5100. nginx does the same in a deployment.
    proxy: {
      '/api/core': { target: 'http://localhost:5100', changeOrigin: false },
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
