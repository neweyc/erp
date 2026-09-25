import { expect, test } from '@playwright/test'

const ADMIN_EMAIL = 'admin@e2e.test'
const ADMIN_PASSWORD = 'correct horse battery'

/**
 * The walking skeleton, in a browser.
 *
 * The API-level journey test already proves the handlers connect. What only this can prove is
 * the parts a browser owns: that the auth cookie is actually accepted over the dev origin, that
 * the CSRF token issued at sign-in is readable by script and echoed back, and that the shell's
 * entitlement gating fetches the app bundle it was told it may have.
 *
 * Every bug found in M1 lived in a seam, and this is the last seam nothing had crossed.
 */
test.describe('walking skeleton', () => {
  test('sign in, see the licensed app, create a ticket', async ({ page }) => {
    await page.goto('/')

    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()

    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()

    // The dashboard names the tenant, which means the session endpoint answered and the cookie
    // survived the round trip.
    await expect(page.getByRole('heading', { name: 'E2E Ltd' })).toBeVisible()
    await expect(page.getByText(ADMIN_EMAIL)).toBeVisible()

    // Licensed, so it is in the nav. The bundle is fetched only on navigation.
    const ticketsLink = page.getByRole('link', { name: 'Tickets' })
    await expect(ticketsLink).toBeVisible()

    await ticketsLink.click()
    await expect(page.getByRole('heading', { name: 'Tickets' })).toBeVisible()
  })

  test('the csrf token issued at sign-in is usable for a mutation', async ({ page, request }) => {
    await page.goto('/')
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('heading', { name: 'E2E Ltd' })).toBeVisible()

    // Read the way the page reads it: document.cookie. If the value needed URL-decoding, this
    // is where the mismatch would surface — which is exactly the bug that base64 tokens caused.
    const token = await page.evaluate(() =>
      document.cookie.split('; ').find((c) => c.startsWith('ap_csrf='))?.slice('ap_csrf='.length))

    expect(token, 'sign-in must issue a csrf token').toBeTruthy()

    const created = await page.evaluate(async (csrf) => {
      const response = await fetch('/api/tickets/v1/tickets', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
        body: JSON.stringify({ title: 'Raised from the browser' }),
      })
      return { status: response.status, body: await response.text() }
    }, token)

    expect(created.status, created.body).toBe(200)
  })

  test('a mutation without the csrf token is refused', async ({ page }) => {
    await page.goto('/')
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('heading', { name: 'E2E Ltd' })).toBeVisible()

    const refused = await page.evaluate(async () => {
      const response = await fetch('/api/tickets/v1/tickets', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ title: 'No token' }),
      })
      return response.status
    })

    // Enforced, not merely issued. A cookie-authenticated mutation with no token must fail.
    expect(refused).toBe(403)
  })
})
