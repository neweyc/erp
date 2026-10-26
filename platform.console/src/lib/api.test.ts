import { describe, expect, it } from 'vitest'
import { signInUrlFor, suggestSubdomain } from './api'

describe('suggesting an address', () => {
  it.each([
    ['Acme Fire', 'acme-fire'],
    ['Acme Fire & Rescue Ltd.', 'acme-fire-rescue-ltd'],
    ['  Café Société  ', 'cafe-societe'],
    ['---', ''],
  ])('makes %j into %j', (name, expected) => {
    expect(suggestSubdomain(name)).toBe(expected)
  })

  it('keeps within 40 characters without ending on a hyphen', () => {
    const suggested = suggestSubdomain(`${'a'.repeat(39)} b`)
    expect(suggested).toBe('a'.repeat(39))
  })
})

describe('the full address', () => {
  it('puts the address in front of the base host, keeping scheme and port', () => {
    expect(signInUrlFor('acme', 'https://app.example.com')).toBe('https://acme.app.example.com')
    expect(signInUrlFor('acme', 'http://localhost:5173')).toBe('http://acme.localhost:5173')
  })

  it.each(['acme fire', 'acme/x', 'ab', '1acme', 'acme--fire', 'acme-'])(
    'shows no address for %j, which is not one', (subdomain) => {
      expect(signInUrlFor(subdomain, 'https://app.example.com')).toBeNull()
    })
})
