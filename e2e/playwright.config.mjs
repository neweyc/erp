import { defineConfig } from '@playwright/test'
import { join } from 'node:path'
import { CAPTURE_DIR, CONNECTION, ensureStack, ROOT } from './stack.mjs'

/**
 * The database is prepared HERE, at config-module load, rather than in globalSetup.
 *
 * Ordering is the reason. Playwright evaluates this module — including the webServer commands
 * and their env — BEFORE globalSetup runs, so a tenant id written by globalSetup arrives too
 * late and the APIs start without one. Sign-in then cannot resolve a tenant and every spec fails
 * at the first assertion after the form, which looks like a broken login form and is not one.
 *
 * ensureStack is IDEMPOTENT for the matching reason: this module is evaluated again in each
 * worker process, and a second unconditional rebuild would destroy the database the already-
 * running APIs are connected to.
 */
const tenantPublicId = ensureStack()

const apiEnv = {
  ConnectionStrings__Core: CONNECTION,
  ConnectionStrings__Tickets: CONNECTION,
  // Pins the tenant for anonymous sign-in. On localhost there is no hostname to resolve, and
  // falling back to "the first tenant" would be a cross-tenant login.
  Tenant__PublicId: tenantPublicId,
  // Shared key ring. Without it the cookie issued by core.api cannot be decrypted by
  // tickets.api, and every app API answers 401 to a user who has just signed in successfully.
  DataProtection__KeyPath: join(ROOT, 'e2e/.keys'),
  // Turns on the file transport, which is what makes the outbox worker actually deliver. With
  // no transport configured the worker dead-letters every message, and the journey fails with a
  // clear reason rather than hanging.
  Email__CapturePath: CAPTURE_DIR,
  ASPNETCORE_ENVIRONMENT: 'Development',
}

export default defineConfig({
  testDir: './specs',
  globalTeardown: './global-teardown.mjs',
  // One worker: the journey shares one seeded tenant, and parallel specs mutating it would
  // fail in ways that look like product bugs.
  workers: 1,
  fullyParallel: false,
  reporter: [['list']],
  // The invitation must be accepted before anything can sign in, and acceptance is itself part
  // of the journey. A setup project makes that order explicit rather than relying on tests
  // running top to bottom in one file.
  projects: [
    { name: 'setup', testMatch: /.*\.setup\.mjs/ },
    { name: 'journey', testMatch: /journey\.spec\.mjs/, dependencies: ['setup'] },
  ],
  timeout: 30_000,
  use: {
    baseURL: 'http://localhost:5273',
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: `dotnet run --project ${join(ROOT, 'core.api')} --no-launch-profile --urls http://localhost:5100`,
      url: 'http://localhost:5100/api/core/v1/auth/session',
      // 401 is a healthy answer from an endpoint that requires a session — it proves the
      // process is up and routing, which is what readiness means here.
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 120_000,
      env: apiEnv,
    },
    {
      command: `dotnet run --project ${join(ROOT, 'apps/tickets/tickets.api')} --no-launch-profile --urls http://localhost:5102`,
      url: 'http://localhost:5102/api/tickets/v1/tickets',
      reuseExistingServer: false,
      timeout: 120_000,
      env: apiEnv,
    },
    {
      command: `npm run dev --workspace @app-platform/shell -- --port 5273 --strictPort`,
      url: 'http://localhost:5273',
      cwd: ROOT,
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
})
