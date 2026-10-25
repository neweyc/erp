import { expect, test } from '@playwright/test'
import { currentStep, OPERATOR_EMAIL, OPERATOR_PASSWORD, operatorSecret, totpCode } from '../stack.mjs'

/**
 * The operator console, in the browser, against the running platform.api and core.api: sign in
 * with an authenticator code, create a tenant (core provisions it), license an app, suspend it by
 * typing its name, resume it, and find all of that in the audit log.
 *
 * Runs after operator-mfa, the only other spec that signs in with fresh codes.
 */

const CONSOLE = 'http://localhost:5274'

// Signing in may have to wait for the next authenticator code.
test.setTimeout(120_000)

/**
 * A code is accepted once, and the platform takes the current step's code or the next one. Both
 * may already be used by an earlier sign-in, so after trying them this waits for a new step.
 */
async function signIn(page) {
  await page.goto(CONSOLE)
  await expect(page.getByRole('heading', { name: 'Operator sign-in' })).toBeVisible()
  await page.getByLabel('Email').fill(OPERATOR_EMAIL)
  await page.getByLabel('Password').fill(OPERATOR_PASSWORD)

  for (let round = 0; round < 3; round++) {
    const step = currentStep()
    for (const candidate of [step, step + 1]) {
      await page.getByLabel('Authenticator code').fill(totpCode(operatorSecret(), candidate))
      await page.getByRole('button', { name: 'Sign in' }).click()
      // The click clears any earlier refusal, so whichever appears next is this attempt's answer.
      const tenants = page.getByRole('heading', { name: 'Tenants' })
      const refused = page.getByRole('alert')
      await expect(tenants.or(refused)).toBeVisible()
      if (await tenants.isVisible()) return
      await expect(refused).toContainText('already been used')
    }
    await page.waitForTimeout(Math.max(0, (step + 1) * 30_000 - Date.now() + 1_000))
  }
  throw new Error('no authenticator code signed in within three steps')
}

test('an operator creates, licenses, suspends and resumes a tenant from the console', async ({ page }) => {
  const name = `Console Ltd ${Date.now()}`

  await signIn(page)
  await expect(page.getByText(OPERATOR_EMAIL)).toBeVisible()

  // ---- Create: platform asks core, which provisions the tenant and invites its admin ----
  await page.getByRole('button', { name: 'New tenant' }).click()
  const create = page.getByRole('dialog', { name: 'New tenant' })
  await create.getByLabel('Organization name').fill(name)
  await create.getByLabel("Administrator's email").fill(`admin+${Date.now()}@console.test`)
  await create.getByRole('button', { name: 'Create tenant' }).click()

  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible()
  await expect(page.getByText(/^Read at /)).toBeVisible()
  const tenantId = await page.getByText(/^ten_/).textContent()

  // ---- License an app ----
  await page.getByRole('button', { name: 'License Tickets' }).click()
  await expect(page.getByRole('button', { name: 'Revoke Tickets' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'License history' })).toContainText('Tickets')

  // ---- Suspend: refused until the tenant's name is typed ----
  await page.getByRole('button', { name: 'Suspend' }).click()
  const suspend = page.getByRole('dialog', { name: `Suspend ${name}?` })
  await expect(suspend).toContainText('stopped from using the platform at once')
  await expect(suspend.getByRole('button', { name: 'Suspend' })).toBeDisabled()
  await suspend.getByLabel('Reason (optional)').fill('e2e check')
  await suspend.getByLabel(`Type ${name} to confirm`).fill(name)
  await suspend.getByRole('button', { name: 'Suspend' }).click()
  await expect(page.getByRole('button', { name: 'Resume' })).toBeVisible()

  // ---- Resume ----
  await page.getByRole('button', { name: 'Resume' }).click()
  await page.getByRole('dialog', { name: `Resume ${name}?` }).getByRole('button', { name: 'Resume' }).click()
  await expect(page.getByRole('button', { name: 'Suspend' })).toBeVisible()

  // ---- The tenant's own trail, newest first ----
  const actions = page.getByRole('table', { name: 'Operator actions' })
  await expect(actions).toContainText('Created the tenant')
  await expect(actions).toContainText('Licensed an app')
  await expect(actions).toContainText('Suspended the tenant')
  await expect(actions).toContainText('Resumed the tenant')

  // ---- The list, at phone width without sideways scrolling ----
  await page.getByRole('link', { name: 'All tenants' }).click()
  const row = page.getByRole('row', { name: new RegExp(name) })
  await expect(row).toContainText('Active')
  await expect(row).toContainText('Tickets')
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })

  // ---- The audit log names the operator ----
  await page.getByRole('link', { name: 'Audit log' }).click()
  const created = page.getByRole('row', { name: new RegExp(`Created the tenant.*${tenantId}`) })
  await expect(created).toContainText(OPERATOR_EMAIL)

  // ---- Signing out ends the session on the server, not only in this browser ----
  const cookies = (await page.context().cookies(CONSOLE)).map((c) => `${c.name}=${c.value}`).join('; ')
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByText('You have signed out.')).toBeVisible()

  const copied = await page.request.get(`${CONSOLE}/api/platform/v1/auth/session`, { headers: { Cookie: cookies } })
  expect(copied.status()).toBe(401)
  expect((await copied.json()).problemCode).toBe('session_revoked')
})
