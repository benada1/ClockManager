import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { dayBarParts } from '../pages/history/labels.js'
import { weekDays } from '../pages/history/weeks.js'
import BarChart from './BarChart.jsx'
import { TIP_START, nextColumn, shownDay, tipAlign, tipReducer } from './barChartNav.js'

const closed = { shiftId: 'a', open: false, durationSeconds: 157740 }
const open = { shiftId: 'b', open: true }
const data = {
  from: '2026-11-16',
  to: '2026-11-22',
  days: [
    { date: '2026-11-18', totalSeconds: 157740, shifts: [closed, { ...closed, shiftId: 'c' }] },
    { date: '2026-11-19', totalSeconds: 0, shifts: [open] },
    { date: '2026-11-20', totalSeconds: 1200, shifts: [closed] },
  ],
}
const days = weekDays(data)
const text = (d) => dayBarParts(d).join(' · ')

describe('bar chart tooltip text', () => {
  it('says the day total and shift count, like the day cards', () => {
    expect(text(days[2])).toBe('Wednesday · 43 h 49 min · 2 shifts')
    expect(text(days[4])).toBe('Friday · 20 min · 1 shift')
  })

  it('a day with no time still shows a time', () => {
    expect(text(days[1])).toBe('Tuesday · 0 min')
    expect(text(days[6])).toBe('Sunday · 0 min')
    expect(text(days[3])).toBe('Thursday · 0 min · 1 shift, still open')
  })

  it('a closed shift next to the open one is counted; the open one is named', () => {
    const d = weekDays({ ...data, days: [{ date: '2026-11-16', totalSeconds: 3600, shifts: [closed, open] }] })
    expect(text(d[0])).toBe('Monday · 1 h 00 min · 2 shifts, 1 still open')
  })

  it('each column is named with the same text, and no tooltip shows by default', () => {
    const html = renderToStaticMarkup(<BarChart days={days} />)
    for (const d of days) expect(html).toContain(`aria-label="${text(d)}"`)
    expect(html).not.toContain('data-chart-tip')
    expect(html.match(/tabindex="0"/g)).toHaveLength(1)
  })

  it('the tab stop starts on the highlighted day', () => {
    const html = renderToStaticMarkup(<BarChart days={days} highlight={(d) => d.date === '2026-11-20'} />)
    expect(html).toMatch(/aria-label="Friday[^"]*" tabindex="0"/)
  })
})

describe('bar chart tooltip state', () => {
  const run = (...actions) => actions.reduce(tipReducer, TIP_START)

  it('hover and focus show a day; leaving hides it', () => {
    expect(shownDay(run({ type: 'enter', index: 2 }))).toBe(2)
    expect(shownDay(run({ type: 'enter', index: 2 }, { type: 'leave', index: 2 }))).toBeNull()
    expect(shownDay(run({ type: 'focus', index: 4 }))).toBe(4)
  })

  it('Escape after a click releases the pin: hovering another day and leaving does not bring it back (QA-51)', () => {
    const s = run({ type: 'click', index: 2 }, { type: 'escape' }, { type: 'enter', index: 4 }, { type: 'leave', index: 4 })
    expect(shownDay(s)).toBeNull()
  })

  it('after Escape, a key that lands on the same day shows it again (QA-51)', () => {
    const s = run({ type: 'focus', index: 6 }, { type: 'escape' })
    expect(shownDay(s)).toBeNull()
    expect(shownDay(tipReducer(s, { type: 'key', index: 6 }))).toBe(6)
  })

  it('a press outside hides everything', () => {
    expect(shownDay(run({ type: 'click', index: 1 }, { type: 'outside' }))).toBeNull()
  })
})

describe('bar chart keys and placement', () => {
  it('arrows move one day and stop at the ends; Home/End jump', () => {
    expect(nextColumn('ArrowRight', 2, 7)).toBe(3)
    expect(nextColumn('ArrowLeft', 0, 7)).toBe(0)
    expect(nextColumn('ArrowRight', 6, 7)).toBe(6)
    expect(nextColumn('Home', 4, 7)).toBe(0)
    expect(nextColumn('End', 1, 7)).toBe(6)
    expect(nextColumn('Tab', 1, 7)).toBeNull()
  })

  it('outer columns anchor the tooltip to their edge', () => {
    expect([0, 1, 2, 3, 4, 5, 6].map((i) => tipAlign(i, 7))).toEqual(['start', 'start', 'center', 'center', 'center', 'end', 'end'])
  })
})
