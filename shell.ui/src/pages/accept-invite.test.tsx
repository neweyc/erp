import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AcceptInvite } from './accept-invite'

function stubAccept(status: number, body: unknown) {
  vi.stubGlobal('fetch', vi.fn(() =>
    Promise.resolve({ ok: status < 400, status, json: async () => body } as Response)))
}

async function accept() {
  render(<MemoryRouter><AcceptInvite /></MemoryRouter>)
  const user = userEvent.setup()
  await user.type(screen.getByLabelText('Password'), 'correct horse battery')
  await user.click(screen.getByRole('button', { name: 'Accept invitation' }))
}

beforeEach(() => window.history.replaceState({}, '', '/accept-invite?token=abc'))
afterEach(() => vi.unstubAllGlobals())

describe('accepting an invitation', () => {
  it('sends the person to sign in here when this is still the organization’s address', async () => {
    stubAccept(200, { email: 'ada@acme.test', signInUrl: window.location.origin })

    await accept()

    expect(await screen.findByRole('link', { name: 'Go to sign in' })).toHaveAttribute('href', '/')
    expect(screen.getByText('Your password is set. You can now sign in.')).toBeInTheDocument()
  })

  it('sends the person to the organization’s new address when it changed after they were invited', async () => {
    stubAccept(200, { email: 'ada@acme.test', signInUrl: 'https://acme-fire.app.example.com' })

    await accept()

    expect(await screen.findByRole('link', { name: 'Go to sign in' }))
      .toHaveAttribute('href', 'https://acme-fire.app.example.com')
    expect(screen.getByText(/signs in at https:\/\/acme-fire\.app\.example\.com/)).toBeInTheDocument()
  })

  it('ignores an address that is not a web address', async () => {
    stubAccept(200, { email: 'ada@acme.test', signInUrl: 'javascript:alert(1)' })

    await accept()

    expect(await screen.findByRole('link', { name: 'Go to sign in' })).toHaveAttribute('href', '/')
  })

  it('says when the link is not valid', async () => {
    stubAccept(400, { problemCode: 'invalid_token' })

    await accept()

    expect(await screen.findByText(/This invitation link is not valid/)).toBeInTheDocument()
  })

  it('asks for the link from the email when the token is missing', () => {
    window.history.replaceState({}, '', '/accept-invite')

    render(<MemoryRouter><AcceptInvite /></MemoryRouter>)

    expect(screen.getByText(/missing its token/)).toBeInTheDocument()
  })
})
