import { expect, request, test } from '@playwright/test'
import {
  currentStep, OPERATOR_EMAIL, OPERATOR_PASSWORD, operatorSecret, PLATFORM, totpCode,
} from '../stack.mjs'

/**
 * M2 item 16: an operator cannot sign in without a current authenticator code, against the
 * running platform.api.
 *
 * Runs after `license`, which leaves a saved operator session that every other spec reuses. A code
 * is accepted once, so a spec that signed in fresh here could otherwise use up the code another
 * spec was about to use.
 */

async function signIn(code) {
  const api = await request.newContext({ baseURL: PLATFORM })
  const response = await api.post('/api/platform/v1/auth/sign-in', {
    data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD, code },
  })
  const result = { status: response.status(), body: await response.text() }
  await api.dispose()
  return result
}

test('the suite computes authenticator codes as RFC 6238 does', () => {
  // Appendix B's SHA-1 secret at T=59s. Everything below trusts this function.
  expect(totpCode('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', Math.floor(59 / 30))).toBe('287082')
})

test('the right password without a code is refused, and asks for one', async () => {
  const refused = await signIn(undefined)

  expect(refused.status).toBe(403)
  expect(JSON.parse(refused.body).problemCode).toBe('mfa_required')
})

test('the right password with a wrong code is refused', async () => {
  // The current code with its first digit changed is always wrong.
  const right = totpCode(operatorSecret(), currentStep())
  const wrong = `${(Number(right[0]) + 1) % 10}${right.slice(1)}`

  const refused = await signIn(wrong)

  expect(refused.status).toBe(403)
  expect(JSON.parse(refused.body).problemCode).toBe('mfa_code_invalid')
})

test('a code that has signed in once cannot sign in again', async () => {
  // The current step's code may already have been used this run, so take the first that works,
  // exactly as the shared sign-in helper does.
  const step = currentStep()
  let used
  for (const candidate of [step, step + 1]) {
    const code = totpCode(operatorSecret(), candidate)
    if ((await signIn(code)).status === 200) {
      used = code
      break
    }
  }
  expect(used, 'neither the current nor the next code signed in').toBeTruthy()

  const replayed = await signIn(used)

  expect(replayed.status).toBe(403)
  expect(JSON.parse(replayed.body).problemCode).toBe('mfa_code_invalid')
})
