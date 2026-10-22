import {
  Card, CardContent, CardHeader, cn, Input, Label, numeric, Table, TableBody, TableCell, TableFooter, TableHead,
  TableHeader, TableRow,
} from '@app-platform/ui-kit'
import type { TrialBalance } from './api'
import { fromMinor } from './money'

export function TrialBalanceSection({
  balance, asOf, onAsOfChange,
}: {
  balance: TrialBalance | null
  asOf: string
  onAsOfChange: (date: string) => void
}) {
  return (
    <Card>
      <section aria-labelledby="trial-balance-heading">
        <CardHeader
          id="trial-balance-heading"
          title="Trial balance"
          actions={
            <div className="flex items-center gap-2">
              <Label htmlFor="as-of" className="text-xs font-normal text-muted-foreground">As of</Label>
              <Input id="as-of" type="date" className="h-8 w-40" value={asOf}
                onChange={(e) => e.target.value && onAsOfChange(e.target.value)} />
            </div>
          }
        />

        {!balance || balance.currencies.length === 0 ? (
          <CardContent>
            <p className="text-sm text-muted-foreground">No balances as of this date.</p>
          </CardContent>
        ) : (
          <div className="grid gap-4 pt-4">
            {balance.currencies.map((c) => (
              // One table per currency: amounts in different currencies are never added together.
              <Table key={c.currency} aria-label={`Trial balance in ${c.currency}`}>
                <TableHeader>
                  <TableRow className="hover:bg-transparent">
                    <TableHead scope="col">Account</TableHead>
                    <TableHead scope="col" className={numeric}>Debit</TableHead>
                    <TableHead scope="col" className={numeric}>Credit</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {c.accounts.map((a) => (
                    <TableRow key={a.accountId}>
                      <TableCell>
                        <span className="mr-2 font-mono text-xs text-muted-foreground">{a.code}</span>
                        {a.name}
                      </TableCell>
                      <TableCell className={numeric}>{a.debitMinor ? fromMinor(a.debitMinor) : ''}</TableCell>
                      <TableCell className={numeric}>{a.creditMinor ? fromMinor(a.creditMinor) : ''}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
                <TableFooter>
                  <TableRow className="hover:bg-transparent">
                    <TableHead scope="row" className="text-foreground normal-case">Total {c.currency}</TableHead>
                    <TableCell className={cn(numeric, 'font-semibold')}>{fromMinor(c.totalDebitMinor)}</TableCell>
                    <TableCell className={cn(numeric, 'font-semibold')}>{fromMinor(c.totalCreditMinor)}</TableCell>
                  </TableRow>
                </TableFooter>
              </Table>
            ))}
          </div>
        )}
      </section>
    </Card>
  )
}
