import { formatDuration, needsOffset, zurichOffsetLabel, zurichTime } from '../../lib/zurich.js'

/** Week and day totals always read "9 h 09 min" (also "0 h 00 min"), counted from closed shifts. */
export function formatTotal(seconds) {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return '0 h 00 min'
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return `${h} h ${String(m).padStart(2, '0')} min`
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
    if (texts[flag]) badges.push({ key: flag, tone: 'warning', text: texts[flag] })
  }
  return badges
}

/** "9 h 09 min", or the soft explanation for an open shift. */
export function shiftDurationText(shift, { manager = false } = {}) {
  if (shift.open) return manager ? 'Not counted while open' : 'Not counted until you clock out'
  return formatDuration(shift.durationSeconds) ?? ''
}
