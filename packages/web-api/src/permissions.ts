/**
 * Permission names, mirrored from packages/auth/Permissions.cs. The session returns the caller's
 * permissions as these strings; UIs use them to HIDE what the user cannot do. The APIs enforce
 * independently — nothing decided here is access control.
 *
 * Kept in sync by boundary.tests/PermissionCatalogueTests, which fails when this list and the
 * .NET catalogue disagree. Lives in this package because every UI already depends on it.
 */
export const Permissions = {
  core: {
    employeeRead: 'core.employee.read',
    employeeCreate: 'core.employee.create',
    userRead: 'core.user.read',
    userManage: 'core.user.manage',
  },
  tickets: {
    ticketRead: 'tickets.ticket.read',
    ticketCreate: 'tickets.ticket.create',
    ticketAssign: 'tickets.ticket.assign',
    ticketClose: 'tickets.ticket.close',
  },
  ledger: {
    read: 'ledger.read',
    entryPost: 'ledger.entry.post',
    entryReverse: 'ledger.entry.reverse',
    accountCreate: 'ledger.account.create',
    periodClose: 'ledger.period.close',
  },
} as const

type Catalogue = typeof Permissions

/** Any one permission name. Mapped per app, because keyof a union of the groups would be never. */
export type Permission = {
  [App in keyof Catalogue]: Catalogue[App][keyof Catalogue[App]]
}[keyof Catalogue]

/**
 * What the shell hands each app: the caller's permissions, already computed by the server.
 * An app component takes these as props rather than reading the session itself, so the shell
 * stays the one place that knows who is signed in.
 */
export interface AppProps {
  readonly permissions: readonly string[]
}
