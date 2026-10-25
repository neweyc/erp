import { expect, request, test } from '@playwright/test'
import { tenantOrigin, waitForInvitation } from '../stack.mjs'

/**
 * D17: an employee invited by an admin can accept and sign in.
 *
 * The journey's own invitation is the tenant admin's, issued at provisioning, so it never
 * exercised this path, and for a while an employee invitation carried no token and could never be
 * accepted. This drives the whole path through the running services: an admin invites, the outbox
 * worker DELIVERS, the invitee accepts in the browser with the link from the captured message,
 * and then signs in with the role they were given. It then goes on to the organisation's own roles:
 * the administrator defines one on the Roles page and gives it to the invitee on the Users page.
 */

const CORE = tenantOrigin(5100)
const ADMIN = { email: 'admin@e2e.test', password: 'correct horse battery' }

test('an invited employee accepts in the browser and signs in', async ({ page }) => {
  // Unique per run, so a warm stack with an earlier run's invitee still starts clean.
  const email = `invitee-${Date.now()}@e2e.test`
  const password = 'invitee correct horse'

  const admin = await request.newContext()
  const signedIn = await admin.post(`${CORE}/api/core/v1/auth/sign-in`, { data: ADMIN })
  expect(signedIn.status(), await signedIn.text()).toBe(200)
  const csrf = (await admin.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value

  const employee = await admin.post(`${CORE}/api/core/v1/employees`, {
    headers: { 'X-CSRF-Token': csrf },
    data: { firstName: 'Ivy', lastName: 'Invitee', email },
  })
  expect(employee.status(), await employee.text()).toBe(200)

  const invited = await admin.post(
    `${CORE}/api/core/v1/employees/${(await employee.json()).employeeId}/invite`, {
      headers: { 'X-CSRF-Token': csrf },
      data: { roles: ['core:member'], email },
    })
  expect(invited.status(), await invited.text()).toBe(200)
  await admin.dispose()

  // From the DELIVERED message, never the database: that would pass even if nothing were sent.
  const token = await waitForInvitation(email)

  await page.goto(`/accept-invite?token=${encodeURIComponent(token)}`)
  await expect(page.getByRole('heading', { name: 'Accept your invitation' })).toBeVisible()
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Accept invitation' }).click()
  await expect(page.getByRole('heading', { name: 'Invitation accepted' })).toBeVisible()

  // Signed in as the role the admin chose.
  const invitee = await request.newContext()
  const inviteeSignIn = await invitee.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })
  expect(inviteeSignIn.status(), await inviteeSignIn.text()).toBe(200)
  const session = await (await invitee.get(`${CORE}/api/core/v1/auth/session`)).json()
  expect({ email: session.email, roles: session.roles }).toEqual({ email, roles: [{ key: 'core:member', name: 'Member' }] })

  // A member cannot invite. An invitation can grant admin, so this would otherwise be a way for
  // the member to make themselves one: invite an address they control as admin, then accept.
  const inviteeCsrf = (await invitee.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  const ownEmployeeId = (await (await invitee.get(`${CORE}/api/core/v1/employees`)).json())
    .find((e) => e.email === email)?.employeeId
  expect(ownEmployeeId).toBeTruthy()
  const escalation = await invitee.post(`${CORE}/api/core/v1/employees/${ownEmployeeId}/invite`, {
    headers: { 'X-CSRF-Token': inviteeCsrf },
    data: { roles: ['core:admin'], email },
  })
  expect(escalation.status(), await escalation.text()).toBe(403)
  expect((await escalation.json()).problemCode).toBe('not_permitted')

  // D18: a member reads the roster but cannot add to it (core.employee.create is admin and manager).
  const created = await invitee.post(`${CORE}/api/core/v1/employees`, {
    headers: { 'X-CSRF-Token': inviteeCsrf },
    data: { firstName: 'Not', lastName: 'Allowed', email: `not-allowed-${Date.now()}@e2e.test` },
  })
  expect(created.status(), await created.text()).toBe(403)
  expect((await created.json()).problemCode).toBe('not_permitted')

  // Nor change roles, their own included: that is the escalation path the Users page opens.
  const selfPromotion = await invitee.patch(`${CORE}/api/core/v1/users/${session.userId}/roles`, {
    headers: { 'X-CSRF-Token': inviteeCsrf },
    data: { add: ['core:admin'] },
  })
  expect(selfPromotion.status(), await selfPromotion.text()).toBe(403)
  expect((await selfPromotion.json()).problemCode).toBe('not_permitted')

  // Nor define a role: core.role.manage is an administrator's.
  const selfMadeRole = await invitee.post(`${CORE}/api/core/v1/roles`, {
    headers: { 'X-CSRF-Token': inviteeCsrf },
    data: { name: 'Everything', permissions: ['core.user.manage'] },
  })
  expect(selfMadeRole.status(), await selfMadeRole.text()).toBe(403)
  expect((await selfMadeRole.json()).problemCode).toBe('not_permitted')
  await invitee.dispose()

  // ---- A role the organisation defines itself, in the browser ----
  // The administrator makes a "Roster keeper" role that may add employees, which a member may not,
  // and gives it to the invitee. The role comes from data, not code, so this is the path that
  // proves a custom role reaches the API that enforces it.
  const roleName = `Roster keeper ${Date.now()}`
  await page.goto('/')
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('navigation').getByRole('link', { name: 'Roles' }).click()
  await expect(page.getByRole('heading', { name: 'Roles', level: 1 })).toBeVisible()

  // A new page: wide content must scroll inside its container, never widen the document (CLAUDE.md).
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })

  await page.getByRole('button', { name: 'New role' }).click()
  await page.getByLabel('Name', { exact: true }).fill(roleName)
  await page.getByRole('checkbox', { name: 'See the employee roster' }).check()
  await page.getByRole('checkbox', { name: 'Add employees' }).check()
  await page.getByRole('button', { name: 'Save role' }).click()
  await expect(page.getByRole('article', { name: roleName })).toBeVisible()

  await page.getByRole('navigation').getByRole('link', { name: 'Users' }).click()
  await page.getByRole('button', { name: `Edit roles for ${email}` }).click()
  await page.getByRole('checkbox', { name: new RegExp(`^${roleName}`) }).check()
  await page.getByRole('button', { name: 'Save roles' }).click()
  await expect(page.getByRole('row', { name: new RegExp(email) }).getByRole('cell', { name: `Member, ${roleName}` }))
    .toBeVisible()

  // The invitee, signed in afresh, may now do what the custom role grants.
  const keeper = await request.newContext()
  expect((await keeper.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).toBe(200)
  const keeperCsrf = (await keeper.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  const keeperSession = await (await keeper.get(`${CORE}/api/core/v1/auth/session`)).json()
  expect(keeperSession.permissions).toContain('core.employee.create')
  const added = await keeper.post(`${CORE}/api/core/v1/employees`, {
    headers: { 'X-CSRF-Token': keeperCsrf },
    data: { firstName: 'Now', lastName: 'Allowed', email: `now-allowed-${Date.now()}@e2e.test` },
  })
  expect(added.status(), await added.text()).toBe(200)
  await keeper.dispose()

  // Single use: the same link cannot set a password a second time.
  const anonymous = await request.newContext()
  const replayed = await anonymous.post(`${CORE}/api/core/v1/auth/accept-invite`, {
    data: { token, password: 'someone else entirely' },
  })
  expect(replayed.status()).toBe(400)
  expect((await replayed.json()).problemCode).toBe('invalid_token')
  await anonymous.dispose()
})
