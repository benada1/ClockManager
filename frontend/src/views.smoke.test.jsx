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
import DecisionPanel from './pages/time-lab/DecisionPanel.jsx'
import ProviderCard from './pages/time-lab/ProviderCard.jsx'
import CrossCheckPanel from './pages/time-lab/CrossCheckPanel.jsx'

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
    expect(html(<BarChart days={weekDays(data)} />)).toContain('Hours per day: Monday none')
    const w = html(<WeekView history={{ data, loading: false, failure: null }} header={<h1>H</h1>} />)
    expect(w).toContain('Week total')
    expect(w).toContain('2 shifts · 2 flagged')
  })

  it('time lab panels', () => {
    const p = { role: 'primary', name: 'time.now', outcome: 'ok', roundTripMs: 300, utc: '2026-11-17T12:02:58.446Z', zurichOffset: '+01:00', zurichLocal: '2026-11-17T13:02:58.446', rawPayload: '{}' }
    expect(html(<ProviderCard provider={p} />)).toContain('time.now')
    expect(html(<DecisionPanel decision={{ result: 'accepted', provider: 'time.now', utc: p.utc, zurichLocal: p.zurichLocal, zurichOffset: '+01:00' }} />)).toContain('Accepted')
    expect(html(<CrossCheckPanel crossCheck={null} />)).toContain('Not cross-checked')
  })

  it('app shell renders', () => {
    expect(html(<AuthProvider><App /></AuthProvider>)).toContain('Clock Manager')
  })
})
