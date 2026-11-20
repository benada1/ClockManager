import { describe, expect, it } from 'vitest'
import { THEME_KEY, saveTheme, storedTheme } from './theme.js'

const memory = (initial = {}) => {
  const data = { ...initial }
  return { getItem: (k) => (k in data ? data[k] : null), setItem: (k, v) => (data[k] = String(v)), data }
}
const broken = { getItem: () => { throw new Error('SecurityError') }, setItem: () => { throw new Error('QuotaExceededError') } }

describe('theme storage', () => {
  it('is light on a first visit, whatever the system prefers', () => {
    expect(storedTheme(memory())).toBe('light')
  })

  it('remembers dark and light', () => {
    expect(storedTheme(memory({ [THEME_KEY]: 'dark' }))).toBe('dark')
    expect(storedTheme(memory({ [THEME_KEY]: 'light' }))).toBe('light')
  })

  it('treats garbage as light', () => {
    for (const v of ['Dark', 'auto', '', '1', 'null']) expect(storedTheme(memory({ [THEME_KEY]: v }))).toBe('light')
  })

  it('falls back to light when storage is missing or throws', () => {
    expect(storedTheme(null)).toBe('light')
    expect(storedTheme(undefined)).toBe('light')
    expect(storedTheme(broken)).toBe('light')
  })

  it('saves the choice and reports a refusal instead of throwing', () => {
    const s = memory()
    expect(saveTheme(s, 'dark')).toBe(true)
    expect(s.data[THEME_KEY]).toBe('dark')
    expect(saveTheme(broken, 'dark')).toBe(false)
    expect(saveTheme(null, 'dark')).toBe(false)
  })
})
