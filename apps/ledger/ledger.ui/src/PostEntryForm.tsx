import { useState } from 'react'
import { CircleCheck, Plus, TriangleAlert, X } from 'lucide-react'
import {
  Button, Card, CardHeader, cn, Field, Input, NativeSelect, numeric, Table, TableBody, TableCell, TableFooter,
  TableHead, TableHeader, TableRow,
} from '@app-platform/ui-kit'
import type { Account, NewLine } from './api'
import { fromMinor, toMinor } from './money'
import { today } from './dates'

/** One editable line: an account and EITHER a debit or a credit, as typed. */
interface DraftLine {
  readonly accountId: string
  readonly debit: string
  readonly credit: string
}

const blank: DraftLine = { accountId: '', debit: '', credit: '' }

/**
 * A line's signed amount in minor units — debit positive, credit negative, the sign rule the server
 * posts with — or null while the line is incomplete or ambiguous (both columns filled, or neither).
 */
export function lineAmount(line: DraftLine): number | null {
  const debit = line.debit.trim() === '' ? null : toMinor(line.debit)
  const credit = line.credit.trim() === '' ? null : toMinor(line.credit)

  if (line.accountId === '') return null
  if (debit !== null && line.credit.trim() === '') return debit
  if (credit !== null && line.debit.trim() === '') return -credit
  return null
}

export function PostEntryForm({
  accounts, onPost,
}: {
  accounts: readonly Account[]
  onPost: (date: string, memo: string, currency: string, lines: readonly NewLine[], key: string) => Promise<boolean>
}) {
  const [entryDate, setEntryDate] = useState(today)
  const [memo, setMemo] = useState('')
  const [currency, setCurrency] = useState('USD')
  const [lines, setLines] = useState<DraftLine[]>([blank, blank])
  const [submitting, setSubmitting] = useState(false)

  // One key per filled-in form: a retry of the SAME submission sends the same key, so the server
  // returns the entry it already posted instead of posting twice. A new key only after success.
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID())

  const amounts = lines.map(lineAmount)
  const complete = amounts.every((a) => a !== null)
  const debits = amounts.reduce<number>((sum, a) => sum + Math.max(a ?? 0, 0), 0)
  const credits = amounts.reduce<number>((sum, a) => sum + Math.max(-(a ?? 0), 0), 0)
  // The same rule the server and the database enforce, shown before the person presses Post.
  const balanced = complete && lines.length >= 2 && debits === credits && debits > 0

  function update(index: number, change: Partial<DraftLine>) {
    setLines((current) => current.map((line, i) => (i === index ? { ...line, ...change } : line)))
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)

    const posted = await onPost(
      entryDate, memo.trim(), currency.trim(),
      lines.map((line, i) => ({ accountId: line.accountId, amountMinor: amounts[i]! })),
      idempotencyKey)

    // Kept on failure, so the person can correct and resubmit what they typed.
    if (posted) {
      setMemo('')
      setLines([blank, blank])
      setIdempotencyKey(crypto.randomUUID())
    }

    setSubmitting(false)
  }

  return (
    <Card>
      <section aria-labelledby="post-heading">
        <CardHeader
          id="post-heading"
          title="Post an entry"
          description="Each line is a debit or a credit; the entry posts only when they balance."
        />

        <form onSubmit={submit} aria-label="Post an entry" className="grid gap-5 pt-5">
          <div className="grid gap-4 px-5 sm:grid-cols-[10rem_minmax(0,1fr)_6rem] sm:px-6">
            <Field label="Date" htmlFor="entry-date">
              <Input id="entry-date" type="date" value={entryDate} onChange={(e) => setEntryDate(e.target.value)} required />
            </Field>
            <Field label="Memo" htmlFor="entry-memo">
              <Input id="entry-memo" placeholder="What this entry records" value={memo}
                onChange={(e) => setMemo(e.target.value)} required maxLength={500} />
            </Field>
            <Field label="Currency" htmlFor="entry-currency">
              <Input id="entry-currency" className="uppercase" value={currency}
                onChange={(e) => setCurrency(e.target.value.toUpperCase())} required maxLength={3} />
            </Field>
          </div>

          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead scope="col">Account</TableHead>
                <TableHead scope="col" className={cn(numeric, 'w-36')}>Debit</TableHead>
                <TableHead scope="col" className={cn(numeric, 'w-36')}>Credit</TableHead>
                <TableHead scope="col" className="w-12"><span className="sr-only">Remove</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {lines.map((line, i) => (
                <TableRow key={i} className="hover:bg-transparent">
                  <TableCell className="py-2">
                    <NativeSelect aria-label={`Line ${i + 1} account`} value={line.accountId} className="min-w-48"
                      onChange={(e) => update(i, { accountId: e.target.value })}>
                      <option value="">Choose an account…</option>
                      {accounts.map((a) => <option key={a.accountId} value={a.accountId}>{a.code} — {a.name}</option>)}
                    </NativeSelect>
                  </TableCell>
                  <TableCell className="py-2">
                    <Input aria-label={`Line ${i + 1} debit`} inputMode="decimal" placeholder="0.00"
                      className="min-w-24 text-right tabular-nums" value={line.debit}
                      onChange={(e) => update(i, { debit: e.target.value })} />
                  </TableCell>
                  <TableCell className="py-2">
                    <Input aria-label={`Line ${i + 1} credit`} inputMode="decimal" placeholder="0.00"
                      className="min-w-24 text-right tabular-nums" value={line.credit}
                      onChange={(e) => update(i, { credit: e.target.value })} />
                  </TableCell>
                  <TableCell className="py-2">
                    {lines.length > 2 && (
                      <Button variant="ghost" size="icon" className="size-8 text-muted-foreground" aria-label={`Remove line ${i + 1}`}
                        onClick={() => setLines((current) => current.filter((_, j) => j !== i))}>
                        <X />
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
            <TableFooter>
              <TableRow className="hover:bg-transparent">
                <TableHead scope="row" className="text-foreground normal-case">Totals</TableHead>
                <TableCell className={numeric}>{fromMinor(debits)}</TableCell>
                <TableCell className={numeric}>{fromMinor(credits)}</TableCell>
                <TableCell />
              </TableRow>
            </TableFooter>
          </Table>

          <div className="flex flex-wrap items-center gap-3 px-5 pb-5 sm:px-6">
            <p
              role="status"
              className={cn('mr-auto flex items-center gap-2 text-sm', balanced ? 'text-success' : 'text-muted-foreground')}
            >
              {balanced ? <CircleCheck className="size-4" /> : complete ? <TriangleAlert className="size-4 text-warning" /> : null}
              {balanced
                ? 'Balanced.'
                : !complete
                  ? 'Each line needs an account and an amount in exactly one column (up to two decimals).'
                  : `Out of balance by ${fromMinor(Math.abs(debits - credits))}.`}
            </p>
            <Button onClick={() => setLines((current) => [...current, blank])}>
              <Plus /> Add line
            </Button>
            <Button type="submit" variant="primary" disabled={submitting || !balanced || memo.trim() === ''}>
              {submitting ? 'Posting…' : 'Post entry'}
            </Button>
          </div>
        </form>
      </section>
    </Card>
  )
}
