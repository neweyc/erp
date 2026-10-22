import { useCallback, useEffect, useMemo, useState, type ComponentType } from 'react'
import { Pencil, Plus } from 'lucide-react'
import { api, ApiError, Permissions } from '@app-platform/web-api'
import {
  Alert, Badge, Button, Card, Checkbox, CheckboxRow, DataTable, Dialog, DialogBody, DialogFooter, EmptyState,
  Field, Input, Loading, PageHeader, PageStack, type ColumnDef,
} from '@app-platform/ui-kit'
import { useSession } from '../components/session-provider'
import type { CatalogRow } from '../lib/catalogs'

/** Keyed on the server's stable problemCode, never its message. */
const PROBLEMS: Record<string, string> = {
  catalog_name_taken: 'That name is already in the list. If it was deactivated, reactivate it instead.',
  not_found: 'That is no longer in the list. The list has been reloaded.',
  not_permitted: 'You do not have permission to do that.',
  csrf_failed: 'Your session needs refreshing. Reload the page and try again.',
}

export function explainCatalogError(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.'
  // A validation failure's message says what to fix ("A name is required.", "… is inactive").
  if (error.problemCode === 'validation_failed' && error.message) return error.message
  return PROBLEMS[error.problemCode] ?? 'Something went wrong. Please try again.'
}

/** What differs between the job titles page and the locations page. */
export interface CatalogKind {
  /** "Job titles". */
  readonly title: string
  /** "job title": in sentences. */
  readonly noun: string
  readonly description: string
  /** "/api/core/v1/job-titles". */
  readonly route: string
  /** The id field in the API's rows: "jobTitleId". */
  readonly idField: string
  readonly icon: ComponentType<{ className?: string }>
}

type Open = { kind: 'add'; name: string } | { kind: 'edit'; row: CatalogRow; name: string; active: boolean }

/**
 * A flat catalog: job titles or locations. Everyone who can see the roster can read it; adding,
 * renaming and deactivating need core.catalog.manage, and the API enforces it.
 *
 * Nothing is ever deleted. A retired entry stays with the people who have it and is no longer
 * offered, which is why the list shows it with its status rather than hiding it.
 */
export function CatalogPage({ kind }: { kind: CatalogKind }) {
  const { session } = useSession()
  const [rows, setRows] = useState<readonly CatalogRow[]>([])
  const [loading, setLoading] = useState(true)
  const [open, setOpen] = useState<Open | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const canManage = session?.permissions.includes(Permissions.core.catalogManage) ?? false

  const load = useCallback(async () => {
    try {
      const raw = await api<Record<string, unknown>[]>(kind.route)
      setRows(raw.map((r) => ({ id: String(r[kind.idField]), name: String(r.name), active: Boolean(r.active) })))
    } catch (e) {
      setError(explainCatalogError(e))
    } finally {
      setLoading(false)
    }
  }, [kind.route, kind.idField])

  useEffect(() => {
    void load()
  }, [load])

  const columns = useMemo<ColumnDef<CatalogRow>[]>(() => {
    const columns: ColumnDef<CatalogRow>[] = [
      { id: 'name', header: 'Name', accessorFn: (r) => r.name, cell: ({ row: { original: r } }) => <span className="font-medium">{r.name}</span> },
      {
        id: 'status',
        header: 'Status',
        accessorFn: (r) => (r.active ? 'Active' : 'Inactive'),
        cell: ({ row: { original: r } }) => <Badge tone={r.active ? 'success' : 'neutral'}>{r.active ? 'Active' : 'Inactive'}</Badge>,
      },
    ]
    if (canManage) {
      columns.push({
        id: 'actions',
        header: () => <span className="sr-only">Actions</span>,
        enableSorting: false,
        meta: { className: 'text-right' },
        cell: ({ row: { original: r } }) => (
          <Button size="sm" variant="ghost" aria-label={`Edit ${r.name}`} disabled={saving}
            onClick={() => show({ kind: 'edit', row: r, name: r.name, active: r.active })}>
            <Pencil /> Edit
          </Button>
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
    try {
      if (dialog.kind === 'add') {
        await api(kind.route, { method: 'POST', body: JSON.stringify({ name: dialog.name }) })
        setNotice(`${dialog.name.trim()} is added.`)
      } else {
        await api(`${kind.route}/${dialog.row.id}`, { method: 'PUT', body: JSON.stringify({ name: dialog.name, active: dialog.active }) })
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

  if (loading) return <Loading>Loading {kind.title.toLowerCase()}…</Loading>

  const Icon = kind.icon
  const label = open?.kind === 'edit' ? `Edit ${open.row.name}` : `Add ${kind.noun}`

  return (
    <PageStack>
      <PageHeader
        title={kind.title}
        description={kind.description}
        actions={canManage && (
          <Button variant="primary" onClick={() => show({ kind: 'add', name: '' })}>
            <Plus /> Add {kind.noun}
          </Button>
        )}
      />

      {error && <Alert tone="danger">{error}</Alert>}
      {notice && <Alert tone="success">{notice}</Alert>}

      <Card>
        <DataTable
          label={kind.title}
          columns={columns}
          data={rows}
          getRowId={(r) => r.id}
          filterLabel={`Search ${kind.title.toLowerCase()}`}
          empty={
            <EmptyState icon={Icon} title={`No ${kind.title.toLowerCase()} yet`}>
              {canManage ? `Add the first with “Add ${kind.noun}”.` : `Nobody has added any ${kind.title.toLowerCase()}.`}
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
              <Field label="Name" htmlFor="catalog-name"
                hint={open.kind === 'edit' ? 'A new name shows for everyone who has it at once.' : undefined}>
                <Input id="catalog-name" required maxLength={100} value={open.name}
                  onChange={(e) => setOpen({ ...open, name: e.target.value })} />
              </Field>
              {open.kind === 'edit' && (
                <CheckboxRow
                  className="-mx-3"
                  control={<Checkbox checked={open.active} onChange={(e) => setOpen({ ...open, active: e.target.checked })} />}
                  title="In use"
                  description={`Clear this to retire it. People who have it keep it; it is no longer offered for anyone else.`}
                />
              )}
            </DialogBody>
            <DialogFooter>
              <Button disabled={saving} onClick={() => setOpen(null)}>Cancel</Button>
              <Button type="submit" variant="primary" disabled={saving}>
                {saving ? 'Saving…' : open.kind === 'add' ? `Add ${kind.noun}` : 'Save changes'}
              </Button>
            </DialogFooter>
          </form>
        </Dialog>
      )}
    </PageStack>
  )
}
