import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from './session'

function respond(status: number, body: unknown) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response)
}

/**
 * Typed with fetch's real signature. Without the parameters, vi.fn infers an empty tuple and
 * every `mock.calls[0][1]` is a type error — which `tsc -b` catches and a bare `vitest run`
 * does not, because it never type-checks.
 */
function stubFetch(status = 200, body: unknown = {}) {
  const mock = vi.fn((_input: RequestInfo | URL, _init?: RequestInit) => respond(status, body))
  vi.stubGlobal('fetch', mock)
  return mock
}

function initOf(mock: ReturnType<typeof stubFetch>): RequestInit {
  return mock.mock.calls[0]![1] ?? {}
}

function headersOf(mock: ReturnType<typeof stubFetch>): Record<string, string> {
  return (initOf(mock).headers ?? {}) as Record<string, string>
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'ap_csrf=; expires=Thu, 01 Jan 1970 00:00:00 GMT'
})

describe('api client', () => {
  it('sends credentials so the session cookie travels', async () => {
    const fetchMock = stubFetch(200, { ok: true })

    await api('/api/core/v1/employees')

    expect(initOf(fetchMock)).toMatchObject({ credentials: 'include' })
  })

  it('attaches the csrf token to an unsafe method', async () => {
    document.cookie = 'ap_csrf=tok-123'
    const fetchMock = stubFetch()

    await api('/api/core/v1/employees', { method: 'POST', body: '{}' })

    expect(headersOf(fetchMock)['X-CSRF-Token']).toBe('tok-123')
  })

  it.each(['GET', 'HEAD', 'OPTIONS'])('sends no csrf token on %s', async (method) => {
    document.cookie = 'ap_csrf=tok-123'
    const fetchMock = stubFetch()

    await api('/api/core/v1/employees', { method })

    expect(headersOf(fetchMock)['X-CSRF-Token']).toBeUndefined()
  })

  it('surfaces the problem code rather than the message', async () => {
    vi.stubGlobal('fetch', () => respond(403, { problemCode: 'tenant_suspended', message: 'nope' }))

    // Callers branch on the code; the message is prose and will be reworded.
    await expect(api('/x')).rejects.toMatchObject({ problemCode: 'tenant_suspended', status: 403 })
  })

  it('still throws when the error body is not json', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve({ ok: false, status: 502, json: async () => { throw new Error('html') } } as unknown as Response),
    )

    // A proxy returning an HTML error page must not become an unhandled parse error that hides
    // the actual status from the caller.
    await expect(api('/x')).rejects.toBeInstanceOf(ApiError)
  })

  it('returns undefined for 204 rather than failing to parse an empty body', async () => {
    vi.stubGlobal('fetch', () => respond(204, undefined))

    await expect(api('/x')).resolves.toBeUndefined()
  })
})
