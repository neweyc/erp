import { useCallback, useEffect, useMemo, useState } from 'react'
import { Pencil, Send, UserMinus, UserPlus, UsersRound } from 'lucide-react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import {
  Alert, Avatar, Badge, Button, Card, Checkbox, CheckboxRow, ConfirmDialog, DataTable, Dialog, DialogBody,
  DialogFooter, EmptyState, Field, Input, Loading, NativeSelect, PageHeader, PageStack, type BadgeTone, type ColumnDef,
  type ColumnMeta,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { RoleChecklist, toggled } from '../components/role-checklist'
import { accountSentence, describeAccount, type EmployeeRow, employeeStatusLabel, type UserRow } from '../lib/people'
import { departmentItems, pickerOptions, type CatalogRow, type DepartmentRow } from '../lib/catalogs'
import type { RoleRow } from '../lib/roles'

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  email_in_use: 'Someone else already has that email address.',
  employee_has_account: 'That person already has an account. The list has been reloaded.',
  employee_terminated: 'That person is terminated, so they cannot be given access.',
  employee_changed: 'Their email address changed while this was open, so nothing was sent. Close this, check the address in the reloaded list, and try again.',
  cannot_terminate_self: 'You cannot terminate yourself. Ask another administrator.',
  last_admin: 'They are the last person who can manage users. Give someone else that permission first.',
  not_found: 'That person is no longer on the roster. The list has been reloaded.',
  not_permitted: 'You do not have permission to do that.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.'
  // A validation failure's message names the field at fault ("First and last name are required.").
  if (error.problemCode === 'validation_failed' && error.message) return error.message
  return PROBLEMS[error.problemCode] ?? 'Something went wrong. Please try again.'
}

const statusTone: Record<EmployeeRow['status'], BadgeTone> = { Active: 'success', OnLeave: 'warning', Terminated: 'neutral' }

/** The details form, shared by adding and editing. Each placement is an id, or '' for none. */
interface Details {
  readonly firstName: string
  readonly lastName: string
  readonly email: string
  readonly departmentId: string
  readonly jobTitleId: string
  readonly locationId: string
}

const NO_DETAILS: Details = { firstName: '', lastName: '', email: '', departmentId: '', jobTitleId: '', locationId: '' }

/** The three lists an employee is placed from. */
interface Catalogs {
  readonly departments: readonly CatalogRow[]
  readonly jobTitles: readonly CatalogRow[]
  readonly locations: readonly CatalogRow[]
}

const toRows = (rows: Record<string, unknown>[], idField: string): CatalogRow[] =>
  rows.map((r) => ({ id: String(r[idField]), name: String(r.name), active: Boolean(r.active) }))

type Open =
  | { kind: 'add'; details: Details; invite: boolean; chosen: ReadonlySet<string> }
  | { kind: 'edit'; employee: EmployeeRow; details: Details }
  | { kind: 'access'; employee: EmployeeRow; chosen: ReadonlySet<string> }
  | { kind: 'terminate'; employee: EmployeeRow }

const MEMBER = new Set(['core:member'])

/**
 * The employee roster: who works here (docs/permissions.md §7). Everyone with core.employee.read
 * sees it; adding, editing and terminating each need their own permission, and the API enforces
 * every one.
 *
 * Access is given from here: every account belongs to an employee, and an invitation goes to the
 * address on their record. The access column needs core.user.read, so a reader without it sees the
 * roster alone.
 */
export function Employees() {
  const { session } = useSession()
  const [employees, setEmployees] = useState<readonly EmployeeRow[]>([])
  const [users, setUsers] = useState<readonly UserRow[]>([])
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  const [catalogs, setCatalogs] = useState<Catalogs>({ departments: [], jobTitles: [], locations: [] })
  const [showTerminated, setShowTerminated] = useState(false)
  const [loading, setLoading] = useState(true)
  const [open, setOpen] = useState<Open | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<{ tone: 'success' | 'warning'; text: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const can = (permission: string) => session?.permissions.includes(permission) ?? false
  const canSeeAccess = can(Permissions.core.userRead)
  const canInvite = can(Permissions.core.userManage)
  const canAdd = can(Permissions.core.employeeCreate)
  const canEdit = can(Permissions.core.employeeUpdate)
  const canTerminate = can(Permissions.core.employeeTerminate)
  const myId = session?.userId

  const load = useCallback(async () => {
    try {
      const [employeeRows, userRows, roleRows, departmentRows, jobTitleRows, locationRows] = await Promise.all([
        api<EmployeeRow[]>(`/api/core/v1/employees?includeTerminated=${showTerminated}`),
        canSeeAccess ? api<UserRow[]>('/api/core/v1/users') : Promise.resolve([]),
        canInvite ? api<RoleRow[]>('/api/core/v1/roles') : Promise.resolve([]),
        // Only the forms need the lists; the roster rows carry their own names.
        canAdd || canEdit ? api<DepartmentRow[]>('/api/core/v1/departments') : Promise.resolve([]),
        canAdd || canEdit ? api<Record<string, unknown>[]>('/api/core/v1/job-titles') : Promise.resolve([]),
        canAdd || canEdit ? api<Record<string, unknown>[]>('/api/core/v1/locations') : Promise.resolve([]),
      ])
      setEmployees(employeeRows)
      setUsers(userRows)
      setRoles(roleRows)
      setCatalogs({
        departments: departmentItems(departmentRows),
        jobTitles: toRows(jobTitleRows, 'jobTitleId'),
        locations: toRows(locationRows, 'locationId'),
      })
    } catch (e) {
      setError(explain(e))
    } finally {
      setLoading(false)
    }
  }, [showTerminated, canSeeAccess, canInvite, canAdd, canEdit])

  useEffect(() => {
    void load()
  }, [load])

  const accountOf = useMemo(() => {
    const byEmployee = new Map(users.filter((u) => u.employeeId).map((u) => [u.employeeId!, u]))
    return (employee: EmployeeRow) => byEmployee.get(employee.employeeId)
  }, [users])

  const columns = useMemo<ColumnDef<EmployeeRow>[]>(() => {
    const columns: ColumnDef<EmployeeRow>[] = [
      {
        id: 'name',
        header: 'Name',
        accessorFn: (e) => `${e.lastName} ${e.firstName} ${e.email ?? ''}`,
        cell: ({ row: { original: e } }) => (
          <span className="flex min-w-0 items-center gap-3">
            <Avatar name={e.displayName} />
            <span className="grid min-w-0">
              <span className="truncate font-medium">{e.displayName}</span>
              <span className="truncate text-xs text-muted-foreground">{e.email ?? 'No email address'}</span>
            </span>
          </span>
        ),
      },
      {
        id: 'position',
        header: 'Position',
        accessorFn: (e) => [e.jobTitle?.name, e.department?.name, e.location?.name].filter(Boolean).join(' · '),
        meta: { cellLabel: (e) => positionSentence(e) } satisfies ColumnMeta<EmployeeRow>,
        cell: ({ row: { original: e } }) => {
          const where = [e.department?.name, e.location?.name].filter(Boolean).join(' · ')
          if (!e.jobTitle && !where) return <span className="text-sm text-muted-foreground">Not set</span>
          return (
            <span className="grid min-w-0">
              <span className="truncate">{e.jobTitle?.name ?? 'No job title'}</span>
              {where && <span className="truncate text-xs text-muted-foreground">{where}</span>}
            </span>
          )
        },
      },
      {
        id: 'status',
        header: 'Status',
        accessorFn: (e) => employeeStatusLabel[e.status],
        cell: ({ row: { original: e } }) => <Badge tone={statusTone[e.status]}>{employeeStatusLabel[e.status]}</Badge>,
      },
    ]

    if (canSeeAccess) {
      columns.push({
        id: 'access',
        header: 'Access',
        accessorFn: (e) => accountOf(e)?.status ?? 'No access',
        meta: { cellLabel: (e) => { const a = accountOf(e); return a ? accountSentence(a) : 'No access' } } satisfies ColumnMeta<EmployeeRow>,
        cell: ({ row: { original: e } }) => {
          const account = accountOf(e)
          if (!account) return <span className="text-sm text-muted-foreground">No access</span>
          const { label, tone, detail } = describeAccount(account)
          return (
            <span className="grid justify-items-start gap-1">
              <Badge tone={tone} dot>{label}</Badge>
              {detail && <span className="text-xs whitespace-nowrap text-muted-foreground first-letter:uppercase">{detail}</span>}
              {/* Editing a roster email never moves a sign-in (UpdateEmployeeFeature): say when they differ. */}
              {e.email && account.email !== e.email && (
                <span className="text-xs text-muted-foreground">Signs in as {account.email}</span>
              )}
            </span>
          )
        },
      })
    }

    if (canEdit || canInvite || canTerminate) {
      columns.push({
        id: 'actions',
        header: () => <span className="sr-only">Actions</span>,
        enableSorting: false,
        meta: { className: 'text-right' },
        cell: ({ row: { original: e } }) => {
          const account = accountOf(e)
          const terminated = e.status === 'Terminated'
          return (
            <span className="flex justify-end gap-1.5">
              {canInvite && canSeeAccess && !account && !terminated && e.email && (
                <Button size="sm" variant="outline" aria-label={`Give ${e.displayName} access`} disabled={saving}
                  onClick={() => show({ kind: 'access', employee: e, chosen: MEMBER })}>
                  <Send /> Give access
                </Button>
              )}
              {canEdit && (
                <Button size="sm" variant="ghost" aria-label={`Edit ${e.displayName}`} disabled={saving}
                  onClick={() => show({ kind: 'edit', employee: e, details: detailsOf(e) })}>
                  <Pencil /> Edit
                </Button>
              )}
              {canTerminate && !terminated && account?.userId !== myId && (
                <Button size="sm" variant="ghost" aria-label={`Terminate ${e.displayName}`} disabled={saving}
                  className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                  onClick={() => setOpen({ kind: 'terminate', employee: e })}>
                  <UserMinus /> Terminate
                </Button>
              )}
            </span>
          )
        },
      })
    }

    return columns
    // `show` is not a dependency: it uses only state setters, which never change.
  }, [accountOf, canSeeAccess, canEdit, canInvite, canTerminate, myId, saving])

  if (!session) return null

  function show(dialog: Open) {
    setDialogError(null)
    setOpen(dialog)
  }

  /** Runs a dialog's writes; on failure the dialog stays open with the reason (see Users). */
  async function submit(request: () => Promise<void>) {
    setSaving(true)
    setDialogError(null)
    try {
      await request()
      setOpen(null)
    } catch (e) {
      setDialogError(explain(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  // The whole record, placements included: an edit replaces all of it, so '' (none) is sent as null.
  const detailsBody = (details: Details) =>
    JSON.stringify({
      firstName: details.firstName,
      lastName: details.lastName,
      email: details.email,
      departmentId: details.departmentId || null,
      jobTitleId: details.jobTitleId || null,
      locationId: details.locationId || null,
    })

  /**
   * Adds the employee and, if asked, invites them: two requests, because they are two things. If
   * the invitation is refused the employee is still added, and the page says so; "Give access" on
   * their row tries again.
   */
  function add(details: Details, invite: boolean, chosen: ReadonlySet<string>) {
    const name = `${details.firstName.trim()} ${details.lastName.trim()}`
    void submit(async () => {
      const { employeeId } = await api<{ employeeId: string }>('/api/core/v1/employees', { method: 'POST', body: detailsBody(details) })
      if (!invite) {
        setNotice({ tone: 'success', text: `${name} is on the roster.` })
        return
      }
      try {
        await api(`/api/core/v1/employees/${employeeId}/invite`, {
          method: 'POST',
          body: JSON.stringify({ roles: [...chosen], email: details.email }),
        })
        setNotice({ tone: 'success', text: `${name} is on the roster, and an invitation is on its way.` })
      } catch (e) {
        setNotice({ tone: 'warning', text: `${name} is on the roster, but the invitation was not sent: ${explain(e)}` })
      }
    })
  }

  async function terminate(employee: EmployeeRow) {
    setOpen(null)
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      await api(`/api/core/v1/employees/${employee.employeeId}/terminate`, { method: 'POST' })
      setNotice({ tone: 'success', text: `${employee.displayName} is terminated.` })
    } catch (e) {
      setError(explain(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  if (loading) return <Loading>Loading employees…</Loading>

  const terminating = open?.kind === 'terminate' ? open.employee : null
  const terminatingAccount = terminating ? accountOf(terminating) : undefined

  return (
    <PageStack>
      <PageHeader
        title="Employees"
        description={`Everyone who works at ${session.tenantName}. Access to the apps is given from here.`}
        actions={
          canAdd && (
            <Button variant="primary"
              onClick={() => show({ kind: 'add', details: NO_DETAILS, invite: canInvite, chosen: MEMBER })}>
              <UserPlus /> Add employee
            </Button>
          )
        }
      />

      {error && <Alert tone="danger">{error}</Alert>}
      {notice && <Alert tone={notice.tone}>{notice.text}</Alert>}

      <Card>
        <DataTable
          label="Employees"
          columns={columns}
          data={employees}
          getRowId={(e) => e.employeeId}
          filterLabel="Search employees"
          toolbar={
            <label className="flex cursor-pointer items-center gap-2 text-sm text-muted-foreground">
              <Checkbox checked={showTerminated} onChange={(e) => setShowTerminated(e.target.checked)} />
              Show terminated
            </label>
          }
          empty={
            <EmptyState icon={UsersRound} title="No employees yet">
              {canAdd ? 'Add the first person with “Add employee”.' : 'Nobody has been added to the roster.'}
            </EmptyState>
          }
        />
      </Card>

      {(open?.kind === 'add' || open?.kind === 'edit') && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setOpen(null)}
          // Holds a part-filled form: a stray click outside must not discard it.
          dismissible={false}
          title={open.kind === 'add' ? 'Add employee' : `Edit ${open.employee.displayName}`}
          size={open.kind === 'add' && open.invite ? 'lg' : 'md'}
        >
          <form
            aria-label={open.kind === 'add' ? 'Add employee' : `Edit ${open.employee.displayName}`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(event) => {
              event.preventDefault()
              if (open.kind === 'add') add(open.details, open.invite && open.details.email.trim() !== '', open.chosen)
              else
                void submit(async () => {
                  await api(`/api/core/v1/employees/${open.employee.employeeId}`, { method: 'PUT', body: detailsBody(open.details) })
                  setNotice({ tone: 'success', text: `${open.details.firstName.trim()} ${open.details.lastName.trim()} is updated.` })
                })
            }}
          >
            <DialogBody className="grid gap-5">
              {dialogError && <Alert tone="danger">{dialogError}</Alert>}
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="First name" htmlFor="employee-first">
                  <Input id="employee-first" required maxLength={100} value={open.details.firstName}
                    onChange={(e) => setOpen({ ...open, details: { ...open.details, firstName: e.target.value } })} />
                </Field>
                <Field label="Last name" htmlFor="employee-last">
                  <Input id="employee-last" required maxLength={100} value={open.details.lastName}
                    onChange={(e) => setOpen({ ...open, details: { ...open.details, lastName: e.target.value } })} />
                </Field>
                <Field
                  label="Email"
                  htmlFor="employee-email"
                  className="sm:col-span-2"
                  hint={
                    open.kind === 'edit' && accountOf(open.employee)
                      ? `This does not change the address they sign in with, ${accountOf(open.employee)!.email}.`
                      : 'Needed to give them access.'
                  }
                >
                  <Input id="employee-email" type="email" maxLength={320} value={open.details.email}
                    onChange={(e) => setOpen({ ...open, details: { ...open.details, email: e.target.value } })} />
                </Field>
              </div>

              <div className="grid gap-4 border-t pt-5 sm:grid-cols-2">
                <PlacementPicker label="Department" id="employee-department" className="sm:col-span-2"
                  items={catalogs.departments} value={open.details.departmentId}
                  current={open.kind === 'edit' ? open.employee.department?.id ?? null : null}
                  onChange={(departmentId) => setOpen({ ...open, details: { ...open.details, departmentId } })} />
                <PlacementPicker label="Job title" id="employee-job-title"
                  items={catalogs.jobTitles} value={open.details.jobTitleId}
                  current={open.kind === 'edit' ? open.employee.jobTitle?.id ?? null : null}
                  onChange={(jobTitleId) => setOpen({ ...open, details: { ...open.details, jobTitleId } })} />
                <PlacementPicker label="Location" id="employee-location"
                  items={catalogs.locations} value={open.details.locationId}
                  current={open.kind === 'edit' ? open.employee.location?.id ?? null : null}
                  onChange={(locationId) => setOpen({ ...open, details: { ...open.details, locationId } })} />
              </div>

              {open.kind === 'add' && canInvite && (
                <div className="grid gap-4 border-t pt-5">
                  <CheckboxRow
                    className="-mx-3"
                    control={<Checkbox checked={open.invite} onChange={(e) => setOpen({ ...open, invite: e.target.checked })} />}
                    title="Send an invitation"
                    description="They get an email with a link to set their password, and the roles chosen below."
                  />
                  {open.invite && (
                    <RoleChecklist roles={roles} chosen={open.chosen} onToggle={(key) => setOpen({ ...open, chosen: toggled(open.chosen, key) })} />
                  )}
                </div>
              )}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Saving…' : open.kind === 'add' ? 'Add employee' : 'Save changes'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      {open?.kind === 'access' && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setOpen(null)}
          dismissible={false}
          title={`Give ${open.employee.displayName} access`}
          description={`An invitation goes to ${open.employee.email}, with a link to set their password.`}
          size="lg"
        >
          <form
            aria-label={`Give ${open.employee.displayName} access`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(event) => {
              event.preventDefault()
              void submit(async () => {
                await api(`/api/core/v1/employees/${open.employee.employeeId}/invite`, {
                  method: 'POST',
                  // The address shown here: the server refuses if it has changed since, so access
                  // is never sent somewhere the administrator did not see.
                  body: JSON.stringify({ roles: [...open.chosen], email: open.employee.email }),
                })
                setNotice({ tone: 'success', text: `An invitation is on its way to ${open.employee.email}.` })
              })
            }}
          >
            <DialogBody className="grid gap-5">
              {dialogError && <Alert tone="danger">{dialogError}</Alert>}
              <RoleChecklist roles={roles} chosen={open.chosen} onToggle={(key) => setOpen({ ...open, chosen: toggled(open.chosen, key) })} />
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Sending…' : 'Send invitation'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      <ConfirmDialog
        open={terminating !== null}
        onOpenChange={(isOpen) => !isOpen && setOpen(null)}
        title={`Terminate ${terminating?.displayName ?? ''}?`}
        description={
          terminatingAccount && terminatingAccount.status !== 'Deactivated'
            ? 'Their account is deactivated and they are signed out at once. They stay on record, behind “Show terminated”.'
            : 'They stay on record, behind “Show terminated”.'
        }
        confirmLabel="Terminate"
        confirmAccessibleName={terminating ? `Confirm: terminate ${terminating.displayName}` : undefined}
        busy={saving}
        onConfirm={() => terminating && void terminate(terminating)}
      />
    </PageStack>
  )
}

function detailsOf(e: EmployeeRow): Details {
  return {
    firstName: e.firstName,
    lastName: e.lastName,
    email: e.email ?? '',
    departmentId: e.department?.id ?? '',
    jobTitleId: e.jobTitle?.id ?? '',
    locationId: e.location?.id ?? '',
  }
}

/** "Engineer in Engineering › Platform at Head office", for the position cell's accessible name. */
function positionSentence(e: EmployeeRow): string {
  if (!e.jobTitle && !e.department && !e.location) return 'Not set'
  return [e.jobTitle?.name ?? 'No job title', e.department && `in ${e.department.name}`, e.location && `at ${e.location.name}`]
    .filter(Boolean)
    .join(' ')
}

/**
 * Picks a department, job title or location from its list: pick-only, never typed, so a typo cannot
 * become a new entry. Offers what is in use, plus the one the employee already has if it was retired.
 */
function PlacementPicker({
  label, id, items, value, current, onChange, className,
}: {
  label: string
  id: string
  items: readonly CatalogRow[]
  value: string
  current: string | null
  onChange: (id: string) => void
  className?: string
}) {
  return (
    <Field label={label} htmlFor={id} className={className}>
      <NativeSelect id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">None</option>
        {pickerOptions(items, current).map((o) => (
          <option key={o.id} value={o.id}>{o.label}</option>
        ))}
      </NativeSelect>
    </Field>
  )
}
