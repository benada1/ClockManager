import Banner from '../../components/Banner.jsx'
import { formatDuration, needsOffset, zoneLabel, zurichDay, zurichTime } from '../../lib/zurich.js'

const MORE_THAN_USUAL =
  'Clocking out records the moment you press the button, so tell your manager if you really finished earlier.'

/** The words under the state line. Every time shown here is a server value. */
export default function ShiftSummary({ status, justClosedId }) {
  const open = status.openShift
  const last = status.lastClosedShift

  if (open) {
    const t = open.clockIn.zurich
    return (
      <>
        <p className="text-base text-ink">
          You clocked in at <strong>{zurichTime(t)}</strong> {zoneLabel(t, needsOffset(t))}
        </p>
        <p className="text-sm text-muted">{zurichDay(t)}</p>
      </>
    )
  }

  if (!last) return <p className="text-base text-muted">Press the button when you start work.</p>

  const tIn = last.clockIn.zurich
  const tOut = last.clockOut?.zurich
  const showOffset = needsOffset(tIn, tOut)
  const worked = formatDuration(last.durationSeconds)

  if (last.shiftId === justClosedId) {
    return (
      <>
        <p className="text-base text-ink">
          You clocked out at <strong>{zurichTime(tOut)}</strong> {zoneLabel(tOut, showOffset)}
        </p>
        {worked && <p className="text-base text-ink">Worked {worked}</p>}
        <p className="text-sm text-muted">
          Started at {zurichTime(tIn)}, {zurichDay(tIn)}
        </p>
      </>
    )
  }

  return (
    <>
      <p className="text-base text-muted">
        Your last shift: {zurichTime(tIn)} to {zurichTime(tOut)} {zoneLabel(tOut, showOffset)}, {zurichDay(tIn)}
      </p>
      {worked && <p className="text-sm text-muted">Worked {worked}</p>}
    </>
  )
}

/** Notices for shifts the server flagged. Nothing is ever closed for the user. */
export function ShiftNotices({ status }) {
  const forgotten = status.flags?.includes('forgotten_clock_out')
  const longOpen = status.openShift?.flags?.includes('long_shift')
  const longLast = !status.openShift && status.lastClosedShift?.flags?.includes('long_shift')
  return (
    <>
      {(forgotten || longOpen) && (
        <Banner tone="warning" title="This shift has been open for a very long time.">
          You may have forgotten to clock out. {MORE_THAN_USUAL}
        </Banner>
      )}
      {longLast && (
        <Banner tone="warning" title="Your last shift was unusually long.">
          If that is not right, tell your manager.
        </Banner>
      )}
    </>
  )
}
