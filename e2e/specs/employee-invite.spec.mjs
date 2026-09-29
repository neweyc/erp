import { expect, request, test } from '@playwright/test'
import { waitForInvitation } from '../stack.mjs'

/**
 * D17: an employee invited by an admin can accept and sign in.
 *
 * The journey's own invitation is the tenant admin's, issued at provisioning, so it never
 * exercised this path, and for a while an employee invitation carried no token and could never be
 * accepted. This drives the whole path through the running services: an admin invites, the outbox
 * worker DELIVERS, the invitee accepts in the browser with the link from the captured message,
 * and then signs in with the role they were given.
 */

const CORE = 'http://localhost:5100'
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
      data: { role: 'member' },
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
  expect({ email: session.email, role: session.role }).toEqual({ email, role: 'member' })

  // A member cannot invite. An invitation can grant admin, so this would otherwise be a way for
  // the member to make themselves one: invite an address they control as admin, then accept.
  const inviteeCsrf = (await invitee.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  const ownEmployeeId = (await (await invitee.get(`${CORE}/api/core/v1/employees`)).json())
    .find((e) => e.email === email)?.employeeId
  expect(ownEmployeeId).toBeTruthy()
  const escalation = await invitee.post(`${CORE}/api/core/v1/employees/${ownEmployeeId}/invite`, {
    headers: { 'X-CSRF-Token': inviteeCsrf },
    data: { role: 'admin' },
  })
  expect(escalation.status(), await escalation.text()).toBe(403)
  expect((await escalation.json()).problemCode).toBe('not_permitted')
  await invitee.dispose()

  // Single use: the same link cannot set a password a second time.
  const anonymous = await request.newContext()
  const replayed = await anonymous.post(`${CORE}/api/core/v1/auth/accept-invite`, {
    data: { token, password: 'someone else entirely' },
  })
  expect(replayed.status()).toBe(400)
  expect((await replayed.json()).problemCode).toBe('invalid_token')
  await anonymous.dispose()
})
