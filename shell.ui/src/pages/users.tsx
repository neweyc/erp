import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Send, UserCheck, UserRound, UserX } from 'lucide-react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import {
  Alert, Avatar, Badge, Button, Card, CheckboxRow, Checkbox, ConfirmDialog, DataTable, Dialog, DialogBody,
  DialogFooter, EmptyState, Loading, PageHeader, PageStack, type BadgeTone, type ColumnDef, type ColumnMeta,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { type RoleRow, roleGroups } from '../lib/roles'

interface UserRow {
  readonly userId: string
  readonly email: string
  readonly status: string
  /** Role keys: built-in (`ledger:controller`) or custom (`rol_…`). */
  readonly roles: readonly string[]
  /** For an account not yet accepted: its newest invitation. Null otherwise. */
  readonly invitation: Invitation | null
}

interface Invitation {
  readonly delivery: 'pending' | 'delivered' | 'failed' | 'unknown'
  readonly expiresAt: string
  readonly expired: boolean
}

/**
 * The status as a badge, plus, for an invitation, what an administrator needs to decide whether to
 * resend it. Together they make one sentence ("Invited: link expired"), which is the cell's
 * accessible name: what a screen reader hears, and what tests find it by.
 */
function describeStatus(user: UserRow): { label: string; tone: BadgeTone; detail: string | null } {
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

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  last_admin: 'Nobody else active could manage users after that change. Give someone else that permission first.',
  validation_failed: 'One of those roles cannot be given. Reload the page and try again.',
  not_found: 'That user no longer exists. The list has been reloaded.',
  cannot_deactivate_self: 'You cannot deactivate your own account. Ask another administrator.',
  not_invited: 'That person has already accepted, or their account is deactivated. The list has been reloaded.',
  employee_terminated: 'That person’s employee record is terminated, so they cannot be given access.',
  not_permitted: 'You do not have permission to do that.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

function explain(error: unknown): string {
  const code = error instanceof ApiError ? error.problemCode : 'unknown'
  return PROBLEMS[code] ?? 'Something went wrong. Please try again.'
}

/**
 * Accounts and their roles (docs/permissions.md). A user may hold any number of roles, built-in or
 * the organization's own. Anyone with core.user.read sees the page; only core.user.manage can change
 * anything, and the API enforces both.
 *
 * Editing opens a checklist for one person. Saving sends only what changed, as additions and
 * removals, so a role this page does not list (one for an app since unlicensed) is never touched,
 * and an administrator editing the same person at the same moment loses nothing.
 *
 * An account can also be deactivated (after a confirming dialog: it signs the person out at once),
 * reactivated, or, while invited, sent a fresh invitation link.
 */
export function Users() {
  const { session, refresh: refreshSession } = useSession()
  const [users, setUsers] = useState<readonly UserRow[]>([])
  const [roles, setRoles] = useState<readonly RoleRow[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<{ user: UserRow; chosen: ReadonlySet<string> } | null>(null)
  /** A failure while the roles dialog is open, shown inside it, so the choices made are not lost. */
  const [editError, setEditError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  /** The user whose Deactivate button was clicked: the confirming dialog is open for them. */
  const [confirmingDeactivate, setConfirmingDeactivate] = useState<UserRow | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [userRows, roleRows] = await Promise.all([
        api<UserRow[]>('/api/core/v1/users'),
        api<RoleRow[]>('/api/core/v1/roles'),
      ])
      setUsers(userRows)
      setRoles(roleRows)
    } catch (e) {
      setError(explain(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const canManage = session?.permissions.includes(Permissions.core.userManage) ?? false
  const myId = session?.userId

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
          </span>
        ),
      },
      {
        id: 'status',
        header: 'Status',
        accessorFn: (user) => user.status,
        meta: {
          cellLabel: (user) => {
            const { label, detail } = describeStatus(user)
            return detail ? `${label}: ${detail}` : label
          },
        } satisfies ColumnMeta<UserRow>,
        cell: ({ row: { original: user } }) => {
          const { label, tone, detail } = describeStatus(user)
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
            <Button
              size="sm"
              variant="outline"
              aria-label={`Edit roles for ${user.email}`}
              disabled={saving}
              onClick={() => {
                setEditError(null)
                setEditing({ user, chosen: new Set(user.roles) })
              }}
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
                onClick={() => setConfirmingDeactivate(user)}
              >
                <UserX /> Deactivate
              </Button>
            )}
          </span>
        ),
      },
    ]
    // `act` is not a dependency: it is recreated each render, but uses only state setters and
    // `load`, which never change, so the copy captured here behaves the same as a fresh one.
  }, [roles, canManage, myId, saving])

  if (!session) return null

  function toggle(key: string) {
    setEditing((current) => {
      if (!current) return current
      const chosen = new Set(current.chosen)
      if (chosen.has(key)) chosen.delete(key)
      else chosen.add(key)
      return { ...current, chosen }
    })
  }

  async function save() {
    if (!editing) return
    const { user, chosen } = editing
    // Only roles this page lists can have been toggled, so only they can be added or removed.
    const listed = roles.map((r) => r.key)
    const add = listed.filter((key) => chosen.has(key) && !user.roles.includes(key))
    const remove = listed.filter((key) => !chosen.has(key) && user.roles.includes(key))

    setSaving(true)
    setEditError(null)
    try {
      if (add.length > 0 || remove.length > 0) {
        await api(`/api/core/v1/users/${user.userId}/roles`, {
          method: 'PATCH',
          body: JSON.stringify({ add, remove }),
        })
      }
      setEditing(null)

      if (user.userId === session!.userId) {
        // Your own roles changed, so your session ends (role_changed) and you sign in again to a
        // shell that matches what you may now do.
        await refreshSession()
        return
      }
    } catch (e) {
      setEditError(explain(e))
    } finally {
      setSaving(false)
    }
    // Reloaded after a failure too, so the list behind the dialog shows what the server holds.
    await load()
  }

  /** Deactivate, reactivate or resend: one request, then the list again, which shows the result. */
  async function act(user: UserRow, path: 'deactivate' | 'reactivate' | 'invitation', done: string) {
    setSaving(true)
    setError(null)
    setNotice(null)
    setConfirmingDeactivate(null)
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

      {editing && (
        <Dialog
          open
          onOpenChange={(open) => !open && setEditing(null)}
          // Holds a part-made selection: a stray click outside must not discard it.
          dismissible={false}
          title={`Roles for ${editing.user.email}`}
          description="A person’s permissions are everything their roles allow together."
          size="lg"
        >
          <form
            aria-label={`Roles for ${editing.user.email}`}
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault()
              void save()
            }}
          >
            <DialogBody className="grid gap-5">
              {editError && <Alert tone="danger">{editError}</Alert>}
              {roleGroups(roles).map((group) => (
                <fieldset key={group.title} className="grid gap-1">
                  <legend className="mb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">
                    {group.title}
                  </legend>
                  {group.roles.map((role) => (
                    <CheckboxRow
                      key={role.key}
                      control={<Checkbox checked={editing.chosen.has(role.key)} onChange={() => toggle(role.key)} />}
                      title={role.name}
                      description={role.description || 'No description.'}
                    />
                  ))}
                </fieldset>
              ))}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Saving…' : 'Save roles'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}

      <ConfirmDialog
        open={confirmingDeactivate !== null}
        onOpenChange={(open) => !open && setConfirmingDeactivate(null)}
        title={`Deactivate ${confirmingDeactivate?.email ?? ''}?`}
        description="They are signed out at once and cannot sign in until reactivated. Their roles are kept."
        confirmLabel="Deactivate"
        confirmAccessibleName={confirmingDeactivate ? `Confirm: deactivate ${confirmingDeactivate.email}` : undefined}
        busy={saving}
        onConfirm={() =>
          confirmingDeactivate &&
          void act(confirmingDeactivate, 'deactivate', `${confirmingDeactivate.email} is deactivated and signed out.`)
        }
      />
    </PageStack>
  )
}
