// QA (phase 8): display helpers around the clock changes, midnight and the year end (zurich-time-cases T1-T8).
import { describe, expect, it } from 'vitest'
import { formatDuration, needsOffset, zoneLabel, zurichDay, zurichOffsetLabel, zurichTime } from './zurich.js'
import { formatElapsed, spokenElapsed } from '../pages/punch/useElapsed.js'

describe('Zurich display of server instants', () => {
  it('T7: a punch exactly at the spring change is 03:00, never 02:00', () => {
    expect(zurichTime('2026-03-29T01:00:00Z')).toBe('03:00')
    expect(zurichTime('2026-03-29T00:59:59Z')).toBe('01:59')
    expect(zurichTime('2026-03-29T03:00:00+02:00')).toBe('03:00')
  })

  it('T6: both 02:30 of the repeated October hour read 02:30 and carry different offsets', () => {
    const first = '2026-10-25T02:30:00+02:00'
    const second = '2026-10-25T02:30:00+01:00'
    expect(zurichTime(first)).toBe('02:30')
    expect(zurichTime(second)).toBe('02:30')
    expect(zurichOffsetLabel(first)).toBe('UTC+2')
    expect(zurichOffsetLabel(second)).toBe('UTC+1')
    expect(needsOffset(first, second)).toBe(true)
    expect(zoneLabel(second, true)).toBe('(Zurich time, UTC+1)')
  })

  it('midnight and year end are read in Zurich, not in UTC', () => {
    expect(zurichTime('2026-12-31T23:00:00Z')).toBe('00:00')
    expect(zurichDay('2026-12-31T23:00:00Z')).toMatch(/Fri 1 Jan/)
    expect(zurichDay('2026-12-31T22:59:59Z')).toMatch(/Thu 31 Dec/)
    expect(zurichTime('2026-06-30T22:00:00Z')).toBe('00:00') // summer: UTC+2
  })

  it('shows the offset on a clock-change day even when both ends have the same offset', () => {
    expect(needsOffset('2026-10-25T08:00:00+01:00', '2026-10-25T16:00:00+01:00')).toBe(true)
    expect(needsOffset('2026-10-26T08:00:00+01:00', '2026-10-26T16:00:00+01:00')).toBe(false)
    expect(needsOffset('2026-03-28T21:00:00+01:00', '2026-03-29T06:00:00+02:00')).toBe(true) // T4: offsets differ
  })

  it('missing or unreadable values give null, never "Invalid Date"', () => {
    for (const bad of [null, undefined, '', 'nonsense', 42]) {
      expect(zurichTime(bad)).toBeNull()
      expect(zurichDay(bad)).toBeNull()
    }
    expect(zurichOffsetLabel('2026-11-17T08:00:00Z')).toBeNull()
  })

  it('accepts the 7-digit fractions the database keeps (100 ns)', () => {
    expect(zurichTime('2026-11-17T08:00:00.1234567+01:00')).toBe('08:00')
  })
})

describe('durations', () => {
  it('formats from seconds, including zero, one minute and day-long shifts', () => {
    expect(formatDuration(0)).toBe('0 s')
    expect(formatDuration(59)).toBe('59 s')
    expect(formatDuration(60)).toBe('1 min')
    expect(formatDuration(3600)).toBe('1 h 00 min')
    expect(formatDuration(25200)).toBe('7 h 00 min') // T4: 7 h from UTC
    expect(formatDuration(32400)).toBe('9 h 00 min') // T5
    expect(formatDuration(2700)).toBe('45 min') // T6
    expect(formatDuration(100 * 3600)).toBe('100 h 00 min')
  })

  it('refuses negative, missing and non-finite values instead of printing nonsense', () => {
    for (const bad of [-1, null, undefined, NaN, Infinity, '60']) expect(formatDuration(bad)).toBeNull()
  })

  it('the live counter formats beyond a day without wrapping', () => {
    expect(formatElapsed(0)).toBe('0:00:00')
    expect(formatElapsed(33000000)).toBe('9:10:00')
    expect(formatElapsed(100 * 3600 * 1000 + 61000)).toBe('100:01:01')
    expect(spokenElapsed(60000)).toBe('1 minute')
    expect(spokenElapsed(3600000)).toBe('1 hour 0 minutes')
  })
})

// QA-30 (open, low): `new Date(iso)` reads a value WITHOUT an offset in the BROWSER time zone, so the "browser zone never plays a role"
// claim in lib/zurich.js holds only because the server always sends an offset. The helper should refuse such a string.
describe('a value without an offset must not be read in the browser zone', () => {
  it('QA-30: zurichTime("2026-11-17T08:00:00") depends on the machine time zone', () => {
    const before = process.env.TZ
    try {
      process.env.TZ = 'America/New_York'
      expect(zurichTime('2026-11-17T08:00:00')).toBeNull()
    } finally {
      if (before === undefined) delete process.env.TZ
      else process.env.TZ = before
    }
  })
})
