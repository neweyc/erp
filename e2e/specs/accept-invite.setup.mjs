import { expect, test } from '@playwright/test'
import { SHELL_PORT, tenantOrigin, waitForInvitation } from '../stack.mjs'

const ADMIN_EMAIL = 'admin@e2e.test'
const ADMIN_PASSWORD = 'correct horse battery'

/**
 * The journey's first two steps: the invitation is DELIVERED, and accepted in the browser.
 *
 * The token comes from the captured message rather than the database. That distinction is the
 * whole point — invitations are stored hashed, so a test that read the database would pass even
 * if the outbox worker never ran and no invitation was ever sent.
 */
test('the invitation is delivered and accepted', async ({ page }) => {
  // The link exactly as the email carries it: on the tenant's own address, built by core.
  const link = await waitForInvitation(ADMIN_EMAIL, { part: 'acceptUrl' })
  const expected = `${tenantOrigin(SHELL_PORT)}/accept-invite?token=`
  expect(link.startsWith(expected), `${link} should start with ${expected}`).toBe(true)

  await page.goto(link)
  await expect(page.getByRole('heading', { name: 'Accept your invitation' })).toBeVisible()

  await page.getByLabel('Password').fill(ADMIN_PASSWORD)
  await page.getByRole('button', { name: 'Accept invitation' }).click()

  await expect(page.getByRole('heading', { name: 'Invitation accepted' })).toBeVisible()
  // Already at the tenant's address, so the shell signs in right here.
  await expect(page.getByRole('link', { name: 'Go to sign in' })).toHaveAttribute('href', '/')
})
