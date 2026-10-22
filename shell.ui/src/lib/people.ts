import type { BadgeTone } from '@app-platform/ui-kit'

/** One row of GET /api/core/v1/users. */
export interface UserRow {
  readonly userId: string
  readonly email: string
  readonly status: string
  /** Role keys: built-in (`ledger:controller`) or custom (`rol_…`). */
  readonly roles: readonly string[]
  /** For an account not yet accepted: its newest invitation. Null otherwise. */
  readonly invitation: Invitation | null
  /** The employee the account belongs to; null for one not on the roster. */
  readonly employeeId: string | null
}

export interface Invitation {
  readonly delivery: 'pending' | 'delivered' | 'failed' | 'unknown'
  readonly expiresAt: string
  readonly expired: boolean
}

/** One row of GET /api/core/v1/employees. */
export interface EmployeeRow {
  readonly employeeId: string
  readonly firstName: string
  readonly lastName: string
  readonly displayName: string
  readonly email: string | null
  readonly status: 'Active' | 'OnLeave' | 'Terminated'
}

/**
 * An account's status as a badge, plus, for an invitation, what an administrator needs to decide
 * whether to resend it. Together they make one sentence ("Invited: link expired"), which is what a
 * screen reader hears and what tests find it by.
 */
export function describeAccount(user: UserRow): { label: string; tone: BadgeTone; detail: string | null } {
  if (user.status === 'Active') return { label: 'Active', tone: 'success', detail: null }
  if (user.status === 'Deactivated') return { label: 'Deactivated', tone: 'neutral', detail: null }
  if (user.status !== 'Invited' || !user.invitation) return { label: user.status, tone: 'info', detail: null }

  const { delivery, expiresAt, expired } = user.invitation
  if (expired) return { label: 'Invited', tone: 'warning', detail: 'link expired' }
  if (delivery === 'failed') return { label: 'Invited', tone: 'danger', detail: 'email could not be delivered' }
  const until = new Date(expiresAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
  if (delivery === 'pending') return { label: 'Invited', tone: 'info', detail: `email sending, link valid until ${until}` }
  if (delivery === 'delivered') return { label: 'Invited', tone: 'info', detail: `email sent, link valid until ${until}` }
  return { label: 'Invited', tone: 'info', detail: `link valid until ${until}` }
}

/** The sentence form, for a cell's accessible name. */
export function accountSentence(user: UserRow): string {
  const { label, detail } = describeAccount(user)
  return detail ? `${label}: ${detail}` : label
}

/** An employee's status in words. */
export const employeeStatusLabel: Record<EmployeeRow['status'], string> = {
  Active: 'Active',
  OnLeave: 'On leave',
  Terminated: 'Terminated',
}
