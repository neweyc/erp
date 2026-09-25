import { expect, test } from '@playwright/test'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { waitForInvitation } from '../stack.mjs'

/**
 * Selection logic for captured mail, against a fixture directory.
 *
 * Exists because a stale capture is invisible on a cold stack — the directory is cleared — but
 * poisons a warm one. An interrupted run skips globalTeardown, so both the container and the
 * captures survive into the next run, and readdir order then decides which token the journey
 * uses. The symptom is a failure that points at accept-invite rather than at the stale file.
 */
function capture(directory, { to, token, capturedAt }) {
  writeFileSync(
    join(directory, `${token}.json`),
    JSON.stringify({ messageId: token, to, capturedAt, payload: { kind: 'invite', token } }),
  )
}

test('the newest capture wins when an older one survives for the same address', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'capture-'))
  const floor = new Date('2026-01-01T00:00:00Z')

  capture(directory, { to: 'a@b.test', token: 'older', capturedAt: '2026-01-02T00:00:00Z' })
  capture(directory, { to: 'a@b.test', token: 'newer', capturedAt: '2026-01-03T00:00:00Z' })

  expect(await waitForInvitation('a@b.test', { notBefore: floor, directory })).toBe('newer')
})

test('a capture older than the run is refused rather than used', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'capture-'))

  capture(directory, { to: 'a@b.test', token: 'stale', capturedAt: '2020-01-01T00:00:00Z' })

  // Timing out with an actionable message is the correct outcome: using the stale token would
  // fail later at accept-invite, blaming the page instead of the leftover file.
  await expect(
    waitForInvitation('a@b.test', { notBefore: new Date('2026-01-01T00:00:00Z'), timeoutMs: 1000, directory }),
  ).rejects.toThrow(/No invitation delivered/)
})

test('a capture for a different address is ignored', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'capture-'))

  capture(directory, { to: 'someone-else@b.test', token: 'wrong', capturedAt: '2026-01-03T00:00:00Z' })

  await expect(
    waitForInvitation('a@b.test', { notBefore: new Date('2026-01-01T00:00:00Z'), timeoutMs: 1000, directory }),
  ).rejects.toThrow(/No invitation delivered/)
})

test('a capture caught mid-write is skipped rather than aborting the wait', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'capture-'))

  // The transport writes non-atomically, so a poll can read a truncated file. Aborting here would
  // replace the actionable timeout message with a SyntaxError.
  writeFileSync(join(directory, 'truncated.json'), '{"to":"a@b.test","payload":{"tok')
  capture(directory, { to: 'a@b.test', token: 'good', capturedAt: '2026-01-03T00:00:00Z' })

  expect(
    await waitForInvitation('a@b.test', { notBefore: new Date('2026-01-01T00:00:00Z'), directory }),
  ).toBe('good')
})
