import { expect, request, test } from '@playwright/test'
import { tenantOrigin, waitForInvitation } from '../stack.mjs'

/**
 * An account's life after the invitation, through the running services and the Users page:
 * an administrator sees that the invitation was delivered, resends it, the person accepts with the
 * NEW link (and the old one can no longer reset their password), and then the administrator
 * deactivates them, which ends their live session at once and stops them signing in, and
 * reactivates them, after which they sign in again but a copy of the old cookie stays dead.
 */

const CORE = tenantOrigin(5100)
const ADMIN = { email: 'admin@e2e.test', password: 'correct horse battery' }

test('an administrator resends an invitation, then deactivates and reactivates the account', async ({ page }) => {
  // Unique per run, so a warm stack with an earlier run's person still starts clean.
  const email = `lifecycle-${Date.now()}@e2e.test`
  const password = 'lifecycle correct horse'

  const admin = await request.newContext()
  expect((await admin.post(`${CORE}/api/core/v1/auth/sign-in`, { data: ADMIN })).status()).toBe(200)
  const csrf = (await admin.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  const employee = await admin.post(`${CORE}/api/core/v1/employees`, {
    headers: { 'X-CSRF-Token': csrf },
    data: { firstName: 'Lee', lastName: 'Lifecycle', email },
  })
  expect(employee.status(), await employee.text()).toBe(200)
  const invited = await admin.post(`${CORE}/api/core/v1/employees/${(await employee.json()).employeeId}/invite`, {
    headers: { 'X-CSRF-Token': csrf },
    data: { roles: ['core:member'], email },
  })
  expect(invited.status(), await invited.text()).toBe(200)
  const firstLink = await waitForInvitation(email)

  // The worker marks the message delivered just after writing it; wait for that, not for a guess.
  await expect.poll(async () => {
    const users = await (await admin.get(`${CORE}/api/core/v1/users`)).json()
    return users.find((u) => u.email === email)?.invitation?.delivery
  }).toBe('delivered')
  await admin.dispose()

  // ---- The administrator sees the delivery and resends, in the browser ----
  await page.goto('/')
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('navigation').getByRole('link', { name: 'Users' }).click()
  const row = page.getByRole('row', { name: new RegExp(email) })
  await expect(row.getByRole('cell', { name: /^Invited: email sent, link valid until/ })).toBeVisible()

  // The page gained buttons; it must still fit a phone without widening the document (CLAUDE.md).
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })

  const beforeResend = new Date()
  await page.getByRole('button', { name: `Resend invitation to ${email}` }).click()
  await expect(page.getByText(`A new invitation is on its way to ${email}.`)).toBeVisible()
  const secondLink = await waitForInvitation(email, { notBefore: beforeResend })
  expect(secondLink).not.toBe(firstLink)

  // The person accepts with the new link. The old one, still unexpired, then cannot set a
  // password on what is now an active account.
  const anonymous = await request.newContext()
  const accepted = await anonymous.post(`${CORE}/api/core/v1/auth/accept-invite`, { data: { token: secondLink, password } })
  expect(accepted.status(), await accepted.text()).toBe(200)
  const spare = await anonymous.post(`${CORE}/api/core/v1/auth/accept-invite`, {
    data: { token: firstLink, password: 'someone else entirely' },
  })
  expect(spare.status()).toBe(400)
  expect((await spare.json()).problemCode).toBe('invalid_token')
  await anonymous.dispose()

  const person = await request.newContext()
  expect((await person.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).toBe(200)
  expect((await person.get(`${CORE}/api/core/v1/auth/session`)).status()).toBe(200)
  // The cookie as it stands now, before deactivation: replayed after reactivation, it must still be
  // refused. (The live context cannot show that: its first 401 deletes the cookie it holds.)
  const copiedCookie = await person.storageState()

  // ---- Deactivation ends the live session at once ----
  await page.reload()
  await page.getByRole('button', { name: `Deactivate ${email}` }).click()
  await page.getByRole('button', { name: `Confirm: deactivate ${email}` }).click()
  await expect(page.getByText(`${email} is deactivated and signed out.`)).toBeVisible()
  await expect(row.getByRole('cell', { name: 'Deactivated' })).toBeVisible()

  expect((await person.get(`${CORE}/api/core/v1/auth/session`)).status()).toBe(401)
  const refused = await request.newContext()
  expect((await refused.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).not.toBe(200)
  await refused.dispose()

  // ---- Reactivation lets them sign in again; the old session stays dead ----
  await page.getByRole('button', { name: `Reactivate ${email}` }).click()
  await expect(row.getByRole('cell', { name: 'Active' })).toBeVisible()

  const replayed = await request.newContext({ storageState: copiedCookie })
  const replayedSession = await replayed.get(`${CORE}/api/core/v1/auth/session`)
  expect(replayedSession.status()).toBe(401)
  expect((await replayedSession.json()).problemCode).toBe('session_revoked')
  await replayed.dispose()
  const again = await request.newContext()
  expect((await again.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).toBe(200)
  expect((await again.get(`${CORE}/api/core/v1/auth/session`)).status()).toBe(200)
  await again.dispose()
  await person.dispose()
})
