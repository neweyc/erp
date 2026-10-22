import { useState } from 'react'
import { Plus } from 'lucide-react'
import {
  Badge, Button, Card, CardContent, CardHeader, Field, Input, NativeSelect, Table, TableBody, TableCell, TableHead,
  TableHeader, TableRow, type BadgeTone,
} from '@app-platform/ui-kit'
import type { Account, AccountType } from './api'

const TYPES: readonly AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense']

/** A color per account type, so the chart reads at a glance. */
const TONE: Record<AccountType, BadgeTone> = {
  Asset: 'info', Liability: 'warning', Equity: 'brand', Revenue: 'success', Expense: 'neutral',
}

export function AccountsSection({
  accounts, onCreate,
}: {
  accounts: readonly Account[]
  /** Null when the user may not add accounts (ledger.account.create): the form is not shown. */
  onCreate: ((code: string, name: string, type: AccountType) => Promise<boolean>) | null
}) {
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [type, setType] = useState<AccountType>('Asset')
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)

    // Cleared only on success, so a refused account keeps what was typed.
    if (onCreate && (await onCreate(code.trim(), name.trim(), type))) {
      setCode('')
      setName('')
    }

    setSubmitting(false)
  }

  return (
    <Card>
      <section aria-labelledby="accounts-heading">
        <CardHeader id="accounts-heading" title="Chart of accounts" />

        {accounts.length === 0 ? (
          <CardContent>
            <p className="text-sm text-muted-foreground">
              No accounts yet.{onCreate && ' Add the first one below — nothing can be posted until there are two.'}
            </p>
          </CardContent>
        ) : (
          <div className="pt-4">
            <Table aria-label="Accounts">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <TableHead scope="col">Code</TableHead>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Type</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {accounts.map((a) => (
                  <TableRow key={a.accountId}>
                    <TableCell className="font-mono text-xs text-muted-foreground tabular-nums">{a.code}</TableCell>
                    <TableCell className="font-medium">{a.name}</TableCell>
                    <TableCell>
                      <Badge tone={TONE[a.type]}>{a.type}</Badge>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}

        {onCreate && (
          <form onSubmit={submit} aria-label="Add account" className="grid gap-3 border-t px-5 py-4 sm:px-6">
            <div className="grid grid-cols-[6rem_minmax(0,1fr)] gap-2">
              <Field label="Code" htmlFor="account-code">
                <Input id="account-code" value={code} onChange={(e) => setCode(e.target.value)} required maxLength={20} />
              </Field>
              <Field label="Name" htmlFor="account-name">
                <Input id="account-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
              </Field>
            </div>
            <div className="flex items-end gap-2">
              <Field label="Type" htmlFor="account-type" className="flex-1">
                <NativeSelect id="account-type" value={type} onChange={(e) => setType(e.target.value as AccountType)}>
                  {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </NativeSelect>
              </Field>
              <Button type="submit" disabled={submitting || code.trim() === '' || name.trim() === ''}>
                <Plus /> Add account
              </Button>
            </div>
          </form>
        )}
      </section>
    </Card>
  )
}
