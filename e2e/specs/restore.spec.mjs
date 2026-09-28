import { expect, request, test } from '@playwright/test'
import { execFileSync, spawn } from 'node:child_process'
import { mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  apiEnvironment, connectionString, CONTAINER, copyIntoContainer, RESTORE_CONTAINER, RESTORE_DB_PORT,
  ROOT, seededTenantPublicId, startDatabase, stopDatabase,
} from '../stack.mjs'

/**
 * The restore drill (Milestone 2, items 13 and 15): back up the database the journey has just
 * filled, restore it into a separate empty one with the shipped scripts, start the APIs against
 * the copy, and prove it WORKS. The scripts completing without an error proves much less.
 *
 * What "works" means here, each checked through the running services:
 *   - the tenant's data reads back identically: employees, tickets, journal, trial balance, periods;
 *   - a session revoked AFTER the backup was taken stays revoked on the restored copy. Without
 *     ops/after-restore.sql it would be live again, because the restored row predates the sign-out;
 *   - a fresh sign-in works, so the refusal above is revocation, not a broken copy;
 *   - with delivery paused, a message restored as Pending is not sent at startup, and neither is an
 *     invitation staged on the copy;
 *   - the restore script refuses to run over a database that is not empty.
 * ops/restore.sh itself checks row counts against the backup's manifest and runs 99-verify.sql.
 *
 * Runs last, after every other project has written its data, so there is as much as possible to
 * restore: two tenants, tickets, a ledger with a reversal and a closed period.
 */

const ADMIN = { email: 'admin@e2e.test', password: 'correct horse battery' }

const LIVE = { core: 'http://localhost:5100', tickets: 'http://localhost:5102', ledger: 'http://localhost:5104' }
const RESTORED = { core: 'http://localhost:5200', tickets: 'http://localhost:5202', ledger: 'http://localhost:5204' }

/** Fixed once, so both reads of the trial balance ask for the same day even across midnight UTC. */
const asOf = new Date().toISOString().slice(0, 10)

/** API processes started against the restored copy; stopped in afterAll. */
const restoredApis = []
let workDirectory

test.afterAll(() => {
  // Each was started as its own process group, so this reaches the app `dotnet run` launched as
  // well as `dotnet run` itself.
  for (const api of restoredApis) {
    try { process.kill(-api.pid, 'SIGTERM') } catch { /* already gone */ }
  }
  stopDatabase(RESTORE_CONTAINER)
  if (workDirectory) rmSync(workDirectory, { recursive: true, force: true })
})

async function signIn(coreBase) {
  const api = await request.newContext()
  const response = await api.post(`${coreBase}/api/core/v1/auth/sign-in`, { data: ADMIN })
  expect(response.status(), await response.text()).toBe(200)
  const csrf = (await api.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  return { api, csrf }
}

async function getJson(api, url) {
  const response = await api.get(url)
  expect(response.status(), `${url}: ${await response.text()}`).toBe(200)
  return response.json()
}

/** Everything the tenant admin can read that the restore must bring back unchanged. */
async function readTenantData(api, base) {
  return {
    employees: await getJson(api, `${base.core}/api/core/v1/employees`),
    tickets: await getJson(api, `${base.tickets}/api/tickets/v1/tickets?includeClosed=true`),
    accounts: await getJson(api, `${base.ledger}/api/ledger/v1/accounts`),
    entries: await getJson(api, `${base.ledger}/api/ledger/v1/entries`),
    trialBalance: await getJson(api, `${base.ledger}/api/ledger/v1/trial-balance?asOf=${asOf}`),
    periods: await getJson(api, `${base.ledger}/api/ledger/v1/periods`),
  }
}

/** Runs a command inside a container; returns its output, or throws with that output. */
function inContainer(container, command, env = {}) {
  const envFlags = Object.entries(env).flatMap(([name, value]) => ['-e', `${name}=${value}`])
  try {
    return execFileSync('docker', ['exec', ...envFlags, container, 'bash', '-c', command],
      { stdio: ['ignore', 'pipe', 'pipe'] }).toString()
  } catch (error) {
    throw new Error(`${command} failed in ${container}:\n${error.stdout}\n${error.stderr}`)
  }
}

/**
 * Presents a saved session cookie to a core.api, as someone who kept a copy of it would.
 *
 * A fresh context every time. The 401 for a revoked session also deletes the cookie, and a shared
 * context would obey that, so the next replay would carry no cookie at all and be refused for a
 * different reason — an anonymous 401, which proves nothing about revocation.
 */
async function replaySession(savedCookies, coreBase) {
  const replay = await request.newContext({ storageState: savedCookies })
  const response = await replay.get(`${coreBase}/api/core/v1/auth/session`)
  const result = { status: response.status(), body: await response.text() }
  await replay.dispose()
  return result
}

function startRestoredApi(project, url, env) {
  const api = spawn('dotnet', [
    'run', '--project', join(ROOT, project), '--no-launch-profile', '--no-restore', '--urls', url,
  ], { env: { ...process.env, ...env }, detached: true, stdio: 'ignore' })
  restoredApis.push(api)
}

/** Ready means answering HTTP at all; a 401 from an endpoint that needs a session is healthy. */
async function waitUntilAnswering(url) {
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    try {
      await fetch(url)
      return
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 1000))
    }
  }
  throw new Error(`${url} did not answer within 120s`)
}

test('a backup restores into a working, verified copy', async () => {
  test.setTimeout(300_000)
  workDirectory = mkdtempSync(join(tmpdir(), 'ap-restore-drill-'))

  // The empty target, started first because it is where the private key is made. The backup side
  // is only ever given the public key: the machine that takes backups cannot read them.
  startDatabase({ container: RESTORE_CONTAINER, port: RESTORE_DB_PORT })
  for (const container of [CONTAINER, RESTORE_CONTAINER]) {
    copyIntoContainer(container, 'ops')
    copyIntoContainer(container, 'database/privileges')
  }
  inContainer(RESTORE_CONTAINER, 'mkdir -p /drill && age-keygen -o /drill/identity.txt 2>/dev/null')
  const recipient = inContainer(RESTORE_CONTAINER, 'age-keygen -y /drill/identity.txt').trim()

  // BEFORE: what the live system holds, and a session that is live when the backup is taken.
  const live = await signIn(LIVE.core)
  const before = await readTenantData(live.api, LIVE)
  expect(before.tickets.length, 'the journey should have left tickets to restore').toBeGreaterThan(0)
  expect(before.entries.length, 'the ledger spec should have left entries to restore').toBeGreaterThan(0)
  const signedInCookies = await live.api.storageState()

  // BACK UP the journey's database, while its APIs are still running against it.
  const backupOutput = inContainer(CONTAINER,
    `rm -rf /drill/out && mkdir -p /drill/out && /repo/ops/backup.sh --recipient ${recipient} --out /drill/out`,
    { PGUSER: 'postgres', PGDATABASE: 'appplatform' })
  const backupName = inContainer(CONTAINER, 'ls /drill/out').trim()
  expect(backupName, backupOutput).toMatch(/^appplatform-\d{8}T\d{6}Z\.tar\.age$/)

  // AFTER the backup point, the session is signed out, so its row in the backup still says live.
  const signedOut = await live.api.post(`${LIVE.core}/api/core/v1/auth/sign-out`, {
    headers: { 'X-CSRF-Token': live.csrf },
  })
  expect(signedOut.ok(), await signedOut.text()).toBe(true)
  // The same cookie, replayed as someone who kept a copy would; sign-out cleared the context's own.
  const refusedLive = await replaySession(signedInCookies, LIVE.core)
  expect(refusedLive, 'the sign-out must revoke the session on the live system')
    .toEqual({ status: 401, body: expect.stringContaining('session_revoked') })

  // Moved off the backup host, as it would be.
  execFileSync('docker', ['cp', `${CONTAINER}:/drill/out/${backupName}`, join(workDirectory, backupName)])
  execFileSync('docker', ['cp', join(workDirectory, backupName), `${RESTORE_CONTAINER}:/drill/${backupName}`])

  // RESTORE with the shipped script. It fails here on a manifest mismatch or a 99-verify finding.
  const restoreCommand = `/repo/ops/restore.sh --identity /drill/identity.txt /drill/${backupName}`
  const restoreEnv = { PGUSER: 'postgres', PGDATABASE: 'appplatform' }
  const restoreOutput = inContainer(RESTORE_CONTAINER, restoreCommand, restoreEnv)
  expect(restoreOutput).toContain('restored and verified')

  // Run a second time, the target is no longer empty, and it must refuse rather than restore over it.
  expect(() => inContainer(RESTORE_CONTAINER, restoreCommand, restoreEnv)).toThrow(/is not empty/)

  // The case the pause exists for: a message Pending at the backup point but delivered afterwards
  // on the live system, so the restored copy would send it again. The live worker drains its
  // outbox within seconds, so the drill cannot reliably take a backup while one is Pending.
  // Instead it puts one delivered message back into the state a restore would leave it in.
  const restoredPendingId = inContainer(RESTORE_CONTAINER,
    'psql -U postgres -d appplatform -qtAc "' +
    "UPDATE core.outbox_message SET status = 'Pending', completed_at = NULL, locked_until = NULL, " +
    "next_attempt_at = now() WHERE id = (SELECT id FROM core.outbox_message " +
    "WHERE status = 'Succeeded' ORDER BY created_at LIMIT 1) RETURNING id" + '"').trim()
  expect(restoredPendingId, 'the journey should have left a delivered message').toMatch(/^[0-9a-f-]{36}$/)

  // START THE APIS against the copy, delivery paused as the runbook says.
  const restoredCapture = join(workDirectory, 'mail')
  mkdirSync(restoredCapture)
  const env = apiEnvironment({
    connection: connectionString(RESTORE_DB_PORT),
    tenantPublicId: seededTenantPublicId(),
    capturePath: restoredCapture,
  })
  startRestoredApi('core.api', RESTORED.core, { ...env, Outbox__DeliveryPaused: 'true' })
  startRestoredApi('apps/tickets/tickets.api', RESTORED.tickets, env)
  startRestoredApi('apps/ledger/ledger.api', RESTORED.ledger, env)
  await waitUntilAnswering(`${RESTORED.core}/api/core/v1/auth/session`)
  await waitUntilAnswering(`${RESTORED.tickets}/api/tickets/v1/tickets`)
  await waitUntilAnswering(`${RESTORED.ledger}/api/ledger/v1/accounts`)

  // The session signed out after the backup point stays signed out.
  const refusedRestored = await replaySession(signedInCookies, RESTORED.core)
  expect(refusedRestored, 'a session revoked after the backup must not come back')
    .toEqual({ status: 401, body: expect.stringContaining('session_revoked') })

  // A fresh sign-in works, and the tenant's data is exactly what it was.
  const restored = await signIn(RESTORED.core)
  expect(await readTenantData(restored.api, RESTORED)).toEqual(before)

  // DELIVERY PAUSED: neither the restored Pending message nor an invitation staged on the copy is
  // sent. The worker would poll every 2s, so three polls' worth of nothing is the evidence.
  const email = `restore-drill-${Date.now()}@e2e.test`
  const employee = await restored.api.post(`${RESTORED.core}/api/core/v1/employees`, {
    headers: { 'X-CSRF-Token': restored.csrf },
    data: { firstName: 'Restore', lastName: 'Drill', email },
  })
  expect(employee.ok(), await employee.text()).toBe(true)
  const invited = await restored.api.post(
    `${RESTORED.core}/api/core/v1/employees/${(await employee.json()).employeeId}/invite`, {
      headers: { 'X-CSRF-Token': restored.csrf },
      data: { role: 'member' },
    })
  expect(invited.ok(), await invited.text()).toBe(true)

  await new Promise((resolve) => setTimeout(resolve, 6_000))
  const statusWhere = (condition) => inContainer(RESTORE_CONTAINER,
    `psql -U postgres -d appplatform -tAc "SELECT status FROM core.outbox_message WHERE ${condition}"`).trim()
  expect(statusWhere(`id = '${restoredPendingId}'`), 'the restored Pending message').toBe('Pending')
  expect(statusWhere(`destination = '${email}'`), 'the invitation staged on the copy').toBe('Pending')
  expect(readdirSync(restoredCapture, { withFileTypes: true }).filter((f) => f.isFile())).toEqual([])

  await Promise.all([live.api.dispose(), restored.api.dispose()])
})
