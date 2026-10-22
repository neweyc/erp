import { RotateCcw, ScrollText } from 'lucide-react'
import {
  Badge, Button, Card, CardHeader, EmptyState, numeric, Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@app-platform/ui-kit'
import type { Entry } from './api'
import { fromMinor } from './money'

/** "2026-7": the gapless number an auditor would cite, in the form the server uses in memos. */
const reference = (e: Pick<Entry, 'fiscalYear' | 'number'>) => `${e.fiscalYear}-${e.number}`

export function JournalSection({
  entries, onReverse,
}: {
  entries: readonly Entry[]
  /** Null when the user may not reverse (ledger.entry.reverse): no Reverse buttons. */
  onReverse: ((entryId: string) => Promise<boolean>) | null
}) {
  const byId = new Map(entries.map((e) => [e.entryId, e]))

  return (
    <Card>
      <section aria-labelledby="journal-heading">
        <CardHeader id="journal-heading" title="Journal" description="Every posted entry, newest first. Posted entries never change." />

        {entries.length === 0 ? (
          <EmptyState icon={ScrollText} title="Nothing posted yet." />
        ) : (
          <ul aria-label="Journal entries" className="mt-5 divide-y border-t">
            {entries.map((entry) => {
              const reverses = entry.reversesEntryId ? byId.get(entry.reversesEntryId) : undefined
              const reversedBy = entry.reversedByEntryId ? byId.get(entry.reversedByEntryId) : undefined

              return (
                <li key={entry.entryId}>
                  <article aria-label={`Entry ${reference(entry)}`} className="grid gap-3 py-4">
                    <div className="flex flex-wrap items-start gap-x-3 gap-y-2 px-5 sm:px-6">
                      <div className="grid min-w-0 flex-1 gap-1">
                        <h3 className="text-sm font-medium">
                          <span className="mr-2 font-mono text-xs text-muted-foreground">{reference(entry)}</span>
                          {entry.memo}
                        </h3>
                        <p className="text-xs text-muted-foreground">
                          {entry.entryDate} · {entry.currency}
                        </p>
                      </div>
                      {/* Both directions, so an entry no longer in effect cannot be mistaken for a live one. */}
                      {entry.reversesEntryId && (
                        <Badge tone="info">Reverses {reverses ? reference(reverses) : 'an earlier entry'}.</Badge>
                      )}
                      {entry.reversedByEntryId && (
                        <Badge tone="warning">Reversed by {reversedBy ? reference(reversedBy) : 'a later entry'}.</Badge>
                      )}
                      {/* Offered only where the server would accept it: never twice, never a reversal. */}
                      {onReverse && !entry.reversesEntryId && !entry.reversedByEntryId && (
                        <Button size="sm" variant="ghost" onClick={() => void onReverse(entry.entryId)}>
                          <RotateCcw /> Reverse
                        </Button>
                      )}
                    </div>

                    <div className="mx-5 overflow-hidden rounded-lg border sm:mx-6">
                      <Table>
                        <TableHeader>
                          <TableRow className="hover:bg-transparent">
                            <TableHead scope="col">Account</TableHead>
                            <TableHead scope="col" className={numeric}>Debit</TableHead>
                            <TableHead scope="col" className={numeric}>Credit</TableHead>
                          </TableRow>
                        </TableHeader>
                        <TableBody>
                          {entry.lines.map((line) => (
                            <TableRow key={line.lineNumber}>
                              <TableCell className="py-2 font-mono text-xs">{line.accountCode}</TableCell>
                              <TableCell className={`py-2 ${numeric}`}>{line.amountMinor > 0 ? fromMinor(line.amountMinor) : ''}</TableCell>
                              <TableCell className={`py-2 ${numeric}`}>{line.amountMinor < 0 ? fromMinor(-line.amountMinor) : ''}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    </div>
                  </article>
                </li>
              )
            })}
          </ul>
        )}
      </section>
    </Card>
  )
}
