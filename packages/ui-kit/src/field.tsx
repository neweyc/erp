import type { ComponentProps, ReactNode } from 'react'
import { cn } from './cn'

export function Label({ className, ...props }: ComponentProps<'label'>) {
  return (
    <label
      className={cn('text-sm leading-none font-medium select-none peer-disabled:opacity-50', className)}
      {...props}
    />
  )
}

/**
 * A label above its control, with an optional hint below. The caller passes the control and gives
 * it the same `id` as `htmlFor`, which is what makes clicking the label focus it and what a screen
 * reader announces.
 */
export function Field({
  label, htmlFor, hint, className, children,
}: {
  label: ReactNode
  htmlFor: string
  hint?: ReactNode
  className?: string
  children: ReactNode
}) {
  return (
    <div className={cn('grid min-w-0 gap-2', className)}>
      <Label htmlFor={htmlFor}>{label}</Label>
      {children}
      {hint && <p className="text-[13px] text-muted-foreground">{hint}</p>}
    </div>
  )
}

/**
 * A checkbox with its label and an optional description, the whole row clickable. Used for
 * checklists of roles and permissions, where the description is what the person is choosing.
 */
export function CheckboxRow({
  control, title, description, className,
}: {
  /** The <Checkbox>; it sits inside the label, so the label needs no htmlFor. */
  control: ReactNode
  title: ReactNode
  description?: ReactNode
  className?: string
}) {
  return (
    <label
      className={cn(
        'flex cursor-pointer items-start gap-3 rounded-md px-3 py-2.5 transition-colors hover:bg-accent/60 has-[:checked]:bg-accent',
        className,
      )}
    >
      <span className="flex h-5 items-center">{control}</span>
      <span className="grid gap-0.5 text-sm">
        <span className="font-medium">{title}</span>
        {description && <span className="text-muted-foreground">{description}</span>}
      </span>
    </label>
  )
}
