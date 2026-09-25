import { defineConfig } from 'vitest/config'

export default defineConfig({
  // happy-dom because the client reads document.cookie for the CSRF token.
  test: { environment: 'happy-dom', globals: true },
})
