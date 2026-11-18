import { describe, expect, it } from 'vitest'
import { ApiError, NetworkError } from '../../api/client.js'
import { createHistoryController } from './historyController.js'
import { dayNoteTexts, formatTotal, shiftBadges, shiftDurationText, shiftTimesText } from './labels.js'
import { addDays, formatDayHeading, formatRange, neighbourWeek } from './weeks.js'

const week = (from, to, extra = {}) => ({ from, to, days: [], totalSeconds: 0, ...extra })

function deferred() {
  const d = {}
  d.promise = new Promise((resolve, reject) => Object.assign(d, { resolve, reject }))
  return d
}

describe('week arithmetic', () => {
  it('moves across a month boundary', () => {
    expect(neighbourWeek({ from: '2026-11-30', to: '2026-12-06' }, 1)).toEqual({ from: '2026-12-07', to: '2026-12-13' })
    expect(neighbourWeek({ from: '2026-12-01', to: '2026-12-07' }, -1)).toEqual({ from: '2026-11-24', to: '2026-11-30' })
  })

  it('moves across the year boundary and back', () => {
    const w = { from: '2026-12-28', to: '2027-01-03' }
    const next = neighbourWeek(w, 1)
    expect(next).toEqual({ from: '2027-01-04', to: '2027-01-10' })
    expect(neighbourWeek(next, -1)).toEqual(w)
    expect(neighbourWeek({ from: '2026-12-21', to: '2026-12-27' }, 1)).toEqual(w)
  })

  it('is not disturbed by the clock changes (23 and 25 hour days)', () => {
    expect(neighbourWeek({ from: '2026-03-23', to: '2026-03-29' }, 1)).toEqual({ from: '2026-03-30', to: '2026-04-05' })
    expect(neighbourWeek({ from: '2026-10-26', to: '2026-11-01' }, -1)).toEqual({ from: '2026-10-19', to: '2026-10-25' })
    expect(addDays('2026-02-28', 1)).toBe('2026-03-01')
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29')
  })

  it('refuses a value that is not yyyy-MM-dd', () => {
    expect(neighbourWeek({ from: 'nope', to: '2026-11-22' }, 1)).toBeNull()
    expect(addDays(null, 1)).toBeNull()
  })

  it('writes the range and day headings in plain words', () => {
    expect(formatRange('2026-11-16', '2026-11-22')).toBe('16 Nov – 22 Nov 2026')
    expect(formatRange('2026-12-28', '2027-01-03')).toBe('28 Dec 2026 – 3 Jan 2027')
    expect(formatDayHeading('2026-11-17')).toBe('Tuesday 17 Nov')
  })
})

describe('labels', () => {
  it('formats totals as hours and minutes', () => {
    expect(formatTotal(32949)).toBe('9 h 09 min')
    expect(formatTotal(0)).toBe('0 h 00 min')
    expect(formatTotal(151749)).toBe('42 h 09 min')
  })

  it('shows a normal shift with Zurich times and a duration', () => {
    const s = { clockIn: { zurich: '2026-11-17T08:03:11+01:00' }, clockOut: { zurich: '2026-11-17T17:12:20+01:00' }, durationSeconds: 32949, open: false, flags: [] }
    expect(shiftTimesText(s)).toBe('08:03 to 17:12 (Zurich time)')
    expect(shiftDurationText(s)).toBe('9 h 09 min')
    expect(shiftBadges(s)).toEqual([])
  })

  it('shows the offsets inside the repeated October hour', () => {
    const s = { clockIn: { zurich: '2026-10-25T02:30:00+02:00' }, clockOut: { zurich: '2026-10-25T02:15:00+01:00' }, durationSeconds: 2700, open: false, flags: [] }
    expect(shiftTimesText(s)).toBe('02:30 UTC+2 to 02:15 UTC+1 (Zurich time)')
  })

  it('labels overnight, open and flagged shifts in plain words', () => {
    const night = { endsNextDay: true, open: false, flags: ['long_shift'] }
    expect(shiftBadges(night).map((b) => b.text)).toEqual(['Ends next day', 'Unusually long shift'])
    const open = { clockIn: { zurich: '2026-11-22T08:03:11+01:00' }, clockOut: null, durationSeconds: null, open: true, flags: ['forgotten_clock_out', 'mystery'] }
    expect(shiftBadges(open).map((b) => b.text)).toEqual(['Still open', 'Open for a very long time: you may have forgotten to clock out'])
    expect(shiftDurationText(open)).toBe('Not counted until you clock out')
    expect(shiftTimesText(open)).toBe('Since 08:03 (Zurich time)')
  })

  it('explains the clock-change note and ignores unknown notes', () => {
    expect(dayNoteTexts({ notes: ['dst_change', 'other'] })).toEqual(['Clocks changed this night'])
    expect(dayNoteTexts({ notes: [] })).toEqual([])
  })
})

describe('history controller', () => {
  it('opens on the server default week, then asks for explicit dates', async () => {
    const asked = []
    const c = createHistoryController({ api: { getShifts: (range) => (asked.push(range), Promise.resolve(week(range?.from ?? '2026-11-16', range?.to ?? '2026-11-22'))) } })
    await c.open()
    await c.previous()
    expect(asked).toEqual([null, { from: '2026-11-09', to: '2026-11-15' }])
    expect(c.getState().data.from).toBe('2026-11-09')
  })

  it('ignores a stale answer that arrives after a newer one', async () => {
    const slow = deferred()
    const calls = []
    const c = createHistoryController({
      api: { getShifts: (range) => (calls.push(range), calls.length === 1 ? slow.promise : Promise.resolve(week('2026-11-23', '2026-11-29'))) },
    })
    const first = c.open()
    await c.retry() // newer request wins
    slow.resolve(week('2026-11-16', '2026-11-22'))
    await first
    expect(c.getState().data.from).toBe('2026-11-23')
    expect(c.getState().loading).toBe(false)
  })

  it('ignores clicks while loading', async () => {
    const pending = deferred()
    let n = 0
    const c = createHistoryController({ api: { getShifts: () => (n++, pending.promise) } })
    c.open()
    await c.next()
    expect(n).toBe(1)
  })

  it('keeps the week on screen when a load fails and retries the same range', async () => {
    const asked = []
    let fail = true
    const c = createHistoryController({
      api: {
        getShifts: (range) => {
          asked.push(range)
          if (range && fail) return Promise.reject(new ApiError({ status: 500 }))
          return Promise.resolve(week(range?.from ?? '2026-11-16', range?.to ?? '2026-11-22'))
        },
      },
    })
    await c.open()
    await c.next()
    expect(c.getState().data.from).toBe('2026-11-16')
    expect(c.getState().failure.kind).toBe('error')
    fail = false
    await c.retry()
    expect(asked.at(-1)).toEqual({ from: '2026-11-23', to: '2026-11-29' })
    expect(c.getState().data.from).toBe('2026-11-23')
    expect(c.getState().failure).toBeNull()
  })

  it('tells offline and bad-date failures apart', async () => {
    const errors = [new NetworkError('down'), new ApiError({ status: 400, code: 'request.invalid', errors: { to: ['must not be before "from"'] } })]
    for (const err of errors) {
      const c = createHistoryController({ api: { getShifts: () => Promise.reject(err) } })
      await c.open()
      expect(c.getState().failure.kind).toBe(err instanceof NetworkError ? 'offline' : 'invalid')
    }
    const c = createHistoryController({ api: { getShifts: () => Promise.reject(errors[1]) } })
    await c.open()
    expect(c.getState().failure.text).toBe('must not be before "from"')
  })

  it('does not offer week buttons when there are no shifts at all', async () => {
    let n = 0
    const c = createHistoryController({ api: { getShifts: () => (n++, Promise.resolve({ from: null, to: null, days: [], totalSeconds: 0 })) } })
    await c.open()
    await c.previous()
    expect(n).toBe(1)
  })
})
