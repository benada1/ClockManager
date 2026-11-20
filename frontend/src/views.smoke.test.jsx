// Server-side render of the main views with real-shaped data: catches render errors without a browser (no DOM, no effects).
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import BarChart from './components/BarChart.jsx'
import AuthProvider from './auth/AuthProvider.jsx'
import App from './App.jsx'
import DayCard from './pages/history/DayCard.jsx'
import WeekView from './pages/history/WeekView.jsx'
import { weekDays } from './pages/history/weeks.js'
import ClockCard from './pages/punch/ClockCard.jsx'
import LastShiftCard from './pages/punch/LastShiftCard.jsx'

const IN = { utc: '2026-11-18T07:03:11.000Z', zurich: '2026-11-18T08:03:11.000+01:00' }
const OUT = { utc: '2026-11-19T05:00:00.000Z', zurich: '2026-11-19T06:00:00.000+01:00' }
const closed = { shiftId: 'a', clockIn: IN, clockOut: OUT, durationSeconds: 78409, flags: ['long_shift'] }
const html = (el) => renderToStaticMarkup(el)

describe('views render', () => {
  it('clock card states', () => {
    const anchor = { syncAt: 1, anchor: Date.parse('2026-11-18T09:41:07Z') }
    const out = html(<ClockCard status={{ openShift: null, lastClosedShift: closed, flags: [] }} anchor={anchor} busy={false} stuck={false} problem={null} justClosedId="a" onPress={() => {}} />)
    expect(out).toContain('Clocked out and saved')
    const open = html(<ClockCard status={{ openShift: { shiftId: 'b', clockIn: IN }, lastClosedShift: null, flags: [] }} anchor={anchor} busy problem={{ title: 'X', text: 'Y', retry: true }} onPress={() => {}} />)
    expect(open).toContain('On shift')
    expect(open).toContain('Shift started at')
    const none = html(<ClockCard status={{ openShift: null, lastClosedShift: null }} anchor={{ syncAt: 1, anchor: null }} onPress={() => {}} />)
    expect(none).toContain('Server time unavailable')
  })

  it('last shift, day card, bars, week view', () => {
    expect(html(<LastShiftCard status={{ lastClosedShift: closed }} />)).toContain('Thu')
    expect(html(<LastShiftCard status={{ lastClosedShift: null }} />)).toContain('No finished shift')
    const day = { date: '2026-11-18', totalSeconds: 78409, notes: ['dst_change'], shifts: [{ ...closed, endsNextDay: true }, { shiftId: 'c', clockIn: IN, clockOut: null, open: true, flags: ['forgotten_clock_out'] }] }
    const d = html(<ul><DayCard day={day} manager renderAction={() => <button>Correct</button>} /></ul>)
    expect(d).toContain('Wednesday 18 Nov')
    expect(d).toContain('Since 08:03')
    const data = { from: '2026-11-16', to: '2026-11-22', totalSeconds: 78409, days: [day] }
    const bars = html(<BarChart days={weekDays(data)} />)
    expect(bars).toContain('aria-label="Hours per day"')
    expect(bars).toContain('aria-label="Monday · No shifts"')
    const w = html(<WeekView history={{ data, loading: false, failure: null }} header={<h1>H</h1>} />)
    expect(w).toContain('Week total')
    expect(w).toContain('2 shifts · 2 flagged')
  })

  it('app shell renders', () => {
    expect(html(<AuthProvider><App /></AuthProvider>)).toContain('Clock Manager')
  })
})
