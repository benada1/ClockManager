import { describe, expect, it } from 'vitest'
import { NO_ANCHOR, anchorFrom, elapsedSince, greetingFor, nextAnchor, zurichClock } from './serverClock.js'

const at = (utc, ageMs = 0) => ({ serverNow: { utc, zurich: null, ageMs } })
const ms = (iso) => Date.parse(iso)

describe('server clock anchor', () => {
  it('is serverNow + ageMs at monotonic 0', () => {
    expect(anchorFrom(at('2026-11-18T09:41:07.000Z', 120), 5000)).toBe(ms('2026-11-18T09:41:07.000Z') + 120 - 5000)
  })

  it('is null without a trusted time, a sync time, or with an offset-less value', () => {
    expect(anchorFrom({ serverNow: null }, 5000)).toBeNull()
    expect(anchorFrom(at('2026-11-18T09:41:07Z'), null)).toBeNull()
    expect(anchorFrom(at('2026-11-18T09:41:07'), 5000)).toBeNull()
    expect(anchorFrom(at('garbage'), 5000)).toBeNull()
  })

  it('keeps the anchor when a punch clears serverNow but the sync is the same', () => {
    const first = nextAnchor(NO_ANCHOR, at('2026-11-18T09:41:07Z'), 1000)
    expect(first.anchor).not.toBeNull()
    expect(nextAnchor(first, { serverNow: null }, 1000)).toBe(first)
  })

  it('clears it when a new sync arrives without a trusted time (providers down)', () => {
    const first = nextAnchor(NO_ANCHOR, at('2026-11-18T09:41:07Z'), 1000)
    expect(nextAnchor(first, { serverNow: null }, 61000)).toEqual({ syncAt: 61000, anchor: null })
  })

  it('returns the same object when nothing changed', () => {
    const first = nextAnchor(NO_ANCHOR, at('2026-11-18T09:41:07Z'), 1000)
    expect(nextAnchor(first, at('2026-11-18T09:41:07Z'), 1000)).toBe(first)
    expect(nextAnchor(NO_ANCHOR, null, null)).toBe(NO_ANCHOR)
  })
})

describe('zurichClock', () => {
  it('formats a winter instant (T1) with the long date', () => {
    const c = zurichClock(ms('2026-01-12T07:00:05Z'))
    expect(c).toMatchObject({ hm: '08:00', ss: '05', hour: 8, date: '2026-01-12', dateLabel: 'Monday, 12 January 2026', offsetLabel: 'UTC+1', dstDay: false })
  })

  it('shows 03:00 UTC+2 at the DST start instant, never 02:00 (T7)', () => {
    expect(zurichClock(ms('2026-03-29T01:00:00Z'))).toMatchObject({ hm: '03:00', offsetLabel: 'UTC+2', dstDay: true })
  })

  it('tells the two 02:30 of the October night apart by offset (T6)', () => {
    expect(zurichClock(ms('2026-10-25T00:30:00Z'))).toMatchObject({ hm: '02:30', offsetLabel: 'UTC+2', dstDay: true })
    expect(zurichClock(ms('2026-10-25T01:30:00Z'))).toMatchObject({ hm: '02:30', offsetLabel: 'UTC+1', dstDay: true })
  })

  it('uses the Zurich date across midnight and the year boundary (T8)', () => {
    expect(zurichClock(ms('2026-12-31T23:30:00Z')).date).toBe('2027-01-01')
  })

  it('is null for a missing value', () => {
    expect(zurichClock(null)).toBeNull()
    expect(zurichClock(NaN)).toBeNull()
  })
})

describe('greeting and elapsed', () => {
  it('greets by the Zurich hour', () => {
    expect([4, 5, 11, 12, 17, 18, 23, 0].map(greetingFor)).toEqual([
      'Good evening', 'Good morning', 'Good morning', 'Good afternoon', 'Good afternoon', 'Good evening', 'Good evening', 'Good evening',
    ])
  })

  it('counts from the clock-in instant, never below zero, across DST (T5: 9 h)', () => {
    expect(elapsedSince(ms('2026-10-25T05:00:00Z'), '2026-10-24T22:00:00+02:00')).toBe(9 * 3600000)
    expect(elapsedSince(ms('2026-01-12T06:59:58Z'), '2026-01-12T07:00:00Z')).toBe(0)
    expect(elapsedSince(null, '2026-01-12T07:00:00Z')).toBeNull()
    expect(elapsedSince(1, '2026-01-12T07:00:00')).toBeNull()
  })
})
