import { Link } from 'react-router-dom'
import { CircleCheck } from 'lucide-react'
import {
  Alert, Card, DataTable, EmptyState, Loading, PageHeader, PageStack, type ColumnDef,
} from '@app-platform/ui-kit'
import { ReadAt } from '../components/read-at'
import { formatMoment, routes, type ErrorOccurrence } from '../lib/api'
import { useLoad } from '../lib/use-load'

const columns: ColumnDef<ErrorOccurrence, any>[] = [
  { accessorKey: 'occurredAt', header: 'When', cell: ({ row }) => formatMoment(row.original.occurredAt) },
  {
    accessorKey: 'reference',
    header: 'Reference',
    cell: ({ row }) => <span className="font-mono text-xs">{row.original.reference}</span>,
  },
  { accessorKey: 'app', header: 'Service' },
  {
    accessorKey: 'tenantId',
    header: 'Tenant',
    cell: ({ row }) => row.original.tenantId
      ? <Link to={`/tenants/${row.original.tenantId}`} className="font-mono text-xs hover:underline">{row.original.tenantId}</Link>
      : <span className="text-muted-foreground">None</span>,
  },
  { accessorKey: 'count', header: 'Times' },
  {
    accessorKey: 'fingerprint',
    header: 'Fingerprint',
    cell: ({ row }) => <span className="font-mono text-xs">{row.original.fingerprint}</span>,
  },
]

/**
 * Recent errors from every service, as metadata only. A customer quotes the reference from their
 * error message; the detail is in that service's own log under the same reference.
 */
export function ErrorFeed() {
  const { data, error, readAt, reload } = useLoad<ErrorOccurrence[]>(routes.errors)

  return (
    <PageStack>
      <PageHeader
        title="Errors"
        description="Recent errors from every service, newest first. Search for the reference a customer quotes; its detail is in that service's log."
        actions={<ReadAt readAt={readAt} onRefresh={reload} />}
      />
      {error && <Alert tone="danger">The error feed could not be read ({error}). Try Refresh.</Alert>}
      <Card>
        {data ? (
          <DataTable
            label="Errors"
            columns={columns}
            data={data}
            getRowId={(row) => row.reference}
            filterLabel="Search errors"
            empty={<EmptyState icon={CircleCheck} title="No errors in the last 30 days" />}
          />
        ) : (
          !error && <Loading className="px-6">Loading errors…</Loading>
        )}
      </Card>
    </PageStack>
  )
}
