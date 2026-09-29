import { expect, request, test } from '@playwright/test'

/**
 * M2 item 18: anonymous endpoints are rate limited per client address, in the running service.
 *
 * packages/auth.tests proves the limiter's rules in an in-process host. What only this proves is
 * that a deployed core.api actually has it switched on: that Program.cs registers it and places
 * it in the pipeline where it sees the sign-in endpoint.
 *
 * It uses the tenant-B sign-in core (5103), configured with a real limit of 10 a minute. The
 * other servers in this suite have a limit high enough never to bite, because every request here
 * comes from one address. The isolation spec may already have used a few of this minute's
 * permits (at most three), so the test sends more than enough and asserts that refusal ARRIVES
 * after ordinary failures, rather than counting to an exact attempt. Requiring the ordinary
 * failures first is what stops a server that refuses EVERY sign-in from passing.
 */

const LIMITED_CORE = 'http://localhost:5103'
const LIMIT = 10

test('repeated failed sign-ins from one address are refused with 429 rate_limited', async () => {
  const api = await request.newContext()
  const statuses = []
  const ordinaryFailures = []
  let refused

  for (let attempt = 0; attempt < LIMIT + 5 && !refused; attempt++) {
    const response = await api.post(`${LIMITED_CORE}/api/core/v1/auth/sign-in`, {
      data: { email: 'nobody@other.test', password: `guess ${attempt}` },
    })
    statuses.push(response.status())
    if (response.status() === 403) ordinaryFailures.push((await response.json()).problemCode)
    if (response.status() === 429) {
      refused = {
        retryAfter: Number(response.headers()['retry-after']),
        problemCode: (await response.json()).problemCode,
      }
    }
  }

  // Before the refusal, several guesses were answered as ordinary failed sign-ins — not errors,
  // and not refused from the first request.
  expect(ordinaryFailures.length, `statuses: ${statuses}`).toBeGreaterThanOrEqual(LIMIT - 3)
  expect(ordinaryFailures.every((code) => code === 'invalid_credentials'), `${ordinaryFailures}`).toBe(true)
  expect(statuses.slice(0, -1).every((s) => s === 403), `statuses: ${statuses}`).toBe(true)
  expect(refused, `no 429 within ${LIMIT + 5} attempts; statuses: ${statuses}`).toBeTruthy()
  expect(refused.problemCode).toBe('rate_limited')
  expect(refused.retryAfter).toBeGreaterThan(0)

  await api.dispose()
})
