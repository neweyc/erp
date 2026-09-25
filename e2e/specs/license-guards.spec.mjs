import { expect, request, test } from '@playwright/test'
import { OPERATOR_EMAIL, OPERATOR_PASSWORD, seededTenantPublicId } from '../stack.mjs'

const PLATFORM = 'http://localhost:5101'

/**
 * Negative cases on the entitlement endpoint.
 *
 * Deliberately NOT in the `license` project. The journey depends on `license`, and Playwright skips
 * dependents when any test in a dependency fails — so a regression in a guard that has nothing to
 * do with preparing the journey would report the whole browser journey as skipped, hiding whether
 * it still works.
 */
test('a grant without the csrf token is refused', async () => {
  const tenantId = seededTenantPublicId()
  const api = await request.newContext({ baseURL: PLATFORM })

  await api.post('/api/platform/v1/auth/sign-in', {
    data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD },
  })

  const forged = await api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    data: { app: 'tickets', licensed: true },
  })

  // The operator surface is where a forged mutation does the most damage, so this runs against the
  // running service rather than only in a unit pipeline.
  expect(forged.status()).toBe(403)
  expect(await forged.text()).toContain('csrf_failed')

  await api.dispose()
})

test('an unauthenticated grant is refused', async () => {
  const tenantId = seededTenantPublicId()
  const api = await request.newContext({ baseURL: PLATFORM })

  const anonymous = await api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    data: { app: 'tickets', licensed: true },
  })

  expect([401, 403]).toContain(anonymous.status())

  await api.dispose()
})
