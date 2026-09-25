import { execFileSync } from 'node:child_process'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

export const ROOT = resolve(import.meta.dirname, '..')
export const CONTAINER = 'ap-e2e-db'
export const DB_PORT = 55433
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

  for (let i = 0; i < 60; i++) {
    try {
      execFileSync('docker', ['exec', CONTAINER, 'pg_isready', '-U', 'postgres', '-d', 'appplatform'],
        { stdio: 'ignore' })
      return
    } catch {
      execFileSync('sleep', ['1'])
    }
  }

  throw new Error('e2e database did not become ready')
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

/** Idempotent: safe to call from every config evaluation. Returns the seeded tenant's public id. */
export function ensureStack() {
  if (!isStackReady()) {
    startDatabase()
    applyMigrations()
    seed()
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
