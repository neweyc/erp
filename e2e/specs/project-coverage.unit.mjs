import { expect, test } from '@playwright/test'
import { readdirSync } from 'node:fs'
import { join } from 'node:path'
import config from '../playwright.config.mjs'

/**
 * Every spec file must be claimed by exactly one project.
 *
 * The projects list matches files by NAME rather than by a single catch-all pattern, because the
 * journey's steps depend on each other and the ordering has to be explicit. The cost is that a new
 * spec matching no project is never executed — and Playwright reports success, because from its
 * point of view there was nothing to run.
 *
 * This is the guard for that. Without it, adding a spec and forgetting the project entry looks
 * exactly like a passing suite.
 */
test('every spec file is claimed by exactly one project', () => {
  const specDirectory = join(import.meta.dirname)

  const specs = readdirSync(specDirectory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith('.mjs'))
    .map((entry) => entry.name)

  expect(specs.length, 'no spec files found — the directory scan is wrong').toBeGreaterThan(0)

  const unclaimed = []
  const contested = []

  for (const spec of specs) {
    const claiming = config.projects
      .filter((project) => project.testMatch.test(spec))
      .map((project) => project.name)

    if (claiming.length === 0) unclaimed.push(spec)
    if (claiming.length > 1) contested.push(`${spec} -> ${claiming.join(', ')}`)
  }

  expect(unclaimed, 'these specs match no project and would never run').toEqual([])
  // Two projects claiming one file would run it twice, which for the ordered setup steps means
  // granting or accepting twice and failing the second time for a confusing reason.
  expect(contested, 'these specs are claimed by more than one project').toEqual([])
})
