import type { ComponentProps } from 'react'
import { ChevronDown } from 'lucide-react'
import { cn } from './cn'

const control =
  'w-full min-w-0 rounded-md border border-input bg-card px-3 text-sm shadow-xs transition-[color,box-shadow,border-color] outline-none placeholder:text-muted-foreground/80 focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/40 disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-destructive/20'

export function Input({ className, type, ...props }: ComponentProps<'input'>) {
  return <input type={type} className={cn(control, 'h-9 py-1', className)} {...props} />
}

/**
 * A native <select>, styled. Deliberately not a custom listbox: the native control brings the
 * phone's own picker, typing to jump, and screen-reader support that a hand-built one must
 * re-create and usually does not.
 */
export function NativeSelect({ className, children, ...props }: ComponentProps<'select'>) {
  return (
    <div className={cn('relative min-w-0', className)}>
      <select className={cn(control, 'h-9 appearance-none py-1 pr-8')} {...props}>
        {children}
      </select>
      <ChevronDown
        aria-hidden
        className="pointer-events-none absolute top-1/2 right-2.5 size-4 -translate-y-1/2 text-muted-foreground"
      />
    </div>
  )
}

/** A native checkbox in the brand color (theme.css), so keyboard and assistive technology just work. */
export function Checkbox({ className, ...props }: Omit<ComponentProps<'input'>, 'type'>) {
  return (
    <input
      type="checkbox"
      className={cn('size-4 shrink-0 cursor-pointer rounded border-input disabled:cursor-not-allowed', className)}
      {...props}
    />
  )
}
