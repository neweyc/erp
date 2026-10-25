import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Building2, Plus } from 'lucide-react'
import {
  Alert, Button, Card, DataTable, Dialog, DialogBody, DialogFooter, EmptyState, Field, Input, Loading, PageHeader,
  PageStack, type ColumnDef,
} from '@app-platform/ui-kit'
import { ReadAt } from '../components/read-at'
import { StatusBadge } from '../components/status-badge'
import { api, ApiError, appName, formatMoment, routes, type TenantSummary } from '../lib/api'
import { useLoad } from '../lib/use-load'

const columns: ColumnDef<TenantSummary, any>[] = [
  {
    accessorKey: 'name',
    header: 'Tenant',
    cell: ({ row }) => (
      <Link to={`/tenants/${row.original.tenantId}`} className="font-medium hover:underline">
        {row.original.name}
      </Link>
    ),
  },
  { accessorKey: 'status', header: 'Status', cell: ({ row }) => <StatusBadge status={row.original.status} /> },
  {
    id: 'apps',
    header: 'Licensed apps',
    enableSorting: false,
    accessorFn: (tenant) => tenant.apps.map(appName).join(', '),
    cell: ({ getValue }) => getValue() || <span className="text-muted-foreground">None</span>,
  },
  { accessorKey: 'createdAt', header: 'Created', cell: ({ row }) => formatMoment(row.original.createdAt) },
]

export function Tenants() {
  const { data, error, readAt, reload } = useLoad<TenantSummary[]>(routes.tenants)
  const [creating, setCreating] = useState(false)

  return (
    <PageStack>
      <PageHeader
        title="Tenants"
        description="Every customer on the platform: whether they can use it, and which apps they have licensed."
        actions={
          <>
            <ReadAt readAt={readAt} onRefresh={reload} />
            <Button variant="primary" onClick={() => setCreating(true)}>
              <Plus aria-hidden />
              New tenant
            </Button>
          </>
        }
      />

      {error && <Alert tone="danger">The tenants could not be read ({error}). Try Refresh.</Alert>}

      <Card>
        {data ? (
          <DataTable
            label="Tenants"
            columns={columns}
            data={data}
            getRowId={(tenant) => tenant.tenantId}
            filterLabel="Search tenants"
            empty={
              <EmptyState icon={Building2} title="No tenants yet">
                Create one, and its administrator is sent an invitation.
              </EmptyState>
            }
          />
        ) : (
          !error && <Loading className="px-6">Loading tenants…</Loading>
        )}
      </Card>

      {/* Mounted only while open, so each opening starts with empty fields and a new key. */}
      {creating && (
        <NewTenantDialog
          onClose={() => {
            setCreating(false)
            // A failed attempt may still have created the tenant; reading again shows it.
            void reload()
          }}
        />
      )}
    </PageStack>
  )
}

/**
 * Creates a tenant: its organization, its company, and an invitation for its first administrator,
 * all made by core in one transaction.
 *
 * The idempotency key is made once per opening of this dialog and reused by every submit from it.
 * A submit that times out has an unknown result; pressing Create again then returns the tenant the
 * first attempt made instead of creating a second customer. So the dialog cannot be closed while a
 * submit is pending: reopening it would make a new key, and the same tenant could be made twice.
 */
function NewTenantDialog({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const [key] = useState(() => crypto.randomUUID())
  const [name, setName] = useState('')
  const [adminEmail, setAdminEmail] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const created = await api<{ tenantId: string }>(routes.tenants, {
        method: 'POST',
        body: JSON.stringify({ name, adminEmail, idempotencyKey: key }),
      })
      navigate(`/tenants/${created.tenantId}`)
    } catch (caught) {
      // A validation message is written for people ("An admin email is required."). Anything else,
      // including core being unreachable, is worth retrying, and the key makes that safe.
      setError(caught instanceof ApiError && caught.problemCode === 'validation_failed'
        ? caught.message
        : 'The tenant could not be created. Try again: it will not be created twice.')
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && !busy && onClose()}
      dismissible={false}
      title="New tenant"
      description="Their administrator is emailed an invitation to set a password and sign in."
    >
      <form onSubmit={submit}>
        <DialogBody className="grid gap-4">
          <Field label="Organization name" htmlFor="tenant-name">
            <Input id="tenant-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} required />
          </Field>
          <Field label="Administrator's email" htmlFor="admin-email">
            <Input id="admin-email" type="email" value={adminEmail} onChange={(e) => setAdminEmail(e.target.value)} required />
          </Field>
          {error && <Alert tone="danger">{error}</Alert>}
        </DialogBody>
        <DialogFooter>
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>{busy ? 'Creating…' : 'Create tenant'}</Button>
        </DialogFooter>
      </form>
    </Dialog>
  )
}
