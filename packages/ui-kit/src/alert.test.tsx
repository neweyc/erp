import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Alert } from './alert'
import { initials } from './page'

describe('Alert', () => {
  it('announces a failure at once and anything else politely', () => {
    render(
      <>
        <Alert tone="danger">Could not save.</Alert>
        <Alert tone="success">Saved.</Alert>
      </>,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Could not save.')
    expect(screen.getByRole('status')).toHaveTextContent('Saved.')
  })
})

describe('initials', () => {
  it.each([
    ['ana.silva@acme.test', 'as'],
    ['Acme Fire', 'AF'],
    ['grace@acme.test', 'g'],
    ['', '?'],
  ])('%s -> %s', (name, expected) => {
    expect(initials(name)).toBe(expected)
  })
})
