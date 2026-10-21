import type { ReactNode } from 'react'
import { AlertCircle, CheckCircle2, Info, TriangleAlert } from 'lucide-react'
import { cn } from './cn'

const tones = {
  danger: { icon: AlertCircle, className: 'border-destructive/30 bg-destructive/5 text-destructive' },
  warning: { icon: TriangleAlert, className: 'border-warning/35 bg-warning/5 text-warning' },
  success: { icon: CheckCircle2, className: 'border-success/30 bg-success/5 text-success' },
  info: { icon: Info, className: 'border-info/30 bg-info/5 text-info' },
} as const

/**
 * A message about the page: a failure, a warning, a confirmation.
 *
 * A failure is `role="alert"`, announced at once; anything else is `role="status"`, announced
 * politely. Tests and screen readers both rely on that split, so it follows from the tone rather
 * than being left to each caller.
 */
export function Alert({
  tone, title, children, className,
}: {
  tone: keyof typeof tones
  title?: ReactNode
  children: ReactNode
  className?: string
}) {
  const { icon: Icon, className: toneClass } = tones[tone]
  return (
    <div
      role={tone === 'danger' ? 'alert' : 'status'}
      className={cn('flex gap-3 rounded-lg border px-4 py-3 text-sm', toneClass, className)}
    >
      <Icon aria-hidden className="mt-0.5 size-4 shrink-0" />
      <div className="grid min-w-0 gap-0.5">
        {title && <p className="font-medium">{title}</p>}
        <div className="text-foreground/85">{children}</div>
      </div>
    </div>
  )
}
