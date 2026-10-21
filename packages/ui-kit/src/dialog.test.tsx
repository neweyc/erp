import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { ConfirmDialog, Dialog } from './dialog'

function Harness({ dismissible }: { dismissible: boolean }) {
  const [open, setOpen] = useState(true)
  return open ? (
    <Dialog open onOpenChange={setOpen} dismissible={dismissible} title="Edit role">
      <input aria-label="Name" />
    </Dialog>
  ) : (
    <p>Closed</p>
  )
}

describe('Dialog', () => {
  it('keeps a non-dismissible dialog open on Escape, so typed input is not thrown away', async () => {
    const user = userEvent.setup()
    render(<Harness dismissible={false} />)

    await user.type(screen.getByLabelText('Name'), 'Clerk')
    await user.keyboard('{Escape}')

    expect(screen.getByRole('dialog', { name: 'Edit role' })).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveValue('Clerk')
  })

  it('closes a dismissible dialog on Escape', async () => {
    const user = userEvent.setup()
    render(<Harness dismissible />)

    await user.keyboard('{Escape}')

    expect(await screen.findByText('Closed')).toBeInTheDocument()
  })

  it('always closes from its own close button', async () => {
    const user = userEvent.setup()
    render(<Harness dismissible={false} />)

    await user.click(screen.getByRole('button', { name: 'Close' }))

    expect(await screen.findByText('Closed')).toBeInTheDocument()
  })

  it('caps its height in svh, never vh (a phone browser’s toolbars would hide the buttons)', () => {
    render(<Harness dismissible={false} />)

    const className = screen.getByRole('dialog').className
    expect(className).toMatch(/max-h-\[\d+svh\]/)
    expect(className).not.toMatch(/\d+vh\b/)
  })
})

describe('ConfirmDialog', () => {
  it('names its confirm button precisely and does nothing until it is pressed', async () => {
    const user = userEvent.setup()
    let confirmed = 0
    render(
      <ConfirmDialog
        open
        onOpenChange={() => {}}
        title="Deactivate ana@acme.test?"
        description="They are signed out at once."
        confirmLabel="Deactivate"
        confirmAccessibleName="Confirm: deactivate ana@acme.test"
        onConfirm={() => (confirmed += 1)}
      />,
    )

    expect(screen.getByRole('alertdialog', { name: 'Deactivate ana@acme.test?' })).toBeInTheDocument()
    expect(confirmed).toBe(0)

    await user.click(screen.getByRole('button', { name: 'Confirm: deactivate ana@acme.test' }))
    expect(confirmed).toBe(1)
  })
})
