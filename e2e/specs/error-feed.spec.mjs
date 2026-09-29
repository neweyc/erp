import { expect, request, test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { randomInt } from 'node:crypto'
import { CONTAINER, operatorSignIn, PLATFORM, seededTenantPublicId, TENANT_NAME } from '../stack.mjs'

/** Adds one occurrence through the published function, as a service's writer would. */
function recordOccurrence(reference, tenantName) {
  execFileSync('docker', [
    'exec', CONTAINER, 'psql', '-U', 'postgres', '-d', 'appplatform', '-tAc',
    "SELECT platform_v1.record_error('" + reference + "', '0123456789abcdef', 'tickets', " +
    `(SELECT id FROM platform.tenant WHERE name = '${tenantName}'), 3, now())`,
  ], { stdio: 'ignore' })
}

function newReference() {
  const alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
  return 'err_' + Array.from({ length: 25 }, () => alphabet[randomInt(alphabet.length)]).join('')
}

/**
 * M2 item 17: the operator error feed is served by the running platform, to operators only.
 *
 * What reaches the feed, and what cannot, is proved against real PostgreSQL in privileges.tests
 * (ErrorFeedTests): the whole path from a throwing endpoint to a row, under the reference the
 * customer saw. This checks the deployed endpoint and its guard.
 */

test('an operator reads the feed', async () => {
  // A known occurrence, so the checks below have a row to look at rather than passing on an empty feed.
  const reference = newReference()
  recordOccurrence(reference, TENANT_NAME)
  const { api } = await operatorSignIn()

  const response = await api.get('/api/platform/v1/errors?limit=50')

  expect(response.status(), await response.text()).toBe(200)
  const row = (await response.json()).find((r) => r.reference === reference)
  expect(row, 'the occurrence just recorded is in the feed').toBeTruthy()
  // Metadata only: exactly these fields, and nothing that could carry a message. The tenant by its
  // public id, never its internal key.
  expect(Object.keys(row).sort()).toEqual(['app', 'count', 'fingerprint', 'occurredAt', 'reference', 'tenantId'])
  expect(row).toMatchObject({ app: 'tickets', count: 3, fingerprint: '0123456789abcdef', tenantId: seededTenantPublicId() })

  await api.dispose()
})

test('nobody else does', async () => {
  const anonymous = await request.newContext({ baseURL: PLATFORM })

  const response = await anonymous.get('/api/platform/v1/errors')

  expect(response.status()).toBe(401)
  await anonymous.dispose()
})
