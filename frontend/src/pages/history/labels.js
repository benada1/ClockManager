import { formatDuration, needsOffset, zurichDay, zurichOffsetLabel, zurichTime } from '../../lib/zurich.js'

/** Week and day totals always read "9 h 09 min" (also "0 h 00 min"), counted from closed shifts. */
export function formatTotal(seconds) {
  const { h, m } = totalParts(seconds)
  return `${h} h ${m} min`
}

/** The numbers of a total for the big display: 157740 -> { h: '43', m: '49' }. */
export function totalParts(seconds) {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return { h: '0', m: '00' }
  return { h: String(Math.floor(seconds / 3600)), m: String(Math.floor((seconds % 3600) / 60)).padStart(2, '0') }
}

/** '7 shifts · 6 flagged' for a loaded week (flagged = has a server flag). */
export function weekCountsText(data) {
  const shifts = (data?.days ?? []).flatMap((d) => d.shifts ?? [])
  const flagged = shifts.filter((s) => (s.flags ?? []).length > 0).length
  const count = `${shifts.length} ${shifts.length === 1 ? 'shift' : 'shifts'}`
  return flagged ? `${count} · ${flagged} flagged` : count
}

/**
 * Bar-chart tooltip for one weekDays() entry: ['Wednesday', '43 h 49 min', '2 shifts'] or ['Tuesday', 'No shifts'].
 * Joined with ' · ' it is also the column's accessible name, so both always match.
 */
export function dayBarParts(day) {
  if (!day.shifts) return [day.name, 'No shifts']
  const count = `${day.shifts} ${day.shifts === 1 ? 'shift' : 'shifts'}`
  // Same wording as the day card: an open shift is not counted yet.
  return [day.name, day.seconds > 0 ? formatDuration(day.seconds) : 'Nothing counted yet', count]
}

/** Plain-words notes for a day. Unknown notes are ignored. */
export function dayNoteTexts(day) {
  return (day.notes ?? []).filter((n) => n === 'dst_change').map(() => 'Clocks changed this night')
}

/** "08:03 to 17:12 (Zurich time)"; around a clock change each time carries its offset. */
export function shiftTimesText(shift) {
  const inIso = shift.clockIn?.zurich
  const outIso = shift.clockOut?.zurich ?? null
  const withOffset = needsOffset(inIso, outIso)
  const one = (iso) => {
    const t = zurichTime(iso)
    if (!t) return '–'
    return withOffset && zurichOffsetLabel(iso) ? `${t} ${zurichOffsetLabel(iso)}` : t
  }
  return outIso ? `${one(inIso)} to ${one(outIso)} (Zurich time)` : `Since ${one(inIso)} (Zurich time)`
}

const FLAG_TEXT = {
  long_shift: 'Unusually long shift',
  short_shift: 'Very short shift',
  forgotten_clock_out: 'Open for a very long time: you may have forgotten to clock out',
}

const MANAGER_FLAG_TEXT = { ...FLAG_TEXT, forgotten_clock_out: 'Forgot to clock out' }

/** Small labels for one shift: [{ key, tone, text }]. Always words, never color alone. `manager` = the manager's view. */
export function shiftBadges(shift, { manager = false } = {}) {
  const texts = manager ? MANAGER_FLAG_TEXT : FLAG_TEXT
  const badges = []
  if (shift.open) badges.push({ key: 'open', tone: 'info', text: 'Still open' })
  if (shift.endsNextDay) badges.push({ key: 'next', tone: 'info', text: 'Ends next day' })
  if (shift.corrected) badges.push({ key: 'corrected', tone: 'info', text: manager ? 'Corrected' : 'Corrected by a manager' })
  for (const flag of shift.flags ?? []) {
    if (texts[flag]) badges.push({ key: flag, tone: flag === 'forgotten_clock_out' ? 'danger' : 'warning', text: texts[flag] })
  }
  return badges
}

/** "9 h 09 min", or the soft explanation for an open shift. */
export function shiftDurationText(shift, { manager = false } = {}) {
  if (shift.open) return manager ? 'Not counted while open' : 'Not counted until you clock out'
  return formatDuration(shift.durationSeconds) ?? ''
}

/**
 * Parts for the '08:03 → 17:12 Fri' row: start, end (null while open) and the clock-out weekday when it is on a later
 * Zurich day. Around a clock change each time carries its offset.
 */
export function shiftTimeParts(shift) {
  const inIso = shift.clockIn?.zurich
  const outIso = shift.clockOut?.zurich ?? null
  const withOffset = needsOffset(inIso, outIso)
  const one = (iso) => {
    const t = zurichTime(iso)
    if (!t) return '–'
    return withOffset && zurichOffsetLabel(iso) ? `${t} ${zurichOffsetLabel(iso)}` : t
  }
  const outDay = outIso ? zurichDay(outIso) : null
  return { start: one(inIso), end: outIso ? one(outIso) : null, endDay: outDay && outDay !== zurichDay(inIso) ? outDay.split(' ')[0] : null }
}
