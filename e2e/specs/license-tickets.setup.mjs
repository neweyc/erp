import { expect, request, test } from '@playwright/test'
import { OPERATOR_EMAIL, OPERATOR_PASSWORD, seededTenantPublicId } from '../stack.mjs'

const PLATFORM = 'http://localhost:5101'
const CORE = 'http://localhost:5100'
const TICKETS = 'http://localhost:5102'
const ADMIN_EMAIL = 'admin@e2e.test'
const ADMIN_PASSWORD = 'correct horse battery'

/** An operator context with a session and its CSRF token. */
async function signInAsOperator() {
  const api = await request.newContext({ baseURL: PLATFORM })

  const signIn = await api.post('/api/platform/v1/auth/sign-in', {
    data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD },
  })
  expect(signIn.status(), await signIn.text()).toBe(200)

  const csrf = (await api.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  expect(csrf, 'sign-in must issue a csrf token').toBeTruthy()

  return { api, csrf }
}

/**
 * A tenant-admin context. Separate from the operator's on purpose: they are different identities
 * with different cookies, and sharing a jar would hide a mistake that matters.
 */
async function signInAsTenantAdmin(baseURL) {
  const api = await request.newContext({ baseURL })

  const signIn = await api.post(`${CORE}/api/core/v1/auth/sign-in`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  })
  expect(signIn.status(), await signIn.text()).toBe(200)

  const csrf = (await api.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  return { api, csrf }
}

/**
 * An operator licenses the tickets app for the tenant, over HTTP.
 *
 * This replaces a raw `INSERT INTO platform.tenant_app` in the seed. The insert worked, but it
 * left the operator path — sign-in, session, CSRF, the entitlement handler, the audit entry —
 * completely unexercised, and made the journey depend on a manual database edit.
 */
test('the tickets API refuses an unlicensed tenant, and accepts once an operator licenses it', async () => {
  const tenantId = seededTenantPublicId()

  // BEFORE the grant. Signed in as a real tenant admin with a real session and CSRF token, so
  // this is the entitlement filter refusing a fully authenticated caller — not an anonymous
  // request being turned away by authorization.
  const tenant = await signInAsTenantAdmin(TICKETS)

  const refused = await tenant.api.post('/api/tickets/v1/tickets', {
    headers: { 'X-CSRF-Token': tenant.csrf },
    data: { title: 'Before licensing' },
  })

  // 403, not 404: the endpoint exists and the tenant could license it. A 404 would send an
  // administrator hunting for a broken deployment.
  expect(refused.status(), await refused.text()).toBe(403)
  expect(await refused.text()).toContain('app_not_licensed')

  // The grant, by an operator, over HTTP.
  const operator = await signInAsOperator()

  const granted = await operator.api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    headers: { 'X-CSRF-Token': operator.csrf },
    data: { app: 'tickets', licensed: true },
  })

  expect(granted.status(), await granted.text()).toBe(200)
  expect(await granted.json()).toMatchObject({ app: 'tickets', licensed: true })

  // AFTER the grant, on the SAME tenant session. Entitlement is read per request, so licensing
  // takes effect without signing in again — and the API, not the shell, is what enforces it.
  const accepted = await tenant.api.post('/api/tickets/v1/tickets', {
    headers: { 'X-CSRF-Token': tenant.csrf },
    data: { title: 'After licensing' },
  })

  expect(accepted.status(), await accepted.text()).toBe(200)

  await tenant.api.dispose()
  await operator.api.dispose()
})

test('the same grant twice is refused rather than silently duplicated', async () => {
  const tenantId = seededTenantPublicId()
  const { api, csrf } = await signInAsOperator()

  const again = await api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    headers: { 'X-CSRF-Token': csrf },
    data: { app: 'tickets', licensed: true },
  })

  // Two live grants for one app would make "is this licensed" depend on which row a query read.
  expect(again.status()).toBe(409)
  expect(await again.text()).toContain('app_already_licensed')

  await api.dispose()
})

test('a grant without the csrf token is refused', async () => {
  const tenantId = seededTenantPublicId()
  const { api } = await signInAsOperator()

  const forged = await api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    data: { app: 'tickets', licensed: true },
  })

  // The operator surface is where a forged mutation does the most damage, so this is checked
  // against the running service rather than only in a unit pipeline.
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
