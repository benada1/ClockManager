// QA (phase 8): week navigation at the awkward calendar places (zurich-time-cases T8, leap day, month ends).
import { describe, expect, it } from 'vitest'
import { addDays, formatDayHeading, formatRange, neighbourWeek } from './weeks.js'

describe('week navigation', () => {
  it('Dec 28 to Jan 3 (ISO week 53 of 2026) and the weeks around it', () => {
    const w = { from: '2026-12-28', to: '2027-01-03' }
    expect(neighbourWeek(w, 1)).toEqual({ from: '2027-01-04', to: '2027-01-10' })
    expect(neighbourWeek(w, -1)).toEqual({ from: '2026-12-21', to: '2026-12-27' })
    expect(formatRange(w.from, w.to)).toBe('28 Dec 2026 – 3 Jan 2027')
  })

  it('the leap day 2028-02-29 exists and 2027-02-29 does not', () => {
    expect(neighbourWeek({ from: '2028-02-21', to: '2028-02-27' }, 1)).toEqual({ from: '2028-02-28', to: '2028-03-05' })
    expect(addDays('2028-02-22', 7)).toBe('2028-02-29')
    expect(addDays('2027-02-22', 7)).toBe('2027-03-01')
    expect(formatDayHeading('2028-02-29')).toBe('Tuesday 29 Feb')
  })

  it('month ends in both directions', () => {
    expect(neighbourWeek({ from: '2026-11-23', to: '2026-11-29' }, 1)).toEqual({ from: '2026-11-30', to: '2026-12-06' })
    expect(neighbourWeek({ from: '2026-03-02', to: '2026-03-08' }, -1)).toEqual({ from: '2026-02-23', to: '2026-03-01' })
  })

  it('steps over both clock-change weeks without drifting by an hour or a day', () => {
    expect(neighbourWeek({ from: '2026-03-23', to: '2026-03-29' }, 1)).toEqual({ from: '2026-03-30', to: '2026-04-05' })
    expect(neighbourWeek({ from: '2026-10-19', to: '2026-10-25' }, 1)).toEqual({ from: '2026-10-26', to: '2026-11-01' })
    expect(formatDayHeading('2026-10-25')).toBe('Sunday 25 Oct')
  })

  it('a bad range gives null, so the controller sends nothing', () => {
    expect(neighbourWeek({ from: '2026-11-16', to: 'x' }, 1)).toBeNull()
    expect(neighbourWeek({ from: null, to: null }, 1)).toBeNull()
  })
})
