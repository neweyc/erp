import { expect, test } from '@playwright/test'

/**
 * The organization's structure, in the browser: an administrator builds a department inside another,
 * adds a job title and a location, places someone on the roster in all three, and then renames the
 * department, which shows on that person at once because they hold it by id. Each page fits a phone.
 */

const ADMIN = { email: 'admin@e2e.test', password: 'correct horse battery' }

/** The page fits a phone without widening the document (CLAUDE.md). */
async function fitsAPhone(page) {
  await page.setViewportSize({ width: 390, height: 800 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth === window.innerWidth)).toBe(true)
  await page.setViewportSize({ width: 1280, height: 800 })
}

test('an administrator builds the structure and places someone in it', async ({ page }) => {
  // Unique per run, so a warm stack with an earlier run's lists still starts clean.
  const stamp = Date.now()
  const division = `Engineering ${stamp}`
  const team = 'Platform'
  const title = `Engineer ${stamp}`
  const site = `Head office ${stamp}`
  const person = { first: 'Olive', last: `Org${stamp}` }

  await page.goto('/')
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  const nav = page.getByRole('navigation')

  // ---- A department, and one inside it ----
  await nav.getByRole('link', { name: 'Departments' }).click()
  await expect(page.getByRole('heading', { name: 'Departments', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Add department' }).click()
  await page.getByRole('form', { name: 'Add department' }).getByLabel('Name').fill(division)
  await page.getByRole('form', { name: 'Add department' }).getByRole('button', { name: 'Add department' }).click()
  await expect(page.getByText(`${division} is added.`)).toBeVisible()

  await page.getByRole('button', { name: `Add a department inside ${division}` }).click()
  await page.getByRole('form', { name: 'Add department' }).getByLabel('Name').fill(team)
  await page.getByRole('form', { name: 'Add department' }).getByRole('button', { name: 'Add department' }).click()
  await expect(page.getByRole('cell', { name: `${division} › ${team}`, exact: true })).toBeVisible()
  await fitsAPhone(page)

  // ---- A job title and a location ----
  await nav.getByRole('link', { name: 'Job titles' }).click()
  await page.getByRole('button', { name: 'Add job title' }).click()
  await page.getByRole('form', { name: 'Add job title' }).getByLabel('Name').fill(title)
  await page.getByRole('form', { name: 'Add job title' }).getByRole('button', { name: 'Add job title' }).click()
  await expect(page.getByText(`${title} is added.`)).toBeVisible()
  await fitsAPhone(page)

  await nav.getByRole('link', { name: 'Locations' }).click()
  await page.getByRole('button', { name: 'Add location' }).click()
  await page.getByRole('form', { name: 'Add location' }).getByLabel('Name').fill(site)
  await page.getByRole('form', { name: 'Add location' }).getByRole('button', { name: 'Add location' }).click()
  await expect(page.getByText(`${site} is added.`)).toBeVisible()

  // ---- Someone placed in all three ----
  await nav.getByRole('link', { name: 'Employees' }).click()
  await page.getByRole('button', { name: 'Add employee' }).click()
  const form = page.getByRole('form', { name: 'Add employee' })
  await form.getByLabel('First name').fill(person.first)
  await form.getByLabel('Last name').fill(person.last)
  await form.getByLabel('Department').selectOption({ label: `${division} › ${team}` })
  await form.getByLabel('Job title').selectOption({ label: title })
  await form.getByLabel('Location').selectOption({ label: site })
  await form.getByRole('button', { name: 'Add employee' }).click()

  const row = page.getByRole('row', { name: new RegExp(person.last) })
  await expect(row.getByRole('cell', { name: `${title} in ${division} › ${team} at ${site}` })).toBeVisible()
  await fitsAPhone(page)

  // ---- A rename shows on them at once ----
  await nav.getByRole('link', { name: 'Departments' }).click()
  await page.getByRole('button', { name: `Edit ${division} › ${team}` }).click()
  const edit = page.getByRole('form', { name: `Edit ${division} › ${team}` })
  await edit.getByLabel('Name').fill('Platform engineering')
  await edit.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText('Platform engineering is saved.')).toBeVisible()

  await nav.getByRole('link', { name: 'Employees' }).click()
  await expect(page.getByRole('row', { name: new RegExp(person.last) })
    .getByRole('cell', { name: `${title} in ${division} › Platform engineering at ${site}` })).toBeVisible()
})
