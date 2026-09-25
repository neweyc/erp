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
  /** Signs in and lands on the dashboard. */
  async function signIn(page) {
    await page.goto('/')
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('heading', { name: 'E2E Ltd' })).toBeVisible()
  }

  test('create, assign and close a ticket entirely through the UI', async ({ page }) => {
    await signIn(page)

    await page.getByRole('link', { name: 'Tickets' }).click()
    await expect(page.getByRole('heading', { name: 'Tickets', level: 1 })).toBeVisible()

    // CREATE — through the form, not a fetch. A fetch from the page proves the API works; it does
    // not prove a person can raise a ticket.
    const title = `Printer jammed ${Date.now()}`
    await page.getByLabel('New ticket').fill(title)
    await page.getByRole('button', { name: 'Raise ticket' }).click()

    const ticket = page.getByRole('listitem').filter({ hasText: title })
    await expect(ticket).toBeVisible()
    await expect(ticket).toContainText('Assigned to: Nobody')

    // ASSIGN — picking from the roster, which comes from CORE's published employee view. This is
    // the cross-service read the whole published-contract design exists for.
    await ticket.getByLabel('Assignee').selectOption({ label: 'Ada Lovelace' })
    await expect(ticket).toContainText('Assigned to: Ada Lovelace')

    // CLOSE.
    await ticket.getByRole('button', { name: 'Close ticket' }).click()

    // Gone from the default list, because the default is open tickets only.
    await expect(page.getByRole('listitem').filter({ hasText: title })).toHaveCount(0)

    // Still there, and still naming its assignee, once closed tickets are shown. That name is the
    // stored snapshot — the reason a closed ticket stays readable after an employee leaves.
    await page.getByLabel('Show closed tickets').check()
    const closed = page.getByRole('listitem').filter({ hasText: title })
    await expect(closed).toContainText('Closed')
    await expect(closed).toContainText('Assigned to: Ada Lovelace')
  })

  test('a closed ticket cannot be reassigned or closed again from the UI', async ({ page }) => {
    await signIn(page)
    await page.getByRole('link', { name: 'Tickets' }).click()

    const title = `Already handled ${Date.now()}`
    await page.getByLabel('New ticket').fill(title)
    await page.getByRole('button', { name: 'Raise ticket' }).click()

    const ticket = page.getByRole('listitem').filter({ hasText: title })
    await ticket.getByRole('button', { name: 'Close ticket' }).click()

    await page.getByLabel('Show closed tickets').check()
    const closed = page.getByRole('listitem').filter({ hasText: title })

    // Disabled rather than offered-and-refused: a control that exists and then errors invites the
    // click. The API refuses both reassignment and re-closing independently, so a caller that is
    // not this UI gets the same answer — see TicketTests.
    await expect(closed.getByLabel('Assignee')).toBeDisabled()
    await expect(closed.getByRole('button', { name: 'Close ticket' })).toHaveCount(0)
  })

  test('sign in and see the licensed app', async ({ page }) => {
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
    await expect(page.getByRole('link', { name: 'Tickets' })).toBeVisible()
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
