import { expect, request, test } from '@playwright/test'
import { operatorSignIn, seededTenantPublicId } from '../stack.mjs'

const CORE = 'http://localhost:5100'
const TICKETS = 'http://localhost:5102'
const ADMIN_EMAIL = 'admin@e2e.test'
const ADMIN_PASSWORD = 'correct horse battery'

/** An operator context with a session and its CSRF token (password and authenticator code). */
const signInAsOperator = operatorSignIn

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
  // Asserted here as well as for the operator. Without it, a core that stopped issuing ap_csrf
  // would make the before-grant POST return 403 csrf_failed — the status the entitlement check
  // also returns — and the failure would point at entitlements instead of CSRF issuance.
  expect(csrf, 'tenant sign-in must issue a csrf token').toBeTruthy()

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

  // AFTER the grant, on the SAME tenant session. Entitlement is read per request, so the answer
  // changes without signing in again — but licensing grants nobody a role (docs/permissions.md),
  // so the administrator is now refused for a different, more specific reason. The next test gives
  // them a role; creating a ticket succeeds there.
  const noRole = await tenant.api.post('/api/tickets/v1/tickets', {
    headers: { 'X-CSRF-Token': tenant.csrf },
    data: { title: 'After licensing, before a role' },
  })

  expect(noRole.status(), await noRole.text()).toBe(403)
  expect((await noRole.json()).problemCode).toBe('not_permitted')

  // Granting again, in the same test rather than a separate one. As its own test this passed only
  // because a previous test had granted — which breaks under -g, --repeat-each, or any future
  // retries setting, where a retry would hit 409 on its own first grant.
  const duplicate = await operator.api.post(`/api/platform/v1/tenants/${tenantId}/entitlements`, {
    headers: { 'X-CSRF-Token': operator.csrf },
    data: { app: 'tickets', licensed: true },
  })

  // Two live grants for one app would make "is this licensed" depend on which row a query read.
  // The partial unique index is what guarantees that; this asserts the handler reports it well.
  expect(duplicate.status()).toBe(409)
  expect(await duplicate.text()).toContain('app_already_licensed')

  await tenant.api.dispose()
  await operator.api.dispose()
})

/**
 * The administrator gives themselves a Tickets role on the Users page, in the browser.
 *
 * Licensing granted nobody anything, so the dashboard says so and points here. Changing your own
 * roles ends your session (role_changed); signing in again shows the app. Every later spec that
 * works in Tickets as this administrator depends on this.
 */
test('the administrator gives themselves a tickets role on the Users page', async ({ page }) => {
  async function signIn() {
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()
  }

  await page.goto('/')
  await signIn()
  await expect(page.getByRole('heading', { name: 'E2E Ltd' })).toBeVisible()

  // Licensed but invisible: no role yet, and the dashboard says what to do about it.
  await expect(page.getByRole('link', { name: 'Tickets' })).toHaveCount(0)
  await expect(page.getByText(/no role yet in Tickets/)).toBeVisible()

  // Followed from the hint itself, which is the path an administrator in this position takes.
  await page.getByRole('main').getByRole('link', { name: 'Users' }).click()
  await expect(page.getByRole('heading', { name: 'Users', level: 1 })).toBeVisible()

  // A new page: wide content must scroll inside its container, never widen the document (CLAUDE.md).
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })

  await page.getByRole('button', { name: `Edit roles for ${ADMIN_EMAIL}` }).click()
  await page.getByRole('checkbox', { name: /^Agent/ }).check()
  await page.getByRole('button', { name: 'Save roles' }).click()

  // Your own roles changed, so the session ends and the sign-in page says why.
  await expect(page.getByText('Your permissions changed. Please sign in again.')).toBeVisible()
  await signIn()
  // Back on the Users page it was on, now showing the role it gave itself alongside its own.
  await expect(page.getByRole('row', { name: new RegExp(ADMIN_EMAIL) }).getByRole('cell', { name: 'Administrator, Agent' }))
    .toBeVisible()
  await expect(page.getByRole('navigation').getByRole('link', { name: 'Tickets' })).toBeVisible()

  // And the API agrees, on a fresh session.
  const tenant = await signInAsTenantAdmin(TICKETS)
  const accepted = await tenant.api.post('/api/tickets/v1/tickets', {
    headers: { 'X-CSRF-Token': tenant.csrf },
    data: { title: 'After licensing and a role' },
  })
  expect(accepted.status(), await accepted.text()).toBe(200)
  await tenant.api.dispose()
})
