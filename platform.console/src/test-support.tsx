import { render } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import type { ReactNode } from 'react'
import { SessionProvider } from './components/session'

type Answer = { status?: number; body?: unknown }

/** An answer, or a promise of one for a test that holds a request open. */
export type Handler = (init?: RequestInit) => Answer | Promise<Answer>

export const operator = { email: 'op@platform.test' }

/**
 * Stands in for platform.api: each route answers from a handler, keyed "METHOD /path". The session
 * route answers as a signed-in operator unless a test replaces it. Anything unexpected is a 404,
 * so a page calling a route the test did not anticipate fails visibly.
 */
export function stubApi(handlers: Record<string, Handler>) {
  const routes: Record<string, Handler> = {
    'GET /api/platform/v1/auth/session': () => ({ body: operator }),
    ...handlers,
  }
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const handler = routes[`${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`]
    const answer = handler ? handler(init) : { status: 404, body: { problemCode: 'not_found' } }
    return Promise.resolve(answer).then(
      ({ status = 200, body = {} }) => ({ ok: status < 400, status, json: async () => body }) as Response,
    )
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

/** A request the test answers later, by calling `answer`. */
export function held() {
  let answer!: (value: Answer) => void
  const promise = new Promise<Answer>((resolve) => (answer = resolve))
  return { promise, answer }
}

/** The JSON bodies sent to one route, in order. */
export function sent(fetchMock: ReturnType<typeof stubApi>, method: string, path: string): unknown[] {
  return fetchMock.mock.calls
    .filter(([url, init]) => String(url) === path && init?.method === method)
    .map(([, init]) => JSON.parse(String(init!.body)))
}

/** Renders a page signed in, at a path, with its route pattern so params resolve. */
export function renderAt(path: string, pattern: string, page: ReactNode) {
  return render(
    <SessionProvider>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path={pattern} element={page} />
          <Route path="*" element={<p>Elsewhere</p>} />
        </Routes>
      </MemoryRouter>
    </SessionProvider>,
  )
}
