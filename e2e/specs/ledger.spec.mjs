import { expect, request, test } from '@playwright/test'
import { OPERATOR_EMAIL, OPERATOR_PASSWORD, seededTenantPublicId } from '../stack.mjs'

const ADMIN_EMAIL = 'admin@e2e.test'
const ADMIN_PASSWORD = 'correct horse battery'

/** Dates as the server counts them (UTC), since the close rule compares against the server's today. */
const today = new Date().toISOString().slice(0, 10)
const yesterday = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10)
const year = today.slice(0, 4)

/**
 * The ledger proof of concept, in a browser, against the real stack: an operator licenses it, then a
 * tenant admin builds a chart of accounts, posts, reads the trial balance, corrects by reversal, and
 * closes the books — and the closed period then refuses a post. Every rule shown here is also enforced
 * by the database; this proves the whole path from the screen to it.
 */
test.beforeAll(async () => {
  const operator = await request.newContext({ baseURL: 'http://localhost:5101' })
  const signedIn = await operator.post('/api/platform/v1/auth/sign-in', {
    data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD },
  })
  expect(signedIn.status(), await signedIn.text()).toBe(200)
  const csrf = (await operator.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value

  const granted = await operator.post(`/api/platform/v1/tenants/${seededTenantPublicId()}/entitlements`, {
    headers: { 'X-CSRF-Token': csrf },
    data: { app: 'ledger', licensed: true },
  })

  // 409 only if an earlier attempt of this same run already granted it.
  expect([200, 409], await granted.text()).toContain(granted.status())
  await operator.dispose()
})

test('the ledger, end to end in the browser', async ({ page }) => {
  await page.goto('/')
  await page.getByLabel('Email').fill(ADMIN_EMAIL)
  await page.getByLabel('Password').fill(ADMIN_PASSWORD)
  await page.getByRole('button', { name: 'Sign in' }).click()

  // Licensed, so it is in the nav — and its code is fetched only now.
  await page.getByRole('link', { name: 'Ledger' }).click()
  await expect(page.getByRole('heading', { name: 'Ledger', level: 1 })).toBeVisible()

  // A CHART OF ACCOUNTS.
  for (const [code, name, type] of [['1000', 'Cash', 'Asset'], ['4000', 'Sales', 'Revenue']]) {
    await page.getByLabel('Code').fill(code)
    await page.getByLabel('Name', { exact: true }).fill(name)
    await page.getByLabel('Type').selectOption(type)
    await page.getByRole('button', { name: 'Add account' }).click()
    await expect(page.getByRole('list', { name: 'Accounts' })).toContainText(`${code} — ${name}`)
  }

  // POST — balanced, so the button enables.
  const post = page.getByRole('form', { name: 'Post an entry' })
  await post.getByLabel('Memo').fill('First sale')
  await post.getByLabel('Line 1 account').selectOption({ label: '1000 — Cash' })
  await post.getByLabel('Line 1 debit').fill('125.00')
  await post.getByLabel('Line 2 account').selectOption({ label: '4000 — Sales' })
  await post.getByLabel('Line 2 credit').fill('125')
  await expect(post.getByRole('status')).toHaveText('Balanced.')
  await post.getByRole('button', { name: 'Post entry' }).click()

  // The gapless number an auditor would cite, and a trial balance whose columns agree.
  const first = page.getByRole('article', { name: `Entry ${year}-1` })
  await expect(first).toContainText('First sale')
  const usd = page.getByRole('table', { name: 'Trial balance in USD' })
  await expect(usd.getByRole('row', { name: /Total USD/ })).toHaveText(/125\.00\s*125\.00/)

  // CORRECT BY REVERSAL — the only correction there is. Both entries stay on the record.
  await first.getByRole('button', { name: 'Reverse' }).click()
  const reversal = page.getByRole('article', { name: `Entry ${year}-2` })
  await expect(reversal).toContainText(`Reverses ${year}-1.`)
  await expect(first).toContainText(`Reversed by ${year}-2.`)
  await expect(first.getByRole('button', { name: 'Reverse' })).toHaveCount(0)
  // The two net to nothing, so the trial balance has no lines left.
  await expect(page.getByText('No balances as of this date.')).toBeVisible()

  // CLOSE THE BOOKS through yesterday…
  await page.getByLabel('Close through').fill(yesterday)
  await page.getByRole('button', { name: 'Close the books' }).click()
  await expect(page.getByText(`closed through ${yesterday}`)).toBeVisible()

  // …and a post dated inside the closed period is refused, with what was typed kept.
  await post.getByLabel('Date').fill(yesterday)
  await post.getByLabel('Memo').fill('Late invoice')
  await post.getByLabel('Line 1 account').selectOption({ label: '1000 — Cash' })
  await post.getByLabel('Line 1 debit').fill('10')
  await post.getByLabel('Line 2 account').selectOption({ label: '4000 — Sales' })
  await post.getByLabel('Line 2 credit').fill('10')
  await post.getByRole('button', { name: 'Post entry' }).click()

  await expect(page.getByRole('alert')).toContainText('The books are closed for that date')
  await expect(post.getByLabel('Memo')).toHaveValue('Late invoice')
  await expect(page.getByRole('article', { name: `Entry ${year}-3` })).toHaveCount(0)

  // Wide content scrolls inside its own container, never widening the page (CLAUDE.md). The ledger
  // is the first app with tables, so it is the first page that could.
  await page.setViewportSize({ width: 390, height: 844 })
  const widths = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth, viewport: window.innerWidth,
  }))
  expect(widths.document).toBeLessThanOrEqual(widths.viewport)
})
