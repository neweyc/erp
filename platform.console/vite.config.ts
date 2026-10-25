import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // Same-origin, as in production, so the operator cookie behaves as it will there. The console
    // talks to platform.api and nothing else: no customer API is proxied, so none can be called.
    proxy: {
      '/api/platform': { target: 'http://localhost:5101', changeOrigin: true },
    },
  },
  test: {
    environment: 'happy-dom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      // Bootstrap only: it mounts the app and is exercised by every browser run.
      exclude: ['src/main.tsx', 'src/test-setup.ts', 'src/test-support.tsx', 'src/**/*.test.{ts,tsx}'],
      // A hard gate (platform.console/CLAUDE.md): the operator surface is where a silent bug is
      // found by a customer, so every line is run by a test or the run fails.
      thresholds: { lines: 100 },
    },
  },
})
