import { defineConfig } from '@playwright/test'
import { join } from 'node:path'
import {
  apiEnvironment, CAPTURE_DIR, CONNECTION, ensureStack, PLATFORM_KEK_FOR_TESTS_ONLY, ROOT, tenantOrigin,
} from './stack.mjs'

/**
 * The database is prepared HERE, at config-module load, rather than in globalSetup.
 *
 * Ordering is the reason. Playwright evaluates this module — including the webServer commands
 * and their env — BEFORE globalSetup runs, and the APIs must start against a database that already
 * holds the seeded tenants and the operator.
 *
 * ensureStack is IDEMPOTENT for the matching reason: this module is evaluated again in each
 * worker process, and a second unconditional rebuild would destroy the database the already-
 * running APIs are connected to.
 */
ensureStack()

const apiEnv = apiEnvironment({ connection: CONNECTION, capturePath: CAPTURE_DIR })

/**
 * The shared secret for core's provisioning endpoint. platform.api sends it; the main core.api
 * accepts it, so the operator console can create a tenant end to end, as in a deployment. It
 * stays off for every other server: it is precisely what turns core's anonymous provisioning
 * endpoint from a 404 into a live tenant-creation route.
 */
const INTERNAL_KEY = 'e2e-internal-key'

const coreEnv = { ...apiEnv, Internal__ApiKey: INTERNAL_KEY }

/** Only the platform receives its key-encryption key, as in a deployment. */
const platformEnv = {
  ...apiEnv,
  Encryption__PlatformKeyEncryptionKey: PLATFORM_KEK_FOR_TESTS_ONLY,
  Internal__ApiKey: INTERNAL_KEY,
  Core__BaseUrl: 'http://localhost:5100',
}

/**
 * A second core.api with a REAL anonymous rate limit (10 a minute), which rate-limit.spec.mjs
 * exhausts. Every other server's limit is high enough never to bite, because every request in this
 * suite comes from one address. Nothing else signs in here.
 */
const limitedCoreEnv = {
  ...apiEnv,
  RateLimits__AnonymousPerMinute: '10',
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
  // A chain, because the journey's steps genuinely depend on each other and each is being proved
  // rather than faked. Ordering across files is not otherwise guaranteed, and the licensing spec
  // needs an accepted admin so it can show the tickets API refusing BEFORE the grant and
  // accepting after.
  projects: [
    // Pure logic; no stack, no order. Also asserts every spec file is claimed by a project.
    { name: 'unit', testMatch: /.*\.unit\.mjs/ },
    { name: 'invite', testMatch: /accept-invite\.setup\.mjs/ },
    { name: 'license', testMatch: /license-tickets\.setup\.mjs/, dependencies: ['invite'] },
    { name: 'journey', testMatch: /journey\.spec\.mjs/, dependencies: ['license'] },
    // Guards depend on `license` but nothing depends on them, so a guard regression cannot mask
    // the journey by making it skip.
    { name: 'guards', testMatch: /license-guards\.spec\.mjs/, dependencies: ['license'] },
    // A2. Needs tenant A licensed with an accepted admin, which `license` guarantees. Nothing
    // depends on it, for the same reason as `guards`.
    { name: 'isolation', testMatch: /tenant-isolation\.spec\.mjs/, dependencies: ['license'] },
    // The ledger proof of concept: licenses the ledger for tenant A, then drives it in the browser.
    // After `license` so the admin has accepted their invitation; nothing depends on it.
    { name: 'ledger', testMatch: /ledger\.spec\.mjs/, dependencies: ['license'] },
    // M2 item 16: operator sign-in needs a current authenticator code. After `license`, whose
    // saved operator session the other specs reuse, so its own sign-ins cannot use their codes up.
    { name: 'operator-mfa', testMatch: /operator-mfa\.spec\.mjs/, dependencies: ['license'] },
    // M2 item 17: the operator error feed, served to operators only. Reuses the saved operator
    // session, so after `license`.
    { name: 'error-feed', testMatch: /error-feed\.spec\.mjs/, dependencies: ['license'] },
    // Exhausts the anonymous rate limit on its own core (5103). Nothing depends on it.
    { name: 'rate-limit', testMatch: /rate-limit\.spec\.mjs/, dependencies: ['isolation'] },
    // D17: an employee invited by the admin accepts and signs in. Needs the admin accepted.
    { name: 'employee-invite', testMatch: /employee-invite\.spec\.mjs/, dependencies: ['license'] },
    // Resending an invitation, deactivating and reactivating, through the Users page.
    { name: 'user-lifecycle', testMatch: /user-lifecycle\.spec\.mjs/, dependencies: ['license'] },
    // Adding, inviting and terminating from the Employees page.
    { name: 'employee-roster', testMatch: /employee-roster\.spec\.mjs/, dependencies: ['license'] },
    // Departments, job titles and locations, and placing someone in them.
    { name: 'organization-structure', testMatch: /organization-structure\.spec\.mjs/, dependencies: ['license'] },
    // The operator console in the browser: sign in with a code, create and license a tenant,
    // suspend and resume it. After operator-mfa, the other spec that signs in with fresh codes, so
    // neither can use up a code the other was about to use.
    { name: 'operator-console', testMatch: /operator-console\.spec\.mjs/, dependencies: ['operator-mfa'] },
    // The restore drill backs up everything the projects above wrote, so it runs after all of them.
    {
      name: 'restore',
      testMatch: /restore\.spec\.mjs/,
      dependencies: ['journey', 'guards', 'isolation', 'ledger', 'employee-invite', 'user-lifecycle', 'employee-roster',
        'organization-structure', 'operator-console'],
    },
  ],
  timeout: 30_000,
  use: {
    // The journey's tenant, at its own address: sign-in reads the tenant from the host.
    baseURL: tenantOrigin(5273),
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: `dotnet run --project ${join(ROOT, 'core.api')} --no-launch-profile --no-restore --urls http://localhost:5100`,
      url: 'http://localhost:5100/api/core/v1/auth/session',
      // 401 is a healthy answer from an endpoint that requires a session — it proves the
      // process is up and routing, which is what readiness means here.
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 120_000,
      env: coreEnv,
    },
    {
      command: `dotnet run --project ${join(ROOT, 'apps/tickets/tickets.api')} --no-launch-profile --no-restore --urls http://localhost:5102`,
      url: 'http://localhost:5102/api/tickets/v1/tickets',
      reuseExistingServer: false,
      timeout: 120_000,
      env: apiEnv,
    },
    {
      // The operator surface. Present because granting an entitlement is an operator action, and
      // the journey performs it over HTTP rather than with a database edit.
      command: `dotnet run --project ${join(ROOT, 'platform.api')} --no-launch-profile --no-restore --urls http://localhost:5101`,
      // The console's session route: 401 with no operator is a healthy answer, as for core.
      url: 'http://localhost:5101/api/platform/v1/auth/session',
      reuseExistingServer: false,
      timeout: 120_000,
      env: platformEnv,
    },
    {
      command: `dotnet run --project ${join(ROOT, 'apps/ledger/ledger.api')} --no-launch-profile --no-restore --urls http://localhost:5104`,
      url: 'http://localhost:5104/api/ledger/v1/accounts',
      reuseExistingServer: false,
      timeout: 120_000,
      env: apiEnv,
    },
    {
      command: `dotnet run --project ${join(ROOT, 'core.api')} --no-launch-profile --no-restore --urls http://localhost:5103`,
      url: 'http://localhost:5103/api/core/v1/auth/session',
      reuseExistingServer: false,
      timeout: 120_000,
      env: limitedCoreEnv,
    },
    {
      // The operator console. Its dev server proxies /api/platform to the platform.api above.
      command: `npm run dev --workspace @app-platform/console -- --port 5274 --strictPort`,
      url: 'http://localhost:5274',
      cwd: ROOT,
      reuseExistingServer: false,
      timeout: 120_000,
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
