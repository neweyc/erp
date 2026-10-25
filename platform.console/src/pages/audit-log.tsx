import { Link } from 'react-router-dom'
import { ScrollText } from 'lucide-react'
import {
  Alert, Card, DataTable, EmptyState, Loading, PageHeader, PageStack, type ColumnDef,
} from '@app-platform/ui-kit'
import { ReadAt } from '../components/read-at'
import { formatMoment, routes, type AuditEntry } from '../lib/api'
import { useLoad } from '../lib/use-load'

/** What each recorded action means, in words. An action this does not know is shown as recorded. */
const ACTIONS: Record<string, string> = {
  'operator.signed_in': 'Signed in',
  'operator.sign_in_failed': 'Failed to sign in',
  'operator.signed_out': 'Signed out',
  'tenant.created': 'Created the tenant',
  'tenant.active': 'Resumed the tenant',
  'tenant.suspended': 'Suspended the tenant',
  'tenant.retired': 'Retired the tenant',
  'tenant.address_changed': 'Changed the address',
  'entitlement.granted': 'Licensed an app',
  'entitlement.revoked': 'Revoked an app',
}

export function describeAction(action: string): string {
  return ACTIONS[action] ?? action
}

function columnsFor(showTenant: boolean): ColumnDef<AuditEntry, any>[] {
  return [
    { accessorKey: 'at', header: 'When', cell: ({ row }) => formatMoment(row.original.at) },
    {
      accessorKey: 'operatorEmail',
      header: 'Operator',
      cell: ({ row }) => row.original.operatorEmail ?? <span className="text-muted-foreground">Nobody signed in</span>,
    },
    { id: 'action', header: 'Action', accessorFn: (entry) => describeAction(entry.action) },
    ...(showTenant
      ? [{
          accessorKey: 'tenantId',
          header: 'Tenant',
          cell: ({ row }) => row.original.tenantId
            ? <Link to={`/tenants/${row.original.tenantId}`} className="font-mono text-xs hover:underline">{row.original.tenantId}</Link>
            : null,
        } satisfies ColumnDef<AuditEntry, any>]
      : []),
    { accessorKey: 'detail', header: 'Detail', enableSorting: false },
  ]
}

/** The operator audit table, also used on a tenant's page (where the tenant column is redundant). */
export function AuditTable({ entries, showTenant }: { entries: readonly AuditEntry[]; showTenant: boolean }) {
  return (
    <DataTable
      label="Operator actions"
      columns={columnsFor(showTenant)}
      data={entries}
      getRowId={(entry) => String(entry.id)}
      empty={<EmptyState icon={ScrollText} title="No operator actions recorded" />}
    />
  )
}

/**
 * Everything operators have done, newest first: sign-ins (failed ones too), and every change to a
 * tenant. Kept apart from every tenant's own audit; nothing a customer did is here.
 */
export function AuditLog() {
  const { data, error, readAt, reload } = useLoad<AuditEntry[]>(routes.audit)

  return (
    <PageStack>
      <PageHeader
        title="Audit log"
        description="The 100 most recent operator actions, newest first. Repeated failed sign-ins are worth noticing."
        actions={<ReadAt readAt={readAt} onRefresh={reload} />}
      />
      {error && <Alert tone="danger">The audit log could not be read ({error}). Try Refresh.</Alert>}
      <Card>
        {data ? <AuditTable entries={data} showTenant /> : !error && <Loading className="px-6">Loading the audit log…</Loading>}
      </Card>
    </PageStack>
  )
}
