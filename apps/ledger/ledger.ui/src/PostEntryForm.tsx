import { useState } from 'react'
import type { Account, NewLine } from './api'
import { fromMinor, toMinor } from './money'
import { Scrolls } from './Scrolls'
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
    <section aria-labelledby="post-heading">
      <h2 id="post-heading">Post an entry</h2>

      <form onSubmit={submit} aria-label="Post an entry">
        <label htmlFor="entry-date">Date</label>
        <input id="entry-date" type="date" value={entryDate} onChange={(e) => setEntryDate(e.target.value)} required />

        <label htmlFor="entry-memo">Memo</label>
        <input id="entry-memo" value={memo} onChange={(e) => setMemo(e.target.value)} required maxLength={500} />

        <label htmlFor="entry-currency">Currency</label>
        <input id="entry-currency" value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())}
          required maxLength={3} size={4} />

        <Scrolls>
          <table>
            <thead>
              <tr><th>Account</th><th>Debit</th><th>Credit</th><th /></tr>
            </thead>
            <tbody>
              {lines.map((line, i) => (
                <tr key={i}>
                  <td>
                    <select aria-label={`Line ${i + 1} account`} value={line.accountId}
                      onChange={(e) => update(i, { accountId: e.target.value })}>
                      <option value="">Choose…</option>
                      {accounts.map((a) => <option key={a.accountId} value={a.accountId}>{a.code} — {a.name}</option>)}
                    </select>
                  </td>
                  <td>
                    <input aria-label={`Line ${i + 1} debit`} inputMode="decimal" value={line.debit}
                      onChange={(e) => update(i, { debit: e.target.value })} />
                  </td>
                  <td>
                    <input aria-label={`Line ${i + 1} credit`} inputMode="decimal" value={line.credit}
                      onChange={(e) => update(i, { credit: e.target.value })} />
                  </td>
                  <td>
                    {lines.length > 2 && (
                      <button type="button" aria-label={`Remove line ${i + 1}`}
                        onClick={() => setLines((current) => current.filter((_, j) => j !== i))}>
                        Remove
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row">Totals</th>
                <td>{fromMinor(debits)}</td>
                <td>{fromMinor(credits)}</td>
                <td />
              </tr>
            </tfoot>
          </table>
        </Scrolls>

        <p role="status">
          {balanced
            ? 'Balanced.'
            : !complete
              ? 'Each line needs an account and an amount in exactly one column (up to two decimals).'
              : `Out of balance by ${fromMinor(Math.abs(debits - credits))}.`}
        </p>

        <button type="button" onClick={() => setLines((current) => [...current, blank])}>Add line</button>
        <button type="submit" disabled={submitting || !balanced || memo.trim() === ''}>
          {submitting ? 'Posting…' : 'Post entry'}
        </button>
      </form>
    </section>
  )
}
