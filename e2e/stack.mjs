import { request } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { createHmac } from 'node:crypto'
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, utimesSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

export const ROOT = resolve(import.meta.dirname, '..')
export const CONTAINER = 'ap-e2e-db'
export const DB_PORT = 55433
/** The restore drill's target: an empty database the backup of the journey's one is restored into. */
export const RESTORE_CONTAINER = 'ap-e2e-restore'
export const RESTORE_DB_PORT = 55434
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

/** A connection string for an e2e database on the given local port, as the superuser (D7). */
export function connectionString(port) {
  return `Host=localhost;Port=${port};Database=appplatform;Username=postgres;Password=e2e`
}

export const CONNECTION = connectionString(DB_PORT)

/**
 * The image every e2e database runs: PostgreSQL plus the backup tools (ops/Dockerfile). Using it
 * for the journey's own database is what lets the restore drill run ops/backup.sh inside it, with
 * the same pg_dump version as the server. Built once; later builds are a cache hit.
 */
export const OPS_IMAGE = 'app-platform-ops'

export function buildOpsImage() {
  execFileSync('docker', ['build', '-q', '-t', OPS_IMAGE, join(ROOT, 'ops')], { stdio: 'ignore' })
}

/**
 * An ephemeral database on its own port, so the normal dev stack can keep running. The port is
 * deliberately not 5432: a test suite that silently targets a developer's real database is a
 * data-loss bug waiting for the first person who assumes otherwise.
 *
 * The restore drill starts a second one, under another name and port, to restore into.
 */
export function startDatabase({ container = CONTAINER, port = DB_PORT } = {}) {
  buildOpsImage()
  execFileSync('docker', ['rm', '-f', container], { stdio: 'ignore' })
  execFileSync('docker', [
    'run', '-d', '--name', container,
    '-e', 'POSTGRES_PASSWORD=e2e',
    '-e', 'POSTGRES_DB=appplatform',
    '-p', `${port}:5432`,
    OPS_IMAGE,
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
        'exec', container, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc', 'SELECT 1',
      ], { stdio: 'ignore' })
      return
    } catch {
      execFileSync('sleep', ['1'])
    }
  }

  throw new Error(
    `e2e database did not accept a query on ${container} within 60s. ` +
    'Check `docker logs ' + container + '`.')
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
    // BOTH tenants. A database warmed by a seed that predates the second tenant would otherwise
    // count as ready, and the isolation spec's core.api would start pinned to a tenant that does
    // not exist — every tenant B sign-in failing as invalid credentials.
    //
    // AND the operator, which is seeded last: a run that failed between the two leaves tenants but
    // no operator, and counted as ready, every operator sign-in then fails as bad credentials.
    const out = execFileSync('docker', [
      'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
      `SELECT (SELECT count(*) FROM platform.tenant WHERE name IN ('${TENANT_NAME}', '${OTHER_TENANT_NAME}'))` +
      ` || '/' || (SELECT count(*) FROM platform.platform_user WHERE email = '${OPERATOR_EMAIL}')`,
    ], { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()

    return out === '2/1'
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

/**
 * Idempotent: safe to call from every config evaluation. Returns both seeded tenants' public ids,
 * because each core.api that signs a tenant in is pinned to one at startup.
 */
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
  } else if (process.env.TEST_WORKER_INDEX === undefined) {
    ensureOperatorSecret()
  }

  return { tenant: seededTenantPublicId(), otherTenant: otherTenantPublicId() }
}

export function stopDatabase(container = CONTAINER) {
  execFileSync('docker', ['rm', '-f', container], { stdio: 'ignore' })
}

/** Copies a directory of this repository into a container at the same relative path under /repo. */
export function copyIntoContainer(container, repoRelativeDirectory) {
  const target = `/repo/${repoRelativeDirectory}`
  execFileSync('docker', ['exec', container, 'mkdir', '-p', target], { stdio: 'ignore' })
  execFileSync('docker', ['cp', `${join(ROOT, repoRelativeDirectory)}/.`, `${container}:${target}`],
    { stdio: 'ignore' })
}

/**
 * The GENERATED scripts, applied by database/init-local.sh — the documented way to initialize a
 * fresh database, not a hand-written schema. Every schema bug this project has hit came from a
 * fixture that had drifted from the migrations, so the browser journey runs against exactly what
 * a deployment would apply: roles, schemas, migrations AS ap_owner, grants.
 *
 * As ap_owner matters for the restore drill: it restores this database and then runs
 * 99-verify.sql, which rejects a published view owned by anything else.
 *
 * Its standard output (progress, and the password advice for a human) is discarded; errors still
 * reach the terminal, and a failure stops the run.
 */
export function applyMigrations() {
  copyIntoContainer(CONTAINER, 'database')
  execFileSync('docker', [
    // Notices silenced: re-granted memberships each print one, which reads as a problem.
    'exec', '-e', 'PGUSER=postgres', '-e', 'PGDATABASE=appplatform',
    '-e', 'PGOPTIONS=-c client_min_messages=warning',
    CONTAINER, 'bash', '/repo/database/init-local.sh',
  ], { stdio: ['ignore', 'ignore', 'inherit'] })
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
    'The outbox worker did not run, delivery failed (check core.outbox_message.last_error), or the ' +
    'message was delivered without a token in its payload.')
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

/**
 * The environment every API process is started with, for a database and the tenant that
 * anonymous sign-in resolves to. Shared by the Playwright config and the restore drill, which
 * starts a second set of APIs against the restored copy.
 */
export function apiEnvironment({ connection, tenantPublicId, capturePath }) {
  return {
    ConnectionStrings__Core: connection,
    ConnectionStrings__Tickets: connection,
    ConnectionStrings__Ledger: connection,
    ConnectionStrings__Platform: connection,
    // Pins the tenant for anonymous sign-in. On localhost there is no hostname to resolve, and
    // falling back to "the first tenant" would be a cross-tenant login.
    Tenant__PublicId: tenantPublicId,
    // Shared key ring. Without it the cookie issued by core.api cannot be decrypted by
    // tickets.api, and every app API answers 401 to a user who has just signed in successfully.
    DataProtection__KeyPath: join(ROOT, 'e2e/.keys'),
    // Turns on the file transport, which is what makes the outbox worker actually deliver. With
    // no transport configured the worker dead-letters every message, and the journey fails with a
    // clear reason rather than hanging.
    Email__CapturePath: capturePath,
    // Every request in this suite comes from one local address, so the production default (10 a
    // minute per address, on anonymous endpoints) would refuse the suite's own sign-ins. The
    // limit itself is proved by rate-limit.spec.mjs against a core.api that keeps a real one.
    RateLimits__AnonymousPerMinute: '1000',
    ASPNETCORE_ENVIRONMENT: 'Development',
  }
}

/** The operator who grants entitlements in the journey. Created by platform.api's own command. */
export const OPERATOR_EMAIL = 'operator@e2e.test'
export const OPERATOR_PASSWORD = 'operator correct horse'

export const PLATFORM = 'http://localhost:5101'

/**
 * The platform's key-encryption key, for THIS SUITE ONLY: it protects nothing but throwaway test
 * operators in a throwaway database. Real keys are never in the repository (KeyEncryptionKey).
 */
export const PLATFORM_KEK_FOR_TESTS_ONLY = 'QPnAVu2bCdrlHkd/84njLirFjxuyl/Z2CMwyxu5zL/I='

/**
 * The operator's authenticator secret, as their phone would hold it. The platform prints it once,
 * at enrolment, and stores it only encrypted, so the suite keeps its own copy here (gitignored),
 * where it survives a warm-stack rerun that skips seeding.
 */
const OPERATOR_SECRET_FILE = join(ROOT, 'e2e/.keys/operator-mfa-secret')
const OPERATOR_SESSION_FILE = join(ROOT, 'e2e/.keys/operator-session.json')
/** When that session signed in. Its modification time cannot say, because reuse refreshes it. */
const OPERATOR_SIGNED_IN_AT_FILE = join(ROOT, 'e2e/.keys/operator-session.signed-in-at')

function runOperatorCommand(command) {
  return execFileSync('dotnet', [
    // --no-restore so CI's prebuild is used rather than re-restored inside this call.
    'run', '--project', join(ROOT, 'platform.api'), '--no-launch-profile', '--no-restore', '--',
    command, OPERATOR_EMAIL,
  ], {
    stdio: ['pipe', 'pipe', 'inherit'],
    input: OPERATOR_PASSWORD + '\n',
    env: {
      ...process.env,
      ConnectionStrings__Platform: CONNECTION,
      Encryption__PlatformKeyEncryptionKey: PLATFORM_KEK_FOR_TESTS_ONLY,
    },
  }).toString()
}

/** Keeps the secret a command printed, and forgets any session signed in with an older one. */
function keepOperatorSecret(output) {
  const prefix = 'MFA secret (shown once): '
  const line = output.split('\n').find((l) => l.startsWith(prefix))
  if (!line) throw new Error(`the operator command printed no MFA secret:\n${output}`)

  mkdirSync(join(ROOT, 'e2e/.keys'), { recursive: true })
  writeFileSync(OPERATOR_SECRET_FILE, line.slice(prefix.length).trim())
  rmSync(OPERATOR_SESSION_FILE, { force: true })
}

export function seedOperator() {
  keepOperatorSecret(runOperatorCommand('create-platform-user'))
}

/**
 * A warm stack whose secret file has gone (deleted .keys, a fresh checkout pointed at an old
 * container) has an operator nobody can sign in as. Re-enrolling is the operator's own recovery
 * path, so the suite uses it too.
 */
export function ensureOperatorSecret() {
  if (!existsSync(OPERATOR_SECRET_FILE)) keepOperatorSecret(runOperatorCommand('reset-platform-user-mfa'))
}

/** RFC 6238, as an authenticator app computes it. The first test in operator-mfa checks it. */
export function totpCode(base32Secret, step) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  let bits = ''
  for (const c of base32Secret.replace(/=+$/, '').toUpperCase()) {
    bits += alphabet.indexOf(c).toString(2).padStart(5, '0')
  }
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)))

  const counter = Buffer.alloc(8)
  counter.writeBigInt64BE(BigInt(step))
  const hash = createHmac('sha1', key).update(counter).digest()

  const offset = hash[hash.length - 1] & 0x0f
  const binary = (hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000
  return binary.toString().padStart(6, '0')
}

export function currentStep() {
  return Math.floor(Date.now() / 30_000)
}

export function operatorSecret() {
  return readFileSync(OPERATOR_SECRET_FILE, 'utf8').trim()
}

/**
 * An operator API context with a session and its CSRF token.
 *
 * Signs in ONCE and reuses the session. An authenticator code is accepted once (RFC 6238 §5.2), so
 * several specs signing in within the same 30 seconds would refuse each other — which is the
 * platform working, not failing. The saved session is forgotten whenever the operator is
 * re-enrolled, and reused only while it cannot have gone idle: operator sessions end after 30
 * minutes without a request, so the file's time is refreshed on every reuse (each is followed by a
 * request) and a file older than 25 minutes is signed in afresh. Separately, a session signed in
 * more than 7 hours ago is never reused: the operator's absolute lifetime is 8. The platform has no
 * read-only endpoint to probe a session with, so this is judged by time rather than asked.
 *
 * A fresh sign-in tries the current step's code, then the next step's (the platform accepts one
 * step of drift): the current one may already have been used by a run moments earlier.
 */
export async function operatorSignIn() {
  const idleFor = existsSync(OPERATOR_SESSION_FILE) ? Date.now() - statSync(OPERATOR_SESSION_FILE).mtimeMs : Infinity
  const ageOf = existsSync(OPERATOR_SIGNED_IN_AT_FILE)
    ? Date.now() - Number(readFileSync(OPERATOR_SIGNED_IN_AT_FILE, 'utf8'))
    : Infinity
  if (idleFor < 25 * 60_000 && ageOf < 7 * 3_600_000) {
    const now = new Date()
    utimesSync(OPERATOR_SESSION_FILE, now, now)
    const api = await request.newContext({ baseURL: PLATFORM, storageState: OPERATOR_SESSION_FILE })
    return { api, csrf: await csrfOf(api) }
  }

  const step = currentStep()
  let lastFailure = ''
  for (const candidate of [step, step + 1]) {
    const api = await request.newContext({ baseURL: PLATFORM })
    const response = await api.post('/api/platform/v1/auth/sign-in', {
      data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD, code: totpCode(operatorSecret(), candidate) },
    })

    if (response.status() === 200) {
      await api.storageState({ path: OPERATOR_SESSION_FILE })
      writeFileSync(OPERATOR_SIGNED_IN_AT_FILE, String(Date.now()))
      return { api, csrf: await csrfOf(api) }
    }

    lastFailure = `${response.status()} ${await response.text()}`
    await api.dispose()
  }

  throw new Error(`operator sign-in failed with both codes: ${lastFailure}`)
}

async function csrfOf(api) {
  const csrf = (await api.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  if (!csrf) throw new Error('the operator session has no csrf token')
  return csrf
}

export function seed() {
  execFileSync('dotnet', [
    'run', '--project', join(ROOT, 'core.api'), '--no-launch-profile', '--no-restore', '--', 'seed-e2e',
  ], {
    stdio: 'inherit',
    env: { ...process.env, ConnectionStrings__Core: CONNECTION },
  })
}

/** Names as `SeedE2E` writes them. */
export const TENANT_NAME = 'E2E Ltd'
export const OTHER_TENANT_NAME = 'Other Ltd'

/** The journey's tenant. */
export function seededTenantPublicId() {
  return tenantPublicId(TENANT_NAME)
}

/** The second tenant, which exists only to prove it cannot reach the first. */
export function otherTenantPublicId() {
  return tenantPublicId(OTHER_TENANT_NAME)
}

function tenantPublicId(name) {
  const out = execFileSync('docker', [
    'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
    `SELECT public_id FROM platform.tenant WHERE name = '${name}'`,
  ]).toString().trim()

  if (!out) throw new Error(`seed did not create the tenant '${name}'`)
  return out
}
