import type { ReactNode } from 'react'
import { Card } from '@app-platform/ui-kit'
import { Brand } from './brand'

/** The signed-out pages: one card, centered, with the product's mark above it. */
export function AuthLayout({
  title, description, children, footer,
}: {
  title: string
  description?: ReactNode
  children: ReactNode
  footer?: ReactNode
}) {
  return (
    <div className="relative isolate flex min-h-svh flex-col items-center justify-center overflow-hidden px-4 py-12">
      {/* A soft wash of the brand color behind the card. Decoration only. */}
      <div
        aria-hidden
        className="absolute inset-x-0 top-0 -z-10 h-[28rem] bg-[radial-gradient(ellipse_60%_60%_at_50%_0%,color-mix(in_oklch,var(--primary)_14%,transparent),transparent)]"
      />
      <main className="w-full max-w-[400px]">
        <div className="mb-8 flex justify-center">
          <Brand className="text-lg" />
        </div>
        <Card className="shadow-sm">
          <div className="grid gap-6 p-6 sm:p-8">
            <div className="grid gap-1.5 text-center">
              <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
              {description && <p className="text-sm text-muted-foreground">{description}</p>}
            </div>
            {children}
          </div>
        </Card>
        {footer && <div className="mt-6 text-center text-xs text-muted-foreground">{footer}</div>}
      </main>
    </div>
  )
}
