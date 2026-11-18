import { needsOffset, zurichDay, zurichOffsetLabel, zurichTime } from '../../lib/zurich.js'

export const FIELD_NAMES = { clockIn: 'Clock in', clockOut: 'Clock out' }
const FIELD_KEYS = { clockIn: 'clockIn', ClockIn: 'clockIn', clockOut: 'clockOut', ClockOut: 'clockOut' }
/** The server may send "ClockIn" or "clockIn". */
export const fieldKey = (field) => FIELD_KEYS[field] ?? null

const KIND_TEXT = {
  forgotten_clock_out: 'Forgot to clock out',
  long_shift: 'Unusually long shift',
  short_shift: 'Very short shift',
}
export const anomalyText = (kind) => KIND_TEXT[kind] ?? 'Needs a look'

const FLAG_TEXT = {
  forgotten_clock_out: 'Forgot to clock out',
  long_shift: 'Long shift',
  short_shift: 'Very short shift',
}
/** Warning pills for an employee row. Unknown flags are ignored. */
export const employeeFlagTexts = (flags) => (flags ?? []).filter((f) => FLAG_TEXT[f]).map((f) => ({ key: f, text: FLAG_TEXT[f] }))

/** "Clocked in since Tue 18 Nov, 08:03" or "Not clocked in". */
export function employeeStatusText(employee) {
  if (employee.status !== 'clocked_in') return 'Not clocked in'
  const iso = employee.since?.zurich
  const t = zurichTime(iso)
  return t ? `Clocked in since ${zurichDay(iso)}, ${t}` : 'Clocked in'
}

/** "08:03", with the date when it differs from `sameDayAs`, and the offset when needed. */
export function pointText(iso, { withOffset = false, sameDayAs = null } = {}) {
  const t = zurichTime(iso)
  if (!t) return null
  const day = sameDayAs && zurichDay(sameDayAs) !== zurichDay(iso) ? `${zurichDay(iso)}, ` : ''
  const off = withOffset && zurichOffsetLabel(iso) ? ` ${zurichOffsetLabel(iso)}` : ''
  return `${day}${t}${off}`
}

/**
 * One line of the change history:
 * "Clock out changed from 17:12 to 18:05 (Zurich time) by Maria Manager on Wed 18 Nov, 09:40."
 */
export function correctionSentence(c) {
  const name = FIELD_NAMES[fieldKey(c.field)] ?? 'Time'
  const oldIso = c.old?.zurich ?? null
  const newIso = c.new?.zurich ?? null
  const withOffset = needsOffset(oldIso, newIso)
  const to = pointText(newIso, { withOffset, sameDayAs: oldIso })
  const who = c.by?.displayName ? ` by ${c.by.displayName}` : ''
  const whenTime = zurichTime(c.at?.zurich)
  const when = whenTime ? ` on ${zurichDay(c.at.zurich)}, ${whenTime}` : ''
  const what = oldIso
    ? `${name} changed from ${pointText(oldIso, { withOffset })} to ${to} (Zurich time)`
    : `${name} set to ${to} (Zurich time)`
  return `${what}${who}${when}.${oldIso ? '' : ' Before, the shift was still open.'}`
}

/** The value before any correction of this field: { changed: false } | { changed: true, iso: string | null }. */
export function originalValue(corrections, field) {
  const first = (corrections ?? []).find((c) => fieldKey(c.field) === field)
  return first ? { changed: true, iso: first.old?.zurich ?? null } : { changed: false, iso: null }
}

/** "Saved. Clock out is now 18:05 (Zurich time)." from the 201 body. */
export function savedText(detail, field) {
  const iso = detail?.shift?.[field]?.zurich
  const t = pointText(iso, { withOffset: needsOffset(iso), sameDayAs: detail?.shift?.clockIn?.zurich })
  return t ? `Saved. ${FIELD_NAMES[field]} is now ${t} (Zurich time).` : 'Saved.'
}

/** "Original: 16:00" / "Original: still open" for a changed field, else null. */
export function originalText(corrections, field, currentIso) {
  const o = originalValue(corrections, field)
  if (!o.changed) return null
  if (!o.iso) return 'Original: still open'
  return `Original: ${pointText(o.iso, { withOffset: needsOffset(o.iso, currentIso), sameDayAs: currentIso })}`
}
