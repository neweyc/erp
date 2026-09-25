import { execFileSync } from 'node:child_process'
import { mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

export const ROOT = resolve(import.meta.dirname, '..')
export const CONTAINER = 'ap-e2e-db'
export const DB_PORT = 55433
/** Where the file transport drops delivered email. The journey reads invitations from here. */
export const CAPTURE_DIR = resolve(import.meta.dirname, '.mail')

/**
 * Marks when the capture directory was last cleared. The freshness floor for captured mail is
 * read from this file rather than from a module-level timestamp.
 *
 * It has to be shared across processes. Playwright re-imports this module in each worker, so a
 * per-process timestamp is set AFTER the services have already delivered — and the floor then
 * rejects the very message the run just produced. Named without a .json suffix so the capture
 * scan ignores it.
 */
const RESET_MARKER = join(CAPTURE_DIR, '.reset-at')

export const CONNECTION =
  `Host=localhost;Port=${DB_PORT};Database=appplatform;Username=postgres;Password=e2e`

/**
 * An ephemeral database on its own port, so the normal dev stack can keep running. The port is
 * deliberately not 5432: a test suite that silently targets a developer's real database is a
 * data-loss bug waiting for the first person who assumes otherwise.
 */
export function startDatabase() {
  execFileSync('docker', ['rm', '-f', CONTAINER], { stdio: 'ignore' })
  execFileSync('docker', [
    'run', '-d', '--name', CONTAINER,
    '-e', 'POSTGRES_PASSWORD=e2e',
    '-e', 'POSTGRES_DB=appplatform',
    '-p', `${DB_PORT}:5432`,
    'postgres:16-alpine',
  ], { stdio: 'ignore' })

  // Readiness is a real QUERY against the target database, not pg_isready.
  //
  // pg_isready only reports that the server accepts connections. The postgres image runs a
  // temporary server for initdb before creating POSTGRES_DB, so pg_isready can succeed while
  // `appplatform` does not yet exist — measured window around 0.4s. Returning inside it makes the
  // next statement fail with `database "appplatform" does not exist`, which is how this passed
  // locally on a cached image and failed in CI.
  for (let i = 0; i < 60; i++) {
    try {
      execFileSync('docker', [
        'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc', 'SELECT 1',
      ], { stdio: 'ignore' })
      return
    } catch {
      execFileSync('sleep', ['1'])
    }
  }

  throw new Error(
    `e2e database did not accept a query on ${CONTAINER} within 60s. ` +
    'Check `docker logs ' + CONTAINER + '`.')
}

/**
 * True when the container is up AND already seeded.
 *
 * This guard exists because Playwright evaluates the config module more than once — in the main
 * process and again in each worker. Without it the worker's re-evaluation ran `docker rm -f` and
 * rebuilt the database underneath the APIs that were already connected to it, so every spec
 * arrived at a sign-in page that could no longer resolve a tenant. The symptom looked exactly
 * like a broken login form.
 */
export function isStackReady() {
  try {
    const out = execFileSync('docker', [
      'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
      "SELECT 1 FROM platform.tenant WHERE name = 'E2E Ltd'",
    ], { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()

    return out === '1'
  } catch {
    return false
  }
}

/**
 * True when a warm database already has an invitation that was delivered before this run.
 *
 * A warm stack is skipped by `seed()`, so no NEW invitation is staged — and `resetCapture` has
 * just deleted the delivered one. There is then nothing for the journey to find, which used to
 * surface as "the outbox worker did not run" pointing at a row whose status is Succeeded and
 * whose last_error is null. Detected here so the advice names the actual remedy.
 */
function hasOnlyStaleInvitation() {
  try {
    const out = execFileSync('docker', [
      'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
      "SELECT count(*) FROM core.outbox_message WHERE status <> 'Pending'",
    ], { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()

    return Number(out) > 0
  } catch {
    return false
  }
}

/** Idempotent: safe to call from every config evaluation. Returns the seeded tenant's public id. */
export function ensureStack() {
  // Cleared in the MAIN process only. Playwright re-evaluates this config in each worker, where
  // TEST_WORKER_INDEX is set; clearing there would delete mail the running services had already
  // delivered. The freshness floor in waitForInvitation is what protects the warm-stack case
  // that this cannot.
  const warmWithNothingLeftToDeliver =
    process.env.TEST_WORKER_INDEX === undefined && isStackReady() && hasOnlyStaleInvitation()

  // A warm stack whose invitation was already delivered cannot produce a new one, because seed()
  // is skipped. Rebuilding is the only way forward, and doing it here beats failing later with a
  // message that points at the outbox.
  if (warmWithNothingLeftToDeliver) {
    stopDatabase()
  }

  if (process.env.TEST_WORKER_INDEX === undefined) resetCapture()

  if (!isStackReady()) {
    startDatabase()
    applyMigrations()
    seed()
    seedOperator()
  }

  return seededTenantPublicId()
}

export function stopDatabase() {
  execFileSync('docker', ['rm', '-f', CONTAINER], { stdio: 'ignore' })
}

function applyFile(path) {
  execFileSync('docker', ['cp', path, `${CONTAINER}:/tmp/apply.sql`], { stdio: 'ignore' })
  execFileSync('docker', [
    'exec', CONTAINER, 'psql', '-v', 'ON_ERROR_STOP=1',
    '-U', 'postgres', '-d', 'appplatform', '-q', '-f', '/tmp/apply.sql',
  ], { stdio: 'inherit' })
}

/**
 * The GENERATED scripts, in the runbook's order — not a hand-written schema. Every schema bug
 * this project has hit came from a fixture that had drifted from the migrations, so the browser
 * journey runs against exactly what a deployment would apply.
 */
export function applyMigrations() {
  applyFile(join(ROOT, 'database/privileges/01-roles-and-schemas.sql'))
  applyFile(join(ROOT, 'database/platform/migrations-all.sql'))
  applyFile(join(ROOT, 'database/core/migrations-all.sql'))
  applyFile(join(ROOT, 'database/tickets/migrations-all.sql'))
}

/**
 * Waits for the outbox worker to deliver the invitation, then returns its token.
 *
 * Read from the CAPTURED MESSAGE, never from the database. The point is not that the token is
 * unavailable elsewhere — the outbox payload carries it until pruned — but that reading anything
 * the application wrote locally would let this pass even if no invitation were ever delivered.
 */
export async function waitForInvitation(
  toAddress, { timeoutMs = 20_000, notBefore, directory = CAPTURE_DIR } = {}) {
  // Captures from an earlier run can survive: an interrupted run skips globalTeardown, so the
  // container and the capture directory both outlive it. Without a freshness floor, readdir
  // order decides which token the journey uses, and a stale one fails at accept-invite with a
  // message that points at the page rather than at the stale file.
  const floor = notBefore ?? captureFloor()
  const deadline = Date.now() + timeoutMs

  while (Date.now() < deadline) {
    const candidates = readdirSync(directory, { withFileTypes: true })
      .filter((f) => f.isFile() && f.name.endsWith('.json'))
      // A file caught mid-write is skipped, not fatal. The transport writes non-atomically and
      // this polls every 500ms against a worker delivering every 2s, so the window is real — and
      // a parse error here would replace the actionable timeout message with a SyntaxError.
      .map((f) => {
        try {
          return JSON.parse(readFileSync(join(directory, f.name), 'utf8'))
        } catch {
          return null
        }
      })
      .filter((c) => c !== null && c.to === toAddress && c.payload?.token)
      .filter((c) => new Date(c.capturedAt) >= floor)
      // Newest wins, so a surviving older capture for the same address cannot be chosen.
      .sort((a, b) => new Date(b.capturedAt) - new Date(a.capturedAt))

    if (candidates.length > 0) return candidates[0].payload.token

    await new Promise((resolve) => setTimeout(resolve, 500))
  }

  throw new Error(
    `No invitation delivered to ${toAddress} after ${floor.toISOString()} within ${timeoutMs}ms. ` +
    'The outbox worker did not run, or delivery failed — check core.outbox_message.last_error.')
}

export function resetCapture() {
  rmSync(CAPTURE_DIR, { recursive: true, force: true })
  mkdirSync(CAPTURE_DIR, { recursive: true })
  writeFileSync(RESET_MARKER, new Date().toISOString())
}

/** The floor a captured message must be newer than to belong to this run. */
function captureFloor() {
  try {
    return new Date(readFileSync(RESET_MARKER, 'utf8').trim())
  } catch {
    // No marker means nothing was cleared, so accept anything rather than blocking a run on a
    // missing bookkeeping file.
    return new Date(0)
  }
}

/** The operator who grants entitlements in the journey. Created by platform.api's own command. */
export const OPERATOR_EMAIL = 'operator@e2e.test'
export const OPERATOR_PASSWORD = 'operator correct horse'

export function seedOperator() {
  execFileSync('dotnet', [
    'run', '--project', join(ROOT, 'platform.api'), '--no-launch-profile', '--',
    'create-platform-user', OPERATOR_EMAIL,
  ], {
    stdio: ['pipe', 'inherit', 'inherit'],
    input: OPERATOR_PASSWORD + '\n',
    env: { ...process.env, ConnectionStrings__Platform: CONNECTION },
  })
}

export function seed() {
  execFileSync('dotnet', [
    'run', '--project', join(ROOT, 'core.api'), '--no-launch-profile', '--', 'seed-e2e',
  ], {
    stdio: 'inherit',
    env: { ...process.env, ConnectionStrings__Core: CONNECTION },
  })
}

export function seededTenantPublicId() {
  const out = execFileSync('docker', [
    'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
    "SELECT public_id FROM platform.tenant WHERE name = 'E2E Ltd'",
  ]).toString().trim()

  if (!out) throw new Error('seed did not create the E2E tenant')
  return out
}
