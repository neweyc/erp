import { api } from '@app-platform/web-api'

/** A role the user holds, for display. Never used to decide what to show — see permissions. */
export interface SessionRole {
  /** `core:admin` for a built-in role, `rol_…` for one of the organization's own. */
  readonly key: string
  readonly name: string
}

export interface Session {
  readonly userId: string
  readonly email: string
  /** Every role held; a user may hold several (docs/permissions.md). */
  readonly roles: readonly SessionRole[]
  /**
   * What this user may do, computed by the server from the same rule the APIs enforce. The shell
   * uses it to hide what the user cannot do; it never derives permissions from roles itself.
   */
  readonly permissions: readonly string[]
  readonly tenantName: string
  /** Apps the tenant has licensed. Empty is legitimate — a tenant may license none. */
  readonly licensedApps: readonly string[]
  /** True when the tenant is suspended: the principal is valid, most routes are not. */
  readonly tenantSuspended: boolean
}

export { api, ApiError } from '@app-platform/web-api'
export type { ApiProblem } from '@app-platform/web-api'

export function fetchSession(): Promise<Session> {
  return api<Session>('/api/core/v1/auth/session')
}
