import { Badge, type BadgeTone } from '@app-platform/ui-kit'
import type { TenantStatus } from '../lib/api'

const LOOK: Record<TenantStatus, { tone: BadgeTone; label: string }> = {
  Active: { tone: 'success', label: 'Active' },
  Suspended: { tone: 'warning', label: 'Suspended' },
  Retired: { tone: 'neutral', label: 'Retired' },
}

export function StatusBadge({ status }: { status: TenantStatus }) {
  const { tone, label } = LOOK[status]
  return <Badge tone={tone} dot>{label}</Badge>
}
