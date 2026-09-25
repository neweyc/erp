/**
 * The browser-side API client, shared by every UI.
 *
 * Shared rather than copied because two of the things it does are security rules: sending
 * credentials so the session cookie travels, and attaching the CSRF token to unsafe methods. A
 * second copy would eventually diverge, and the copy that diverges is the one that stops sending
 * the token.
 */

export const CSRF_COOKIE = 'ap_csrf'
export const CSRF_HEADER = 'X-CSRF-Token'

/** Methods that change nothing, and so need no CSRF token. Everything else does. */
const SAFE_METHODS = ['GET', 'HEAD', 'OPTIONS', 'TRACE']

export interface ApiProblem {
  readonly problemCode: string
  readonly message?: string
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problemCode: string,
    message?: string,
  ) {
    super(message ?? problemCode)
  }
}

/** Issued by the server alongside the session cookie, and readable by script on purpose. */
export function csrfToken(): string | undefined {
  return document.cookie
    .split('; ')
    .find((c) => c.startsWith(`${CSRF_COOKIE}=`))
    ?.slice(CSRF_COOKIE.length + 1)
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase()
  const unsafe = !SAFE_METHODS.includes(method)
  const token = csrfToken()

  const response = await fetch(path, {
    ...init,
    method,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(unsafe && token ? { [CSRF_HEADER]: token } : {}),
      ...init.headers,
    },
  })

  if (!response.ok) {
    // The server sends a stable problemCode; callers branch on that, never on the message, which
    // is prose and will be reworded.
    const problem = (await response.json().catch(() => ({}))) as Partial<ApiProblem>
    throw new ApiError(response.status, problem.problemCode ?? 'unknown', problem.message)
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}
