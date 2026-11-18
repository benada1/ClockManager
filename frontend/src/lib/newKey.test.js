import { describe, expect, it } from 'vitest'
import newKey from './newKey.js'

describe('newKey', () => {
  it('uses randomUUID when there is one', () => {
    expect(newKey({ randomUUID: () => 'abc' })).toBe('abc')
  })
  it('falls back to getRandomValues and builds a v4 UUID', () => {
    const c = { getRandomValues: (a) => a.fill(255) }
    expect(newKey(c)).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })
  it('throws when there is no random source', () => {
    expect(() => newKey({})).toThrow()
    expect(() => newKey(undefined)).not.toThrow() // global crypto exists in the test runtime
  })
})
