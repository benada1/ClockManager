import { describe, expect, it } from 'vitest'
import { shiftBadges, shiftTimeParts, totalParts, weekCountsText } from './labels.js'
import { formatShortRange, weekDays, weekOf } from './weeks.js'

describe('weekOf', () => {
  it('starts on Monday and ends on Sunday', () => {
    expect(weekOf('2026-11-20')).toEqual({ from: '2026-11-16', to: '2026-11-22' })
    expect(weekOf('2026-11-16')).toEqual({ from: '2026-11-16', to: '2026-11-22' })
    expect(weekOf('2026-11-22')).toEqual({ from: '2026-11-16', to: '2026-11-22' })
  })

  it('crosses the year boundary (T8) and DST weeks by date only', () => {
    expect(weekOf('2026-12-31')).toEqual({ from: '2026-12-28', to: '2027-01-03' })
    expect(weekOf('2026-10-25')).toEqual({ from: '2026-10-19', to: '2026-10-25' })
  })

  it('is null for a value that is not a date', () => {
    expect(weekOf('20.11.2026')).toBeNull()
    expect(weekOf(null)).toBeNull()
  })
})

describe('weekDays', () => {
  it('lists seven days Monday first, with zero for days without shifts', () => {
    const data = { from: '2026-11-16', to: '2026-11-22', days: [{ date: '2026-11-18', totalSeconds: 3600, shifts: [{}, {}] }] }
    const days = weekDays(data)
    expect(days.map((d) => d.short)).toEqual(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'])
    expect(days.map((d) => d.letter).join('')).toBe('MTWTFSS')
    expect(days[2]).toMatchObject({ date: '2026-11-18', name: 'Wednesday', seconds: 3600, shifts: 2 })
    expect(days[0].seconds).toBe(0)
  })

  it('is empty without a week', () => {
    expect(weekDays({ from: null, to: null, days: [] })).toEqual([])
  })
})

describe('week texts', () => {
  it('splits a total for the big display', () => {
    expect(totalParts(157740)).toEqual({ h: '43', m: '49' })
    expect(totalParts(59)).toEqual({ h: '0', m: '00' })
    expect(totalParts(undefined)).toEqual({ h: '0', m: '00' })
  })

  it('counts shifts and flagged shifts', () => {
    const data = { days: [{ shifts: [{ flags: ['short_shift'] }, { flags: [] }] }, { shifts: [{ flags: ['long_shift', 'x'] }] }] }
    expect(weekCountsText(data)).toBe('3 shifts · 2 flagged')
    expect(weekCountsText({ days: [{ shifts: [{ flags: [] }] }] })).toBe('1 shift')
  })

  it('writes a short range', () => {
    expect(formatShortRange('2026-11-16', '2026-11-22')).toBe('16 – 22 Nov')
    expect(formatShortRange('2026-11-30', '2026-12-06')).toBe('30 Nov – 6 Dec')
    expect(formatShortRange('2026-12-28', '2027-01-03')).toBe('28 Dec – 3 Jan')
  })
})

describe('shiftTimeParts', () => {
  it('gives start and end of a normal shift', () => {
    expect(shiftTimeParts({ clockIn: { zurich: '2026-01-12T08:00:00+01:00' }, clockOut: { zurich: '2026-01-12T16:30:00+01:00' } })).toEqual({
      start: '08:00', end: '16:30', endDay: null,
    })
  })

  it('names the next day for an overnight shift (T3)', () => {
    expect(shiftTimeParts({ clockIn: { zurich: '2026-01-12T22:00:00+01:00' }, clockOut: { zurich: '2026-01-13T06:00:00+01:00' } }).endDay).toBe('Tue')
  })

  it('shows offsets across DST end (T5) and in the repeated hour (T6)', () => {
    expect(shiftTimeParts({ clockIn: { zurich: '2026-10-24T22:00:00+02:00' }, clockOut: { zurich: '2026-10-25T06:00:00+01:00' } })).toEqual({
      start: '22:00 UTC+2', end: '06:00 UTC+1', endDay: 'Sun',
    })
    expect(shiftTimeParts({ clockIn: { zurich: '2026-10-25T02:30:00+02:00' }, clockOut: { zurich: '2026-10-25T02:15:00+01:00' } })).toEqual({
      start: '02:30 UTC+2', end: '02:15 UTC+1', endDay: null,
    })
  })

  it('has no end while open', () => {
    expect(shiftTimeParts({ clockIn: { zurich: '2026-11-22T08:03:11+01:00' }, clockOut: null })).toEqual({ start: '08:03', end: null, endDay: null })
  })
})

describe('badge tones', () => {
  it('marks a forgotten clock-out as danger and other flags as warning', () => {
    const tones = shiftBadges({ open: true, flags: ['forgotten_clock_out', 'long_shift', 'short_shift'] }).map((b) => [b.key, b.tone])
    expect(tones).toEqual([['open', 'info'], ['forgotten_clock_out', 'danger'], ['long_shift', 'warning'], ['short_shift', 'warning']])
  })
})
