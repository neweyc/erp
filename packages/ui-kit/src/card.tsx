import type { ComponentProps, ReactNode } from 'react'
import { cn } from './cn'

export function Card({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('min-w-0 rounded-xl border bg-card text-card-foreground shadow-xs', className)} {...props} />
}

/**
 * A card's heading row: title and description on the left, actions on the right (wrapping under
 * them on a phone). The title is a real heading at the level given, so the page outline stays right.
 */
export function CardHeader({
  title, description, actions, level = 2, id, className,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  /** h2 for a section of a page, h3 inside a section. */
  level?: 2 | 3
  /** For `aria-labelledby` on the card's section. */
  id?: string
  className?: string
}) {
  const Heading = level === 2 ? 'h2' : 'h3'
  return (
    <div className={cn('flex flex-wrap items-start justify-between gap-x-4 gap-y-3 px-5 pt-5 sm:px-6', className)}>
      <div className="grid min-w-0 gap-1">
        <Heading id={id} className="text-base leading-tight font-semibold tracking-tight">
          {title}
        </Heading>
        {description && <p className="text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

export function CardContent({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('px-5 py-5 sm:px-6', className)} {...props} />
}

export function CardFooter({ className, ...props }: ComponentProps<'div'>) {
  return (
    <div
      className={cn('flex flex-wrap items-center gap-2 border-t bg-muted/40 px-5 py-3 sm:px-6 rounded-b-xl', className)}
      {...props}
    />
  )
}
