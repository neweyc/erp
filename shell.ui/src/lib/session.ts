export type Role = 'admin' | 'manager' | 'member'

export interface Session {
  readonly userId: string
  readonly email: string
  readonly role: Role
  readonly tenantName: string
  /** Apps the tenant has licensed. Empty is legitimate — a tenant may license none. */
  readonly licensedApps: readonly string[]
  /** True when the tenant is suspended: the principal is valid, most routes are not. */
  readonly tenantSuspended: boolean
}

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

/** Read from the cookie by the server; mirrored into a header on every unsafe request. */
const CSRF_COOKIE = 'ap_csrf'
const CSRF_HEADER = 'X-CSRF-Token'

function csrfToken(): string | undefined {
  return document.cookie
    .split('; ')
    .find((c) => c.startsWith(`${CSRF_COOKIE}=`))
    ?.slice(CSRF_COOKIE.length + 1)
}

/**
 * Every API call goes through here, for two reasons that are easy to get wrong per-call:
 * credentials must be included so the session cookie is sent, and unsafe methods must carry
 * the CSRF token. A fetch written by hand elsewhere will eventually omit one of them.
 */
export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = init.method ?? 'GET'
  const unsafe = !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method.toUpperCase())
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
    // The server sends a stable problemCode; branch on that, never on the message, which is
    // prose and will be improved.
    const problem = (await response.json().catch(() => ({}))) as Partial<ApiProblem>
    throw new ApiError(response.status, problem.problemCode ?? 'unknown', problem.message)
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

export function fetchSession(): Promise<Session> {
  return api<Session>('/api/core/v1/auth/session')
}
