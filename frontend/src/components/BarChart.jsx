import { useEffect, useReducer, useRef, useState } from 'react'
import { dayBarParts } from '../pages/history/labels.js'
import { TIP_START, nextColumn, shownDay, tipAlign, tipReducer } from './barChartNav.js'

const ALIGN = { start: 'left-0', end: 'right-0', center: 'left-1/2 -translate-x-1/2' }

/**
 * Hours per weekday as plain divs. `days` come from weekDays(); `highlight(day)` makes a label bold.
 * Each day column is one image whose name equals its tooltip; one tab stop, arrows move between days (D-088).
 */
export default function BarChart({ days, short = false, highlight = () => false, className = '' }) {
  const max = Math.max(0, ...days.map((d) => d.seconds))
  const [tip, send] = useReducer(tipReducer, TIP_START)
  const [stop, setStop] = useState(null)
  const root = useRef(null)
  const cols = useRef([])

  const tabStop = stop !== null && stop < days.length ? stop : Math.max(0, days.findIndex(highlight))
  const shown = shownDay(tip)

  // Escape anywhere or a press outside hides the tooltip and releases a pinned day (WCAG 1.4.13 dismissible).
  useEffect(() => {
    if (shown === null) return undefined
    const onKey = (e) => e.key === 'Escape' && send({ type: 'escape' })
    const onDown = (e) => !root.current?.contains(e.target) && send({ type: 'outside' })
    document.addEventListener('keydown', onKey)
    document.addEventListener('pointerdown', onDown)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('pointerdown', onDown)
    }
  }, [shown])

  return (
    <div ref={root} role="group" aria-label="Hours per day" className={`grid grid-cols-7 gap-2 ${className}`}>
      {days.map((d, i) => {
        const parts = dayBarParts(d)
        const on = shown === i
        const strong = d.seconds > 0 || highlight(d) || on
        const bar = d.seconds > 0 ? `min-h-1.5 rounded-md ${on ? 'bg-accent-strong' : 'bg-accent'}` : `h-1 rounded ${on ? 'bg-faint' : 'bg-bar-empty'}`
        return (
          <div
            key={d.date}
            ref={(el) => {
              cols.current[i] = el
            }}
            role="img"
            aria-label={parts.join(' · ')}
            tabIndex={i === tabStop ? 0 : -1}
            className="flex h-full cursor-default flex-col items-center gap-1.5 rounded-md outline-offset-4"
            onPointerEnter={() => send({ type: 'enter', index: i })}
            onPointerLeave={() => send({ type: 'leave', index: i })}
            onFocus={() => {
              setStop(i)
              send({ type: 'focus', index: i })
            }}
            onBlur={() => send({ type: 'blur', index: i })}
            onClick={() => send({ type: 'click', index: i })}
            onKeyDown={(e) => {
              if (e.altKey || e.ctrlKey || e.metaKey) return // Alt+Left is the browser's Back
              const j = nextColumn(e.key, i, days.length)
              if (j === null) return
              e.preventDefault()
              send({ type: 'key', index: j }) // re-shows even when j is the day already focused
              cols.current[j]?.focus()
            }}
          >
            <div className="flex w-full flex-1 items-end">
              <span
                className={`relative w-full transition-colors ${bar}`}
                style={d.seconds > 0 ? { height: `${Math.max(6, Math.round((d.seconds / max) * 100))}%` } : undefined}
              >
                {on && <Tip parts={parts} align={tipAlign(i, days.length)} />}
              </span>
            </div>
            <span aria-hidden="true" className={`text-xs ${strong ? 'font-semibold text-ink' : 'text-muted'}`}>
              {short ? d.letter : d.short}
            </span>
          </div>
        )
      })}
    </div>
  )
}

/** The small card above the bar; hidden from screen readers because the column's name says the same. */
function Tip({ parts, align }) {
  const [name, ...rest] = parts
  return (
    <span
      aria-hidden="true"
      data-chart-tip=""
      className={`absolute bottom-full z-10 mb-1.5 w-max rounded-control border border-control bg-surface px-3 py-2 text-left shadow-pop motion-safe:animate-tip-in ${ALIGN[align]}`}
    >
      <span className="block text-xs font-medium text-muted">{name}</span>
      <span className="block text-sm font-semibold whitespace-nowrap text-ink tabular-nums">{rest.join(' · ')}</span>
    </span>
  )
}
