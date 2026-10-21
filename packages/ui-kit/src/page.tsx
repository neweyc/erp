import type { ComponentType, ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { cn } from './cn'

/**
 * The top of every page: its one h1, a sentence saying what the page is for, and the page's main
 * actions on the right (under the title on a phone).
 */
export function PageHeader({
  title, description, actions, className,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('flex flex-wrap items-end justify-between gap-x-6 gap-y-4', className)}>
      <div className="grid min-w-0 gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-balance">{title}</h1>
        {description && <p className="max-w-2xl text-sm text-muted-foreground text-pretty">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

/** The vertical rhythm of a page's content: header, alerts, cards. */
export function PageStack({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('grid min-w-0 gap-6', className)}>{children}</div>
}

/**
 * What an empty list says instead of nothing. It names what would be here and, where the reader
 * can, what to do about it: "No tickets" alone reads as a fault.
 */
export function EmptyState({
  icon: Icon, title, headingLevel, children, action, className,
}: {
  icon?: ComponentType<{ className?: string }>
  title: ReactNode
  /** When the empty state IS the page (not found), its title is the page's heading. */
  headingLevel?: 1 | 2 | 3
  children?: ReactNode
  action?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('flex flex-col items-center gap-3 px-6 py-12 text-center', className)}>
      {Icon && (
        <div className="flex size-11 items-center justify-center rounded-full bg-muted text-muted-foreground">
          <Icon className="size-5" />
        </div>
      )}
      <div className="grid max-w-sm gap-1">
        {headingLevel ? (
          <Heading level={headingLevel} className="text-base font-semibold">{title}</Heading>
        ) : (
          <p className="text-sm font-medium">{title}</p>
        )}
        {children && <p className="text-sm text-muted-foreground text-pretty">{children}</p>}
      </div>
      {action}
    </div>
  )
}

function Heading({ level, className, children }: { level: 1 | 2 | 3; className: string; children: ReactNode }) {
  const Tag = level === 1 ? 'h1' : level === 2 ? 'h2' : 'h3'
  return <Tag className={className}>{children}</Tag>
}

/** Shown while a page's data loads. A status, so tests and screen readers can wait on it. */
export function Loading({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <p role="status" className={cn('flex items-center gap-2 py-10 text-sm text-muted-foreground', className)}>
      <Loader2 aria-hidden className="size-4 animate-spin" />
      {children}
    </p>
  )
}

/** Initials in a circle, for a person who has no picture. Decorative: the name is always beside it. */
export function Avatar({ name, className }: { name: string; className?: string }) {
  return (
    <span
      aria-hidden
      className={cn(
        'inline-flex size-8 shrink-0 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-primary uppercase',
        className,
      )}
    >
      {initials(name)}
    </span>
  )
}

/** "ana.smith@acme.test" -> "AS"; "Acme Fire" -> "AF". */
export function initials(name: string): string {
  const words = name.split('@')[0]!.split(/[\s._-]+/).filter(Boolean)
  return words.slice(0, 2).map((w) => w[0]).join('') || '?'
}
