import { useCallback, useEffect, useMemo, useState } from 'react'
import { CornerDownRight, Network, Pencil, Plus } from 'lucide-react'
import { api, Permissions } from '@app-platform/web-api'
import {
  Alert, Badge, Button, Card, Checkbox, CheckboxRow, DataTable, Dialog, DialogBody, DialogFooter, EmptyState,
  Field, Input, Loading, NativeSelect, PageHeader, PageStack, type ColumnDef, type ColumnMeta,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import { possibleParents, type DepartmentRow } from '../lib/catalogs'
import { explainCatalogError } from './catalog-page'

type Open =
  | { kind: 'add'; name: string; parentId: string }
  | { kind: 'edit'; row: DepartmentRow; name: string; parentId: string; active: boolean }

/**
 * The department tree. Departments nest (a parent each, at most 8 deep); the API refuses a loop, an
 * active department under an inactive one, and a name its siblings already use, and this page keeps
 * its pickers from offering those in the first place.
 *
 * Shown in tree order with sorting off: sorting by a column would scatter each branch. The search
 * box matches the whole path, so searching "Platform" finds "Engineering › Platform".
 */
export function Departments() {
  const { session } = useSession()
  const [rows, setRows] = useState<readonly DepartmentRow[]>([])
  const [loading, setLoading] = useState(true)
  const [open, setOpen] = useState<Open | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const canManage = session?.permissions.includes(Permissions.core.catalogManage) ?? false

  const load = useCallback(async () => {
    try {
      setRows(await api<DepartmentRow[]>('/api/core/v1/departments'))
    } catch (e) {
      setError(explainCatalogError(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const columns = useMemo<ColumnDef<DepartmentRow>[]>(() => {
    const columns: ColumnDef<DepartmentRow>[] = [
      {
        id: 'name',
        header: 'Department',
        accessorFn: (d) => d.path,
        enableSorting: false,
        // Heard as the path: the indent says it to the eye only.
        meta: { cellLabel: (d) => d.path } satisfies ColumnMeta<DepartmentRow>,
        cell: ({ row: { original: d } }) => (
          <span className="flex min-w-0 items-center gap-2" style={{ paddingLeft: `${(d.depth - 1) * 1.25}rem` }}>
            {d.depth > 1 && <CornerDownRight className="size-3.5 shrink-0 text-muted-foreground" />}
            <span className={d.depth === 1 ? 'font-medium' : undefined}>{d.name}</span>
          </span>
        ),
      },
      {
        id: 'status',
        header: 'Status',
        accessorFn: (d) => (d.active ? 'Active' : 'Inactive'),
        enableSorting: false,
        cell: ({ row: { original: d } }) => <Badge tone={d.active ? 'success' : 'neutral'}>{d.active ? 'Active' : 'Inactive'}</Badge>,
      },
    ]
    if (canManage) {
      columns.push({
        id: 'actions',
        header: () => <span className="sr-only">Actions</span>,
        enableSorting: false,
        meta: { className: 'text-right' },
        cell: ({ row: { original: d } }) => (
          <span className="flex justify-end gap-1.5">
            {d.active && (
              <Button size="sm" variant="ghost" aria-label={`Add a department inside ${d.path}`} disabled={saving}
                onClick={() => show({ kind: 'add', name: '', parentId: d.departmentId })}>
                <Plus /> Add inside
              </Button>
            )}
            <Button size="sm" variant="ghost" aria-label={`Edit ${d.path}`} disabled={saving}
              onClick={() => show({ kind: 'edit', row: d, name: d.name, parentId: d.parentId ?? '', active: d.active })}>
              <Pencil /> Edit
            </Button>
          </span>
        ),
      })
    }
    return columns
    // `show` uses only state setters, which never change.
  }, [canManage, saving])

  if (!session) return null

  function show(dialog: Open) {
    setDialogError(null)
    setOpen(dialog)
  }

  async function save(dialog: Open) {
    setSaving(true)
    setDialogError(null)
    const parentId = dialog.parentId || null
    try {
      if (dialog.kind === 'add') {
        await api('/api/core/v1/departments', { method: 'POST', body: JSON.stringify({ name: dialog.name, parentId }) })
        setNotice(`${dialog.name.trim()} is added.`)
      } else {
        await api(`/api/core/v1/departments/${dialog.row.departmentId}`, {
          method: 'PUT',
          body: JSON.stringify({ name: dialog.name, parentId, active: dialog.active }),
        })
        setNotice(`${dialog.name.trim()} is saved.`)
      }
      setOpen(null)
    } catch (e) {
      setDialogError(explainCatalogError(e))
    } finally {
      setSaving(false)
    }
    await load()
  }

  if (loading) return <Loading>Loading departments…</Loading>

  const label = open?.kind === 'edit' ? `Edit ${open.row.path}` : 'Add department'
  // A department being edited keeps its current parent on offer even if that parent was retired,
  // so saving a rename does not silently move it.
  const parents = open
    ? possibleParents(rows, open.kind === 'edit' ? open.row.departmentId : null)
    : []
  const currentParent = open?.kind === 'edit' ? rows.find((d) => d.departmentId === open.row.parentId) : undefined
  if (currentParent && !parents.includes(currentParent)) parents.unshift(currentParent)

  return (
    <PageStack>
      <PageHeader
        title="Departments"
        description="How the organization is divided. Departments can sit inside one another."
        actions={canManage && (
          <Button variant="primary" onClick={() => show({ kind: 'add', name: '', parentId: '' })}>
            <Plus /> Add department
          </Button>
        )}
      />

      {error && <Alert tone="danger">{error}</Alert>}
      {notice && <Alert tone="success">{notice}</Alert>}

      <Card>
        <DataTable
          label="Departments"
          columns={columns}
          data={rows}
          getRowId={(d) => d.departmentId}
          filterLabel="Search departments"
          empty={
            <EmptyState icon={Network} title="No departments yet">
              {canManage ? 'Add the first with “Add department”.' : 'Nobody has added any departments.'}
            </EmptyState>
          }
        />
      </Card>

      {open && (
        <Dialog open onOpenChange={(isOpen) => !isOpen && setOpen(null)} dismissible={false} title={label}>
          <form aria-label={label} className="flex min-h-0 flex-1 flex-col"
            onSubmit={(event) => {
              event.preventDefault()
              void save(open)
            }}>
            <DialogBody className="grid gap-5">
              {dialogError && <Alert tone="danger">{dialogError}</Alert>}
              <Field label="Name" htmlFor="department-name"
                hint={open.kind === 'edit' ? 'A new name shows for everyone in it at once.' : undefined}>
                <Input id="department-name" required maxLength={100} value={open.name}
                  onChange={(e) => setOpen({ ...open, name: e.target.value })} />
              </Field>
              <Field label="Inside" htmlFor="department-parent" hint="Leave as “Top level” for a department that sits inside no other.">
                <NativeSelect id="department-parent" value={open.parentId}
                  onChange={(e) => setOpen({ ...open, parentId: e.target.value })}>
                  <option value="">Top level</option>
                  {parents.map((d) => (
                    <option key={d.departmentId} value={d.departmentId}>
                      {d.active ? d.path : `${d.path} (inactive)`}
                    </option>
                  ))}
                </NativeSelect>
              </Field>
              {open.kind === 'edit' && (
                <CheckboxRow
                  className="-mx-3"
                  control={<Checkbox checked={open.active} onChange={(e) => setOpen({ ...open, active: e.target.checked })} />}
                  title="In use"
                  description="Clear this to retire it. People in it stay in it; it is no longer offered for anyone else."
                />
              )}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>Cancel</Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Saving…' : open.kind === 'add' ? 'Add department' : 'Save changes'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}
    </PageStack>
  )
}
