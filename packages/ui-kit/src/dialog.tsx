import type { ComponentProps, ReactNode } from 'react'
import { AlertDialog as AlertDialogPrimitive, Dialog as DialogPrimitive } from 'radix-ui'
import { X } from 'lucide-react'
import { buttonVariants } from './button'
import { cn } from './cn'

const overlay =
  'fixed inset-0 z-50 bg-black/40 backdrop-blur-[2px] data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=closed]:animate-out data-[state=closed]:fade-out-0'

/**
 * Centered, and never taller than the screen. The height is capped in `svh`, never `vh` (CLAUDE.md):
 * `vh` measures the viewport behind a phone browser's toolbars, which pushes the submit button out
 * of reach. The body scrolls; the title and the buttons stay put.
 */
const panel =
  'fixed top-1/2 left-1/2 z-50 flex max-h-[90svh] w-[calc(100%-2rem)] -translate-x-1/2 -translate-y-1/2 flex-col rounded-xl border bg-popover text-popover-foreground shadow-2xl outline-none data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-[0.97] data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-[0.97]'

const sizes = { sm: 'max-w-md', md: 'max-w-lg', lg: 'max-w-2xl' } as const

/**
 * A modal dialog.
 *
 * `dismissible` is REQUIRED, so every dialog decides it on purpose (CLAUDE.md). A dialog holding a
 * part-filled form passes `false`: a stray click on the backdrop, or Escape, would otherwise throw
 * away what was typed with no undo. It then closes only by its own Cancel or close button.
 *
 * Put a <form> inside with <DialogBody> and <DialogFooter> as its children, so the submit button is
 * in the form and Enter submits it.
 */
export function Dialog({
  open, onOpenChange, dismissible, title, description, size = 'md', children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  dismissible: boolean
  title: ReactNode
  description?: ReactNode
  size?: keyof typeof sizes
  children: ReactNode
}) {
  const refuse = (event: Event) => {
    if (!dismissible) event.preventDefault()
  }

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className={overlay} />
        <DialogPrimitive.Content
          className={cn(panel, sizes[size])}
          onInteractOutside={refuse}
          onEscapeKeyDown={refuse}
          // Without a description Radix warns unless told there is none on purpose.
          {...(description ? {} : { 'aria-describedby': undefined })}
        >
          <div className="flex items-start justify-between gap-4 border-b px-6 pt-5 pb-4">
            <div className="grid gap-1">
              <DialogPrimitive.Title className="text-base font-semibold tracking-tight">{title}</DialogPrimitive.Title>
              {description && (
                <DialogPrimitive.Description className="text-sm text-muted-foreground">
                  {description}
                </DialogPrimitive.Description>
              )}
            </div>
            <DialogPrimitive.Close
              className={cn(buttonVariants({ variant: 'ghost', size: 'icon' }), '-mt-1 -mr-2 size-8 text-muted-foreground')}
              aria-label="Close"
            >
              <X />
            </DialogPrimitive.Close>
          </div>
          {children}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}

/** The scrolling middle of a dialog. */
export function DialogBody({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('min-h-0 flex-1 overflow-y-auto px-6 py-5', className)} {...props} />
}

/** The buttons, always visible below the body: the main action last, on the right. */
export function DialogFooter({ className, ...props }: ComponentProps<'div'>) {
  return (
    <div
      className={cn('flex flex-col-reverse gap-2 border-t bg-muted/40 px-6 py-4 rounded-b-xl sm:flex-row sm:justify-end', className)}
      {...props}
    />
  )
}

/**
 * Asks before something that cannot be quietly undone: deactivating an account, deleting a role.
 * An alert dialog, so it takes focus and the backdrop does not dismiss it; Cancel is focused first,
 * so pressing Enter by reflex does nothing harmful.
 */
export function ConfirmDialog({
  open, onOpenChange, title, description, confirmLabel, confirmAccessibleName, busy = false, onConfirm,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: ReactNode
  description: ReactNode
  confirmLabel: string
  /** When the visible label alone is ambiguous: "Confirm: deactivate ana@example.com". */
  confirmAccessibleName?: string
  busy?: boolean
  onConfirm: () => void
}) {
  return (
    <AlertDialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <AlertDialogPrimitive.Portal>
        <AlertDialogPrimitive.Overlay className={overlay} />
        <AlertDialogPrimitive.Content className={cn(panel, sizes.sm)}>
          <div className="grid gap-2 px-6 pt-6 pb-5">
            <AlertDialogPrimitive.Title className="text-base font-semibold tracking-tight">{title}</AlertDialogPrimitive.Title>
            <AlertDialogPrimitive.Description className="text-sm text-muted-foreground">
              {description}
            </AlertDialogPrimitive.Description>
          </div>
          <div className="flex flex-col-reverse gap-2 border-t bg-muted/40 px-6 py-4 rounded-b-xl sm:flex-row sm:justify-end">
            <AlertDialogPrimitive.Cancel className={buttonVariants({ variant: 'outline' })} disabled={busy}>
              Cancel
            </AlertDialogPrimitive.Cancel>
            <button
              type="button"
              className={buttonVariants({ variant: 'destructive' })}
              aria-label={confirmAccessibleName}
              disabled={busy}
              onClick={onConfirm}
            >
              {confirmLabel}
            </button>
          </div>
        </AlertDialogPrimitive.Content>
      </AlertDialogPrimitive.Portal>
    </AlertDialogPrimitive.Root>
  )
}

/**
 * A panel sliding in from the left: the navigation on a phone. Dismissible, since it holds no
 * input to lose.
 */
export function Sheet({
  open, onOpenChange, title, children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Read by screen readers; the panel shows its own heading. */
  title: string
  children: ReactNode
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className={overlay} />
        <DialogPrimitive.Content
          aria-describedby={undefined}
          className="fixed inset-y-0 left-0 z-50 flex h-full w-72 max-w-[85vw] flex-col border-r bg-sidebar shadow-2xl outline-none data-[state=open]:animate-in data-[state=open]:slide-in-from-left data-[state=closed]:animate-out data-[state=closed]:slide-out-to-left"
        >
          <DialogPrimitive.Title className="sr-only">{title}</DialogPrimitive.Title>
          {children}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}
