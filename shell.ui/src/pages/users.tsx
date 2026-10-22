import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Send, UserCheck, UserPlus, UserRound, UserX } from 'lucide-react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import {
  Alert, Avatar, Badge, Button, Card, ConfirmDialog, DataTable, Dialog, DialogBody, DialogFooter, EmptyState, Field,
  Input, Loading, NativeSelect, PageHeader, PageStack, type ColumnDef, type ColumnMeta,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { RoleChecklist, toggled } from '../components/role-checklist'
import { accountSentence, describeAccount, type EmployeeRow, type UserRow } from '../lib/people'
import type { RoleRow } from '../lib/roles'

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  last_admin: 'Nobody else active could manage users after that change. Give someone else that permission first.',
  validation_failed: 'One of those roles cannot be given. Reload the page and try again.',
  not_found: 'That person no longer exists. The list has been reloaded.',
  cannot_deactivate_self: 'You cannot deactivate your own account. Ask another administrator.',
  not_invited: 'That person has already accepted, or their account is deactivated. The list has been reloaded.',
  employee_terminated: 'That person’s employee record is terminated, so they cannot be given access.',
  employee_has_account: 'That employee already has an account. The list has been reloaded.',
  employee_changed: 'Their email address changed while this was open, so nothing was sent. Close this, check the address in the reloaded list, and try again.',
  email_in_use: 'An account or employee already uses that email address.',
  user_has_employee: 'That account is already on the roster. The list has been reloaded.',
  not_permitted: 'You do not have permission to do that.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.'
  // A validation failure about a name says which field; that beats the generic sentence.
  if (error.problemCode === 'validation_failed' && error.message && /name/i.test(error.message)) return error.message
  return PROBLEMS[error.problemCode] ?? 'Something went wrong. Please try again.'
}

/** The dialog open on this page, if any. One at a time, so one state says which. */
type Open =
  | { kind: 'roles'; user: UserRow; chosen: ReadonlySet<string> }
  | { kind: 'invite'; employeeId: string; chosen: ReadonlySet<string> }
  | { kind: 'roster'; user: UserRow; firstName: string; lastName: string }
  | { kind: 'deactivate'; user: UserRow }

/**
 * Accounts and their roles (docs/permissions.md). A user may hold any number of roles, built-in or
 * the organization's own. Anyone with core.user.read sees the page; only core.user.manage can change
 * anything, and the API enforces both.
 *
 * Editing roles sends only what changed, as additions and removals, so a role this page does not
 * list (one for an app since unlicensed) is never touched, and an administrator editing the same
 * person at the same moment loses nothing.
 *
 * Everyone with an account is an employee, so inviting picks someone from the roster; an account
 * not on it (the administrator made at provisioning) can be given a record here.
 */
export function Users() {
  const { session, refresh: refreshSession } = useSession()
  const [users, setUsers] = useState<readonly UserRow[]>([])
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  /** Only fetched for an administrator who can read the roster: it feeds "Invite someone". */
  const [employees, setEmployees] = useState<readonly EmployeeRow[]>([])
  const [loading, setLoading] = useState(true)
  const [open, setOpen] = useState<Open | null>(null)
  /** A failure while a form dialog is open, shown inside it, so what was entered is not lost. */
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const canManage = session?.permissions.includes(Permissions.core.userManage) ?? false
  const canReadRoster = session?.permissions.includes(Permissions.core.employeeRead) ?? false
  const myId = session?.userId

  const load = useCallback(async () => {
    try {
      const [userRows, roleRows, employeeRows] = await Promise.all([
        api<UserRow[]>('/api/core/v1/users'),
        api<RoleRow[]>('/api/core/v1/roles'),
        canManage && canReadRoster ? api<EmployeeRow[]>('/api/core/v1/employees') : Promise.resolve([]),
      ])
      setUsers(userRows)
      setRoles(roleRows)
      setEmployees(employeeRows)
    } catch (e) {
      setError(explain(e))
    } finally {
      setLoading(false)
    }
  }, [canManage, canReadRoster])

  useEffect(() => {
    void load()
  }, [load])

  /** Who can be invited: on the roster, not terminated, with an address, and no account yet. */
  const invitable = useMemo(() => {
    const linked = new Set(users.map((u) => u.employeeId))
    return employees.filter((e) => e.status !== 'Terminated' && e.email && !linked.has(e.employeeId))
  }, [users, employees])

  const columns = useMemo<ColumnDef<UserRow>[]>(() => {
    const nameOf = (key: string) => roles.find((r) => r.key === key)?.name
    const roleNames = (user: UserRow) =>
      user.roles.map(nameOf).filter((name): name is string => name !== undefined)

    const base: ColumnDef<UserRow>[] = [
      {
        id: 'email',
        header: 'Person',
        accessorFn: (user) => user.email,
        cell: ({ row: { original: user } }) => (
          <span className="flex min-w-0 items-center gap-3">
            <Avatar name={user.email} />
            <span className="truncate font-medium">{user.email}</span>
            {user.userId === myId && <Badge tone="brand">You</Badge>}
            {user.employeeId === null && <Badge tone="warning">Not on the roster</Badge>}
          </span>
        ),
      },
      {
        id: 'status',
        header: 'Status',
        accessorFn: (user) => user.status,
        meta: { cellLabel: accountSentence } satisfies ColumnMeta<UserRow>,
        cell: ({ row: { original: user } }) => {
          const { label, tone, detail } = describeAccount(user)
          return (
            <span className="grid justify-items-start gap-1">
              <Badge tone={tone} dot>
                {label}
              </Badge>
              {detail && <span className="text-xs whitespace-nowrap text-muted-foreground first-letter:uppercase">{detail}</span>}
            </span>
          )
        },
      },
      {
        id: 'roles',
        header: 'Roles',
        accessorFn: (user) => roleNames(user).join(', '),
        meta: { cellLabel: (user) => roleNames(user).join(', ') || 'No roles' } satisfies ColumnMeta<UserRow>,
        cell: ({ row: { original: user } }) => {
          const names = roleNames(user)
          if (names.length === 0) return <span className="text-muted-foreground">No roles</span>
          return (
            <span className="flex flex-wrap gap-1.5">
              {names.map((name) => (
                <Badge key={name} tone="neutral" className="text-foreground/80">
                  {name}
                </Badge>
              ))}
            </span>
          )
        },
      },
    ]

    if (!canManage) return base

    return [
      ...base,
      {
        id: 'actions',
        header: () => <span className="sr-only">Actions</span>,
        enableSorting: false,
        meta: { className: 'text-right' },
        cell: ({ row: { original: user } }) => (
          <span className="flex justify-end gap-1.5">
            {user.employeeId === null && canReadRoster && (
              <Button
                size="sm"
                variant="ghost"
                aria-label={`Add ${user.email} to the roster`}
                disabled={saving}
                onClick={() => show({ kind: 'roster', user, firstName: '', lastName: '' })}
              >
                <UserPlus /> Add to roster
              </Button>
            )}
            <Button
              size="sm"
              variant="outline"
              aria-label={`Edit roles for ${user.email}`}
              disabled={saving}
              onClick={() => show({ kind: 'roles', user, chosen: new Set(user.roles) })}
            >
              Edit roles
            </Button>
            {user.status === 'Invited' && (
              <Button
                size="sm"
                variant="ghost"
                aria-label={`Resend invitation to ${user.email}`}
                disabled={saving}
                onClick={() => void act(user, 'invitation', `A new invitation is on its way to ${user.email}.`)}
              >
                <Send /> Resend
              </Button>
            )}
            {user.status === 'Deactivated' ? (
              <Button
                size="sm"
                variant="ghost"
                aria-label={`Reactivate ${user.email}`}
                disabled={saving}
                onClick={() => void act(user, 'reactivate', `${user.email} is reactivated.`)}
              >
                <UserCheck /> Reactivate
              </Button>
            ) : user.userId === myId ? null : (
              <Button
                size="sm"
                variant="ghost"
                // Quiet until pointed at: a red word on every row shouts about a rare action.
                className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                aria-label={`Deactivate ${user.email}`}
                disabled={saving}
                onClick={() => setOpen({ kind: 'deactivate', user })}
              >
                <UserX /> Deactivate
              </Button>
            )}
          </span>
        ),
      },
    ]
    // `act` and `show` are not dependencies: they are recreated each render, but use only state
    // setters and `load`, which never change, so the copies captured here behave the same.
  }, [roles, canManage, canReadRoster, myId, saving])

  if (!session) return null

  function show(dialog: Open) {
    setDialogError(null)
    setOpen(dialog)
  }

  /**
   * Runs one write for an open form dialog. On success the dialog closes and the page reloads; on
   * failure the dialog stays open with the reason, and the list behind it is reloaded anyway, so it
   * shows what the server holds rather than what was attempted.
   */
  async function submit(request: () => Promise<unknown>, done: string | null, endsMySession = false) {
    setSaving(true)
    setDialogError(null)
    try {
      await request()
      setOpen(null)
      if (done) setNotice(done)
      if (endsMySession) {
        // Your own roles changed, so your session ends (role_changed) and you sign in again to a
        // shell that matches what you may now do.
        await refreshSession()
        return
      }
    } catch (e) {
      setDialogError(explain(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  function saveRoles(user: UserRow, chosen: ReadonlySet<string>) {
    // Only roles this page lists can have been toggled, so only they can be added or removed.
    const listed = roles.map((r) => r.key)
    const add = listed.filter((key) => chosen.has(key) && !user.roles.includes(key))
    const remove = listed.filter((key) => !chosen.has(key) && user.roles.includes(key))

    void submit(
      async () => {
        if (add.length > 0 || remove.length > 0) {
          await api(`/api/core/v1/users/${user.userId}/roles`, { method: 'PATCH', body: JSON.stringify({ add, remove }) })
        }
      },
      null,
      user.userId === session!.userId,
    )
  }

  /** Deactivate, reactivate or resend: one request, then the list again, which shows the result. */
  async function act(user: UserRow, path: 'deactivate' | 'reactivate' | 'invitation', done: string) {
    setSaving(true)
    setError(null)
    setNotice(null)
    setOpen(null)
    try {
      await api(`/api/core/v1/users/${user.userId}/${path}`, { method: 'POST' })
      setNotice(done)
    } catch (e) {
      setError(explain(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  if (loading) return <Loading>Loading users…</Loading>

  const inviteTarget = open?.kind === 'invite' ? employees.find((e) => e.employeeId === open.employeeId) : undefined

  return (
    <PageStack>
      <PageHeader
        title="Users"
        description={
          <>
            Everyone with an account in {session.tenantName}, and the roles they hold. What each role
            allows is on the{' '}
            <Link to="/roles" className="font-medium text-primary underline-offset-4 hover:underline">
              Roles
            </Link>{' '}
            page.
          </>
        }
        actions={
          canManage &&
          canReadRoster && (
            <Button
              variant="primary"
              onClick={() =>
                show({ kind: 'invite', employeeId: invitable[0]?.employeeId ?? '', chosen: new Set(['core:member']) })
              }
            >
              <Send /> Invite someone
            </Button>
          )
        }
      />

      {error && <Alert tone="danger">{error}</Alert>}
      {notice && <Alert tone="success">{notice}</Alert>}

      <Card>
        <DataTable
          label="Users"
          columns={columns}
          data={users}
          getRowId={(user) => user.userId}
          filterLabel="Search users"
          empty={<EmptyState icon={UserRound} title="No users yet" />}
        />
      </Card>

      {open?.kind === 'roles' && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setOpen(null)}
          // Holds a part-made selection: a stray click outside must not discard it.
          dismissible={false}
          title={`Roles for ${open.user.email}`}
          description="A person’s permissions are everything their roles allow together."
          size="lg"
        >
          <form
            aria-label={`Roles for ${open.user.email}`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault()
              saveRoles(open.user, open.chosen)
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
                {saving ? 'Saving…' : 'Save roles'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      {open?.kind === 'invite' && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setOpen(null)}
          dismissible={false}
          title="Invite someone"
          description="Invitations go to people on the employee roster, at the address it holds for them."
          size="lg"
        >
          <form
            aria-label="Invite someone"
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault()
              if (!inviteTarget) return
              void submit(
                () =>
                  api(`/api/core/v1/employees/${inviteTarget.employeeId}/invite`, {
                    method: 'POST',
                    // The address shown: the server refuses if it has changed since (employee_changed).
                    body: JSON.stringify({ roles: [...open.chosen], email: inviteTarget.email }),
                  }),
                `An invitation is on its way to ${inviteTarget.email}.`,
              )
            }}
          >
            <DialogBody className="grid gap-5">
              {dialogError && <Alert tone="danger">{dialogError}</Alert>}
              {invitable.length === 0 ? (
                <Alert tone="info">
                  Everyone on the roster with an email address already has an account.
                </Alert>
              ) : (
                <Field
                  label="Person"
                  htmlFor="invite-employee"
                  hint={inviteTarget ? `The invitation goes to ${inviteTarget.email}.` : undefined}
                >
                  <NativeSelect
                    id="invite-employee"
                    value={open.employeeId}
                    onChange={(e) => setOpen({ ...open, employeeId: e.target.value })}
                  >
                    {invitable.map((employee) => (
                      <option key={employee.employeeId} value={employee.employeeId}>
                        {employee.displayName} ({employee.email})
                      </option>
                    ))}
                  </NativeSelect>
                </Field>
              )}
              <p className="text-sm text-muted-foreground">
                Not listed? Add them on the{' '}
                <Link to="/employees" className="font-medium text-primary underline-offset-4 hover:underline">
                  Employees
                </Link>{' '}
                page first, with their email address.
              </p>
              {invitable.length > 0 && (
                <RoleChecklist roles={roles} chosen={open.chosen} onToggle={(key) => setOpen({ ...open, chosen: toggled(open.chosen, key) })} />
              )}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving || !inviteTarget}>
                {saving ? 'Sending…' : 'Send invitation'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      {open?.kind === 'roster' && (
        <Dialog
          open
          onOpenChange={(isOpen) => !isOpen && setOpen(null)}
          dismissible={false}
          title={`Add ${open.user.email} to the roster`}
          description="Creates their employee record, with this email address, and links their account to it."
        >
          <form
            aria-label={`Add ${open.user.email} to the roster`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault()
              void submit(
                () =>
                  api(`/api/core/v1/users/${open.user.userId}/employee`, {
                    method: 'POST',
                    body: JSON.stringify({ firstName: open.firstName, lastName: open.lastName }),
                  }),
                `${open.user.email} is on the roster.`,
              )
            }}
          >
            <DialogBody className="grid gap-4 sm:grid-cols-2">
              {dialogError && <Alert tone="danger" className="sm:col-span-2">{dialogError}</Alert>}
              <Field label="First name" htmlFor="roster-first">
                <Input id="roster-first" required maxLength={100} value={open.firstName}
                  onChange={(e) => setOpen({ ...open, firstName: e.target.value })} />
              </Field>
              <Field label="Last name" htmlFor="roster-last">
                <Input id="roster-last" required maxLength={100} value={open.lastName}
                  onChange={(e) => setOpen({ ...open, lastName: e.target.value })} />
              </Field>
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Adding…' : 'Add to roster'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      <ConfirmDialog
        open={open?.kind === 'deactivate'}
        onOpenChange={(isOpen) => !isOpen && setOpen(null)}
        title={`Deactivate ${open?.kind === 'deactivate' ? open.user.email : ''}?`}
        description="They are signed out at once and cannot sign in until reactivated. Their roles are kept."
        confirmLabel="Deactivate"
        confirmAccessibleName={open?.kind === 'deactivate' ? `Confirm: deactivate ${open.user.email}` : undefined}
        busy={saving}
        onConfirm={() =>
          open?.kind === 'deactivate' &&
          void act(open.user, 'deactivate', `${open.user.email} is deactivated and signed out.`)
        }
      />
    </PageStack>
  )
}
