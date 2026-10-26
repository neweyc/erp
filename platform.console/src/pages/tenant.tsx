import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import {
  Alert, Badge, Button, Card, CardContent, CardHeader, ConfirmDialog, Dialog, DialogBody, DialogFooter, EmptyState,
  Field, Input, Loading, PageHeader, PageStack, Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@app-platform/ui-kit'
import { AddressField } from '../components/address-field'
import { ReadAt } from '../components/read-at'
import { StatusBadge } from '../components/status-badge'
import { AuditTable } from './audit-log'
import {
  api, ApiError, appName, formatMoment, LICENSABLE_APPS, routes, type TenantDetail, type TenantStatus,
} from '../lib/api'
import { useLoad } from '../lib/use-load'

/** A change of status, as the operator chooses it. `status` is what is sent. */
interface StatusChange {
  readonly status: TenantStatus
  readonly verb: string
  /** What the tenant's people will experience, in plain words (platform.console/CLAUDE.md). */
  readonly consequence: (name: string) => string
  /** Destructive changes are confirmed by typing the tenant's name. */
  readonly typeToConfirm: boolean
}

const SUSPEND: StatusChange = {
  status: 'Suspended',
  verb: 'Suspend',
  consequence: (name) =>
    `Everyone at ${name} is stopped from using the platform at once. They can still sign in, and are told ` +
    'their account is on hold. Nothing is deleted, and you can resume them at any time.',
  typeToConfirm: true,
}

const RETIRE: StatusChange = {
  status: 'Retired',
  verb: 'Retire',
  consequence: (name) =>
    `Everyone at ${name} is signed out and can never sign in again. This cannot be undone: a retired ` +
    'tenant cannot be reactivated. To serve them again you would create a new tenant.',
  typeToConfirm: true,
}

const RESUME: StatusChange = {
  status: 'Active',
  verb: 'Resume',
  consequence: (name) => `Everyone at ${name} can use the platform again, with the apps they have licensed.`,
  typeToConfirm: false,
}

const CHOICES: Record<TenantStatus, StatusChange[]> = {
  Active: [SUSPEND, RETIRE],
  Suspended: [RESUME, RETIRE],
  // Terminal: the API refuses any change, so none is offered.
  Retired: [],
}

/** Wording for the conflicts a status or license change can meet. */
const REFUSAL: Record<string, string> = {
  already_in_that_state: 'Someone else already made that change. The page has been read again.',
  app_already_licensed: 'That app is already licensed. The page has been read again.',
  tenant_retired: 'A retired tenant cannot be reactivated.',
}

export function TenantPage() {
  const { tenantId = '' } = useParams()
  const { data: tenant, error, readAt, reload } = useLoad<TenantDetail>(routes.tenant(tenantId))
  const [change, setChange] = useState<StatusChange | null>(null)
  const [revoking, setRevoking] = useState<string | null>(null)
  const [changingAddress, setChangingAddress] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (error === 'not_found') {
    return (
      <EmptyState title="No such tenant" headingLevel={1} action={<Link to="/tenants" className="text-sm underline">All tenants</Link>}>
        The link may be wrong, or the tenant id was mistyped.
      </EmptyState>
    )
  }
  if (!tenant) {
    return error
      ? <Alert tone="danger">The tenant could not be read ({error}). Try again.</Alert>
      : <Loading>Loading tenant…</Loading>
  }

  /** Sends one change, then reads the tenant again whatever happened, so the page shows the truth. */
  async function send(path: string, body: object) {
    setBusy(true)
    setFailure(null)
    try {
      await api(path, { method: 'POST', body: JSON.stringify(body) })
    } catch (caught) {
      const code = caught instanceof ApiError ? caught.problemCode : 'unknown'
      setFailure(REFUSAL[code] ?? `That did not work (${code}). The page has been read again.`)
    } finally {
      setChange(null)
      setRevoking(null)
      setBusy(false)
      await reload()
    }
  }

  const retired = tenant.status === 'Retired'

  return (
    <PageStack>
      <Link to="/tenants" className="flex w-fit items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft aria-hidden className="size-4" />
        All tenants
      </Link>

      <PageHeader
        title={tenant.name}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <StatusBadge status={tenant.status} />
            <span>
              Created {formatMoment(tenant.createdAt)}
              {tenant.statusChangedAt && ` · status changed ${formatMoment(tenant.statusChangedAt)}`}
            </span>
            <span className="font-mono text-xs">{tenant.tenantId}</span>
          </span>
        }
        actions={<ReadAt readAt={readAt} onRefresh={reload} />}
      />

      {failure && <Alert tone="warning">{failure}</Alert>}
      {error && <Alert tone="danger">The tenant could not be read again ({error}). What is shown may be out of date.</Alert>}

      <Card>
        <CardHeader
          className="pb-5"
          title="Access"
          description={
            retired
              ? 'Retired. Nobody at this tenant can sign in, and that cannot be changed.'
              : tenant.status === 'Suspended'
                ? 'Suspended. Their people can sign in but cannot use anything until you resume them.'
                : 'Active. Their people can use the platform and every app licensed below.'
          }
          actions={CHOICES[tenant.status].map((choice) => (
            <Button
              key={choice.status}
              variant={choice.typeToConfirm ? 'destructive' : 'primary'}
              onClick={() => setChange(choice)}
            >
              {choice.verb}
            </Button>
          ))}
        />
      </Card>

      <Card>
        <CardHeader
          className="pb-5"
          title="Address"
          description={tenant.signInUrl
            ? <>Their people sign in at <a href={tenant.signInUrl} className="font-medium text-foreground underline">{tenant.signInUrl}</a>.</>
            : 'This tenant has no address, so nobody can sign in to it.'}
          actions={!retired && <Button onClick={() => setChangingAddress(true)}>Change address</Button>}
        />
        {tenant.addresses.some((a) => a.replacedAt) && (
          <CardContent>
            <p className="mb-2 text-sm text-muted-foreground">
              Former addresses no longer sign anyone in, and stay reserved to this tenant.
            </p>
            <ul aria-label="Former addresses" className="grid gap-1 text-sm">
              {tenant.addresses.filter((a) => a.replacedAt).map((a) => (
                // One entry per span, so an address taken back and left again appears twice.
                <li key={`${a.subdomain}@${a.replacedAt}`}>
                  <span className="font-mono">{a.subdomain}</span>
                  <span className="text-muted-foreground">
                    {' '}· {formatMoment(a.assignedAt)} until {formatMoment(a.replacedAt!)}
                  </span>
                </li>
              ))}
            </ul>
          </CardContent>
        )}
      </Card>

      <Card>
        <CardHeader title="Apps" description="Licensing an app turns it on for this tenant. Revoking hides it; nothing is deleted." />
        <CardContent className="grid gap-2">
          {LICENSABLE_APPS.map((app) => {
            const licensed = tenant.apps.includes(app.id)
            return (
              <div key={app.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border px-4 py-3">
                <div className="flex items-center gap-3">
                  <span className="font-medium">{app.name}</span>
                  <Badge tone={licensed ? 'brand' : 'neutral'}>{licensed ? 'Licensed' : 'Not licensed'}</Badge>
                </div>
                {!retired && (
                  licensed ? (
                    <Button size="sm" onClick={() => setRevoking(app.id)} disabled={busy}
                      aria-label={`Revoke ${app.name}`}>
                      Revoke
                    </Button>
                  ) : (
                    <Button size="sm" variant="primary" disabled={busy} aria-label={`License ${app.name}`}
                      onClick={() => void send(routes.entitlements(tenant.tenantId), { app: app.id, licensed: true })}>
                      License
                    </Button>
                  )
                )}
              </div>
            )
          })}
        </CardContent>
      </Card>

      <Card>
        <CardHeader title="License history" description="Every app this tenant has had, and when." />
        <CardContent>
          {tenant.grants.length === 0 ? (
            <p className="text-sm text-muted-foreground">No app has been licensed.</p>
          ) : (
            <Table aria-label="License history">
              <TableHeader>
                <TableRow>
                  <TableHead>App</TableHead>
                  <TableHead>Licensed</TableHead>
                  <TableHead>Revoked</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tenant.grants.map((grant) => (
                  <TableRow key={`${grant.app}-${grant.grantedAt}`}>
                    <TableCell>{appName(grant.app)}</TableCell>
                    <TableCell>{formatMoment(grant.grantedAt)}</TableCell>
                    <TableCell>{grant.revokedAt ? formatMoment(grant.revokedAt) : '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader title="Operator actions" description="What operators have done to this tenant, newest first." />
        <AuditTable entries={tenant.actions} showTenant={false} />
      </Card>

      {change && (
        <StatusDialog
          tenantName={tenant.name}
          change={change}
          busy={busy}
          onCancel={() => setChange(null)}
          onConfirm={(reason) => void send(routes.status(tenant.tenantId), { status: change.status, reason })}
        />
      )}

      {changingAddress && (
        <AddressDialog
          tenant={tenant}
          onClose={() => setChangingAddress(false)}
          onChanged={() => {
            setChangingAddress(false)
            void reload()
          }}
        />
      )}

      <ConfirmDialog
        open={revoking !== null}
        onOpenChange={(open) => !open && setRevoking(null)}
        title={`Revoke ${appName(revoking ?? '')} for ${tenant.name}?`}
        description={`Their people lose ${appName(revoking ?? '')} at once. Nothing is deleted: licensing it again brings back everything they had.`}
        confirmLabel="Revoke"
        busy={busy}
        onConfirm={() => void send(routes.entitlements(tenant.tenantId), { app: revoking, licensed: false })}
      />
    </PageStack>
  )
}

/**
 * Confirms a change of status. Suspending and retiring are confirmed by typing the tenant's name,
 * so the operator has read which customer this is; and the dialog says what that customer's people
 * will experience. An optional reason goes into the operator audit log.
 */
function StatusDialog({
  tenantName, change, busy, onCancel, onConfirm,
}: {
  tenantName: string
  change: StatusChange
  busy: boolean
  onCancel: () => void
  onConfirm: (reason: string) => void
}) {
  const [reason, setReason] = useState('')
  const [typed, setTyped] = useState('')
  const confirmed = !change.typeToConfirm || typed.trim() === tenantName

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onCancel()}
      dismissible={false}
      title={`${change.verb} ${tenantName}?`}
      description={change.consequence(tenantName)}
    >
      <form onSubmit={(e) => { e.preventDefault(); if (confirmed) onConfirm(reason) }}>
        <DialogBody className="grid gap-4">
          <Field label="Reason (optional)" htmlFor="status-reason" hint="Recorded in the operator audit log.">
            <Input id="status-reason" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />
          </Field>
          {change.typeToConfirm && (
            <Field label={`Type ${tenantName} to confirm`} htmlFor="confirm-name">
              <Input id="confirm-name" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" />
            </Field>
          )}
        </DialogBody>
        <DialogFooter>
          <Button type="button" onClick={onCancel} disabled={busy}>Cancel</Button>
          <Button type="submit" variant={change.typeToConfirm ? 'destructive' : 'primary'} disabled={busy || !confirmed}>
            {change.verb}
          </Button>
        </DialogFooter>
      </form>
    </Dialog>
  )
}

/** Refusals of an address change whose message the operator can act on as written. */
const ADDRESS_REFUSALS = ['validation_failed', 'subdomain_taken', 'already_in_that_state']

/**
 * Moves a tenant to a new address. Says what that does to its people before it is done: the old
 * address stops signing anyone in at once, so a bookmark or an unaccepted invitation's link leads
 * nowhere, while anyone signed in there stays signed in until their session ends.
 */
function AddressDialog({
  tenant, onClose, onChanged,
}: {
  tenant: TenantDetail
  onClose: () => void
  onChanged: () => void
}) {
  const [value, setValue] = useState(tenant.subdomain ?? '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api(routes.address(tenant.tenantId), { method: 'POST', body: JSON.stringify({ subdomain: value }) })
      onChanged()
    } catch (caught) {
      setError(caught instanceof ApiError && ADDRESS_REFUSALS.includes(caught.problemCode)
        ? caught.message
        : 'The address could not be changed. Try again.')
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && !busy && onClose()}
      dismissible={false}
      title={`Change ${tenant.name}'s address?`}
      description={
        `From then on their people sign in at the new address only. ${tenant.subdomain ? `"${tenant.subdomain}"` : 'The old address'} ` +
        'stops working at once for anyone signed out, including links in invitations not yet accepted; anyone ' +
        'signed in there stays signed in until their session ends. The old address stays reserved to this tenant.'
      }
    >
      <form onSubmit={submit}>
        <DialogBody className="grid gap-4">
          <AddressField id="new-address" value={value} onChange={setValue} />
          {error && <Alert tone="danger">{error}</Alert>}
        </DialogBody>
        <DialogFooter>
          <Button type="button" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy}>{busy ? 'Changing…' : 'Change address'}</Button>
        </DialogFooter>
      </form>
    </Dialog>
  )
}
