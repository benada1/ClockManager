// QA (D-088): weekly bar chart tooltips. No DOM library is installed, so this uses SSR, the pure helpers and a few
// static source checks. Tests marked (QA-NN) are regression tests for bugs found in this review.
import { readFileSync } from 'node:fs'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DayCard from '../pages/history/DayCard.jsx'
import { dayBarParts } from '../pages/history/labels.js'
import { weekDays } from '../pages/history/weeks.js'
import BarChart from './BarChart.jsx'
import { nextColumn, tipAlign } from './barChartNav.js'

const src = (p) => readFileSync(new URL(p, import.meta.url), 'utf8')
const html = (el) => renderToStaticMarkup(el)
const text = (d) => dayBarParts(d).join(' · ')

const punch = (utc, zurich) => ({ utc, zurich })
const closedShift = (id, inZ, outZ, seconds) => ({
  shiftId: id,
  open: false,
  clockIn: punch(null, inZ),
  clockOut: punch(null, outZ),
  durationSeconds: seconds,
  flags: [],
})
const openShift = (id, inZ) => ({ shiftId: id, open: true, clockIn: punch(null, inZ), clockOut: null, flags: [] })
const day = (date, shifts, notes = []) => ({
  date,
  notes,
  shifts,
  totalSeconds: shifts.reduce((s, x) => s + (x.durationSeconds ?? 0), 0),
})
const week = (from, to, days) => ({ from, to, days, totalSeconds: days.reduce((s, d) => s + d.totalSeconds, 0) })

// The day card's total text, e.g. "8 h 30 min" or "nothing counted yet".
const cardTotal = (d) => /<strong[^>]*>([^<]*)<\/strong>/.exec(html(<ul><DayCard day={d} /></ul>))[1]
const tipTotal = (d, date) => dayBarParts(weekDays(week(d.date, date, [d]))[0])[1]

describe('tooltip values (zurich-time-cases)', () => {
  it('T1 normal day, 1 s shift, 43 h+ day', () => {
    const w = week('2026-01-12', '2026-01-18', [
      day('2026-01-12', [closedShift('a', '2026-01-12T08:00:00+01:00', '2026-01-12T16:30:00+01:00', 30600)]),
      day('2026-01-13', [closedShift('b', '2026-01-13T08:00:00+01:00', '2026-01-13T08:00:01+01:00', 1)]),
      day('2026-01-14', [closedShift('c', '2026-01-14T00:00:00+01:00', '2026-01-15T19:49:00+01:00', 157740)]),
    ])
    const d = weekDays(w)
    expect(text(d[0])).toBe('Monday · 8 h 30 min · 1 shift')
    expect(text(d[1])).toBe('Tuesday · 1 s · 1 shift')
    expect(text(d[2])).toBe('Wednesday · 43 h 49 min · 1 shift')
  })

  it('T4 / T5: the DST nights show the UTC duration (7 h and 9 h), not the 8 h wall-clock span', () => {
    const march = weekDays(week('2026-03-23', '2026-03-29', [day('2026-03-28', [closedShift('a', '2026-03-28T22:00:00+01:00', '2026-03-29T06:00:00+02:00', 25200)], ['dst_change'])]))
    const october = weekDays(week('2026-10-19', '2026-10-25', [day('2026-10-24', [closedShift('b', '2026-10-24T22:00:00+02:00', '2026-10-25T06:00:00+01:00', 32400)], ['dst_change'])]))
    expect(text(march[5])).toBe('Saturday · 7 h 00 min · 1 shift')
    expect(text(october[5])).toBe('Saturday · 9 h 00 min · 1 shift')
  })

  it('T3 / T8: an overnight shift counts on its clock-in day only; the next day stays empty', () => {
    const d = weekDays(week('2026-12-28', '2027-01-03', [day('2026-12-31', [closedShift('a', '2026-12-31T23:00:00+01:00', '2027-01-01T07:00:00+01:00', 28800)])]))
    expect(text(d[3])).toBe('Thursday · 8 h 00 min · 1 shift')
    expect(text(d[4])).toBe('Friday · 0 min')
  })

  it('days missing from the API, an empty days array and no days field all give seven named days', () => {
    for (const days of [[], undefined]) {
      const d = weekDays({ from: '2026-11-16', to: '2026-11-22', days, totalSeconds: 0 })
      expect(d.map(text)).toEqual(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map((n) => `${n} · 0 min`))
    }
  })

  it('null / loading data gives no days and the chart renders an empty group without crashing', () => {
    expect(weekDays(null)).toEqual([])
    expect(weekDays(undefined)).toEqual([])
    expect(weekDays({ from: null, to: null, days: [] })).toEqual([])
    const out = html(<BarChart days={[]} />)
    expect(out).toContain('role="group"')
    expect(out).not.toContain('tabindex')
  })
})

describe('tooltip text equals the History day card total', () => {
  const cases = {
    'normal day': day('2026-11-16', [closedShift('a', '2026-11-16T08:00:00+01:00', '2026-11-16T16:30:00+01:00', 30600)]),
    '1 s shift': day('2026-11-16', [closedShift('a', '2026-11-16T08:00:00+01:00', '2026-11-16T08:00:01+01:00', 1)]),
    '43 h+ day': day('2026-11-16', [closedShift('a', '2026-11-16T00:00:00+01:00', '2026-11-17T19:49:00+01:00', 157740)]),
    'closed + open': day('2026-11-16', [closedShift('a', '2026-11-16T08:00:00+01:00', '2026-11-16T09:00:00+01:00', 3600), openShift('b', '2026-11-16T10:00:00+01:00')]),
  }
  for (const [name, d] of Object.entries(cases)) {
    it(name, () => expect(tipTotal(d, '2026-11-22').toLowerCase()).toBe(cardTotal(d).toLowerCase()))
  }

  // Product change (D-088): the tooltip always shows a time, so an open-shift-only day reads "0 min" plus a note,
  // where the day card says "nothing counted yet".
  it('open shift only: 0 min and "still open"', () => {
    const d = day('2026-11-16', [openShift('a', '2026-11-16T08:00:00+01:00')])
    expect(cardTotal(d)).toBe('nothing counted yet')
    expect(text(weekDays(week(d.date, '2026-11-22', [d]))[0])).toBe('Monday · 0 min · 1 shift, still open')
  })

  // A closed shift shorter than 1 s is valid (DB CHECK is ClockOut > ClockIn) and the API truncates it to
  // durationSeconds 0. The day card says "0 s"; the tooltip used to say "Nothing counted yet" as if it were open.
  it('a closed sub-second shift (durationSeconds 0) reads the same as the day card (QA-49)', () => {
    const d = day('2026-11-16', [closedShift('a', '2026-11-16T08:00:00.100+01:00', '2026-11-16T08:00:00.900+01:00', 0)])
    expect(cardTotal(d)).toBe('0 s')
    expect(tipTotal(d, '2026-11-22')).toBe('0 s')
  })
})

describe('accessible names and the tooltip', () => {
  const w = week('2026-11-16', '2026-11-22', [
    day('2026-11-18', [closedShift('a', '2026-11-18T08:00:00+01:00', '2026-11-18T16:00:00+01:00', 28800)]),
    day('2026-11-19', [openShift('b', '2026-11-19T08:00:00+01:00')]),
  ])
  const days = weekDays(w)

  it('every day, including 0-time days, is a named img whose name is the tooltip text', () => {
    const out = html(<BarChart days={days} />)
    expect(out.match(/role="img"/g)).toHaveLength(7)
    for (const d of days) expect(out.match(new RegExp(`aria-label="${text(d)}"`, 'g'))).toHaveLength(1)
  })

  it('the visible weekday labels are hidden from screen readers so nothing is read twice', () => {
    const out = html(<BarChart days={days} short />)
    expect(out.match(/<span aria-hidden="true" class="text-xs[^"]*">[A-Z]<\/span>/g)).toHaveLength(7)
  })

  it('the tooltip card is aria-hidden, not focusable and has no live region (static check)', () => {
    const tip = src('./BarChart.jsx').split('function Tip')[1]
    expect(tip).toContain('aria-hidden="true"')
    expect(tip).not.toMatch(/tabIndex|aria-live|role=/)
  })
})

describe('roving tab stop', () => {
  const days = weekDays(week('2026-11-16', '2026-11-22', []))
  const stopName = (out) => /aria-label="([^"]*)" tabindex="0"/.exec(out)?.[1]

  it('exactly one tab stop for every highlight case', () => {
    for (const highlight of [undefined, () => false, () => true, (d) => d.date === '2026-11-22', (d) => d.date === '2026-12-01']) {
      expect(html(<BarChart days={days} highlight={highlight} />).match(/tabindex="0"/g)).toHaveLength(1)
    }
  })

  it('starts on today on the Clock page, on Monday when today is outside the shown week (D-088)', () => {
    expect(stopName(html(<BarChart days={days} highlight={(d) => d.date === '2026-11-22'} />))).toBe('Sunday · 0 min')
    expect(stopName(html(<BarChart days={days} highlight={(d) => d.date === '2026-11-30'} />))).toBe('Monday · 0 min')
    expect(stopName(html(<BarChart days={days} />))).toBe('Monday · 0 min')
  })

  it('arrows clamp at both ends (no wrap), Home/End jump, other keys are ignored', () => {
    for (const k of ['ArrowLeft', 'ArrowUp']) expect(nextColumn(k, 0, 7)).toBe(0)
    for (const k of ['ArrowRight', 'ArrowDown']) expect(nextColumn(k, 6, 7)).toBe(6)
    expect(nextColumn('ArrowDown', 3, 7)).toBe(4)
    expect(nextColumn('ArrowUp', 3, 7)).toBe(2)
    expect(nextColumn('Home', 0, 7)).toBe(0)
    expect(nextColumn('End', 6, 7)).toBe(6)
    for (const k of ['Enter', ' ', 'Escape', 'Tab', 'PageDown', 'a']) expect(nextColumn(k, 3, 7)).toBeNull()
  })

  // Alt+ArrowLeft / Alt+ArrowRight are the browser's Back / Forward on Windows and Linux. The handler calls
  // preventDefault() used to fire for any arrow without looking at modifiers, swallowing Back while a day had focus.
  it('the key handler leaves modified keys (Alt/Ctrl/Meta + arrows) to the browser (QA-50)', () => {
    const handler = src('./BarChart.jsx').split('onKeyDown')[1].split('}}')[0]
    expect(handler).toMatch(/altKey|ctrlKey|metaKey/)
  })

  it('edge columns anchor the tooltip to the outer edge', () => {
    expect(tipAlign(0, 7)).toBe('start')
    expect(tipAlign(6, 7)).toBe('end')
    expect(tipAlign(3, 7)).toBe('center')
  })
})

describe('clock rule, warnings and themes', () => {
  afterEach(() => vi.restoreAllMocks())

  it('chart code reads no clock and computes no durations itself', () => {
    for (const f of ['./BarChart.jsx', './barChartNav.js', '../pages/history/labels.js']) {
      const s = src(f)
      expect(s).not.toMatch(/new Date\(\s*\)|Date\.now\(|performance\.now\(/)
      expect(s).not.toMatch(/Date\.parse\(/)
    }
  })

  it('renders without React warnings (keys, props)', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    html(<BarChart days={weekDays(week('2026-11-16', '2026-11-22', []))} short highlight={() => true} />)
    expect(spy).not.toHaveBeenCalled()
  })

  it('uses theme tokens only, no hard-coded colours that break light or dark', () => {
    const s = src('./BarChart.jsx')
    expect(s).not.toMatch(/#[0-9a-fA-F]{3,8}\b|rgb\(|hsl\(/)
    expect(s).not.toMatch(/\b(bg|text|border|ring|shadow)-(white|black|gray|slate|zinc|neutral|blue|red)\b/)
    const css = src('../app.css')
    for (const token of ['accent', 'accent-strong', 'faint', 'bar-empty', 'surface', 'control', 'ink', 'muted']) {
      expect(css.match(new RegExp(`--color-${token}:`, 'g'))).toHaveLength(2) // light + dark
    }
    expect(css.match(/--shadow-pop:/g)).toHaveLength(2)
  })

  it('the fade only runs without reduced motion', () => {
    expect(src('./BarChart.jsx')).toMatch(/motion-safe:animate-tip-in/)
    expect(src('./BarChart.jsx')).not.toMatch(/(?<!motion-safe:)animate-tip-in/)
  })
})
