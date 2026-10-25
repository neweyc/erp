import { useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { Button } from '@app-platform/ui-kit'

/**
 * "Read at 14:02:11", with a button to read again. On every console screen, so an operator acting
 * on what they see knows how old it is.
 */
export function ReadAt({ readAt, onRefresh }: { readAt: Date | null; onRefresh: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)

  async function refresh() {
    setBusy(true)
    try {
      await onRefresh()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex items-center gap-2 text-sm text-muted-foreground">
      {readAt && <span>Read at {readAt.toLocaleTimeString()}</span>}
      <Button size="sm" onClick={() => void refresh()} disabled={busy}>
        <RefreshCw aria-hidden />
        Refresh
      </Button>
    </div>
  )
}
