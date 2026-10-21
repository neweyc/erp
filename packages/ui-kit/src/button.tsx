import type { ComponentProps } from 'react'
import { Slot } from 'radix-ui'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from './cn'

export const buttonVariants = cva(
  'inline-flex shrink-0 items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-[color,background-color,border-color,box-shadow] outline-none select-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*="size-"])]:size-4',
  {
    variants: {
      variant: {
        /** The one action a screen is for. */
        primary: 'bg-primary text-primary-foreground shadow-xs hover:bg-primary/90',
        /** Everything else a person may do here. */
        outline: 'border border-input bg-card shadow-xs hover:bg-accent hover:text-accent-foreground',
        secondary: 'bg-secondary text-secondary-foreground hover:bg-secondary/80',
        /** Quiet actions repeated on every row of a table. */
        ghost: 'hover:bg-accent hover:text-accent-foreground',
        /** Takes something away: deactivate, delete. */
        destructive: 'bg-destructive text-white shadow-xs hover:bg-destructive/90 focus-visible:ring-destructive/30',
        link: 'text-primary underline-offset-4 hover:underline',
      },
      size: {
        sm: 'h-8 px-3 text-[13px] has-[>svg]:px-2.5',
        md: 'h-9 px-4 has-[>svg]:px-3',
        lg: 'h-10 px-6 has-[>svg]:px-4',
        icon: 'size-9',
      },
    },
    defaultVariants: { variant: 'outline', size: 'md' },
  },
)

/**
 * A button. `asChild` renders the child element instead (a router `Link` that looks like a button)
 * so a link stays a link for the browser and for assistive technology.
 */
export function Button({
  className, variant, size, asChild = false, type, ...props
}: ComponentProps<'button'> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Component = asChild ? Slot.Root : 'button'
  return (
    <Component
      // A button inside a form submits it unless told otherwise; defaulting to "button" means only
      // a deliberate type="submit" can, so an "Add line" button never posts half an entry.
      type={asChild ? undefined : (type ?? 'button')}
      className={cn(buttonVariants({ variant, size }), className)}
      {...props}
    />
  )
}
