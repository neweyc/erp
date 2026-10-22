import { expect, request, test } from '@playwright/test'
import { waitForInvitation } from '../stack.mjs'

/**
 * The roster, in the browser: an administrator adds someone to it and invites them in one step,
 * the person accepts the delivered link and signs in, and the administrator then terminates them,
 * which ends their live session at once. Everyone with an account belongs to an employee; the
 * administrator made at provisioning is the exception, and the Users page says so.
 */

const CORE = 'http://localhost:5100'
const ADMIN = { email: 'admin@e2e.test', password: 'correct horse battery' }

test('an administrator adds and invites someone from the roster, then terminates them', async ({ page }) => {
  // Unique per run, so a warm stack with an earlier run's person still starts clean.
  const stamp = Date.now()
  const email = `roster-${stamp}@e2e.test`
  const name = `Rita Roster${stamp}`
  const password = 'roster correct horse'

  await page.goto('/')
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()

  // ---- Added and invited in one step ----
  await page.getByRole('navigation').getByRole('link', { name: 'Employees' }).click()
  await expect(page.getByRole('heading', { name: 'Employees', level: 1 })).toBeVisible()

  await page.getByRole('button', { name: 'Add employee' }).click()
  const form = page.getByRole('form', { name: 'Add employee' })
  await form.getByLabel('First name').fill('Rita')
  await form.getByLabel('Last name').fill(`Roster${stamp}`)
  await form.getByLabel('Email', { exact: true }).fill(email)
  // "Send an invitation" is ticked, and Member chosen, by default.
  await expect(form.getByRole('checkbox', { name: /^Send an invitation/ })).toBeChecked()
  await expect(form.getByRole('checkbox', { name: /^Member/ })).toBeChecked()
  await form.getByRole('button', { name: 'Add employee' }).click()
  await expect(page.getByText(`${name} is on the roster, and an invitation is on its way.`)).toBeVisible()

  const row = page.getByRole('row', { name: new RegExp(name) })
  await expect(row.getByRole('cell', { name: /^Invited/ })).toBeVisible()

  // The page fits a phone without widening the document (CLAUDE.md).
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })

  // ---- The person accepts the DELIVERED link and signs in ----
  const token = await waitForInvitation(email)
  const person = await request.newContext()
  const accepted = await person.post(`${CORE}/api/core/v1/auth/accept-invite`, { data: { token, password } })
  expect(accepted.status(), await accepted.text()).toBe(200)
  expect((await person.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).toBe(200)
  expect((await person.get(`${CORE}/api/core/v1/auth/session`)).status()).toBe(200)

  // Two "Active" cells now: their employment, and their account.
  await page.reload()
  await expect(row.getByRole('cell', { name: 'Active', exact: true })).toHaveCount(2)

  // ---- Terminated: on record still, and signed out at once ----
  await row.getByRole('button', { name: `Terminate ${name}` }).click()
  await page.getByRole('button', { name: `Confirm: terminate ${name}` }).click()
  await expect(page.getByText(`${name} is terminated.`)).toBeVisible()
  await expect(page.getByRole('row', { name: new RegExp(name) })).toHaveCount(0)

  const after = await person.get(`${CORE}/api/core/v1/auth/session`)
  expect(after.status()).toBe(401)
  await person.dispose()
  const refused = await request.newContext()
  expect((await refused.post(`${CORE}/api/core/v1/auth/sign-in`, { data: { email, password } })).status()).not.toBe(200)
  await refused.dispose()

  await page.getByRole('checkbox', { name: 'Show terminated' }).check()
  await expect(row.getByRole('cell', { name: 'Terminated' })).toBeVisible()
  await expect(row.getByRole('cell', { name: 'Deactivated' })).toBeVisible()

  // ---- The provisioned administrator is the one account not on the roster ----
  await page.getByRole('navigation').getByRole('link', { name: 'Users' }).click()
  await expect(page.getByRole('row', { name: new RegExp(ADMIN.email) }).getByText('Not on the roster')).toBeVisible()
})
