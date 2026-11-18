// Display helpers for time strings the SERVER sent (ISO with offset). The browser clock
// is never read: we only format the given value in Europe/Zurich.

const ZONE = 'Europe/Zurich'
const timeFmt = new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
const dateFmt = new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, weekday: 'short', day: 'numeric', month: 'short' })
const dayParts = new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, weekday: 'short', day: 'numeric', month: 'numeric' })

const OFFSET_RE = /([+-])(\d{2}):(\d{2})$/

const parse = (iso) => {
  const d = new Date(iso) // server value, not the current time
  return Number.isNaN(d.getTime()) ? null : d
}

/** "17:12" in Zurich time, or null when the value is missing or unreadable. */
export function zurichTime(iso) {
  const d = typeof iso === 'string' ? parse(iso) : null
  return d ? timeFmt.format(d) : null
}

/** "Tue 18 Nov" in Zurich time. */
export function zurichDay(iso) {
  const d = typeof iso === 'string' ? parse(iso) : null
  return d ? dateFmt.format(d) : null
}

/** "UTC+1" from the offset at the end of the server string. */
export function zurichOffsetLabel(iso) {
  const m = typeof iso === 'string' ? OFFSET_RE.exec(iso) : null
  if (!m) return null
  const hours = Number(m[2])
  return `UTC${m[1]}${hours}${m[3] === '00' ? '' : `:${m[3]}`}`
}

// DST changes happen on the last Sunday of March and October (zurich-time-cases).
function isDstChangeDay(iso) {
  const d = typeof iso === 'string' ? parse(iso) : null
  if (!d) return false
  const p = Object.fromEntries(dayParts.formatToParts(d).map((x) => [x.type, x.value]))
  return p.weekday === 'Sun' && Number(p.day) >= 25 && (p.month === '3' || p.month === '10')
}

/** True when the offset must be shown so two equal-looking times can be told apart. */
export function needsOffset(...isoValues) {
  const values = isoValues.filter(Boolean)
  const offsets = new Set(values.map((v) => OFFSET_RE.exec(v)?.[0]))
  return offsets.size > 1 || values.some(isDstChangeDay)
}

/** "(Zurich time)" or "(Zurich time, UTC+2)". */
export const zoneLabel = (iso, withOffset) =>
  withOffset && zurichOffsetLabel(iso) ? `(Zurich time, ${zurichOffsetLabel(iso)})` : '(Zurich time)'

/** 32969 -> "9 h 09 min"; short shifts "20 s". Comes from the server's durationSeconds. */
export function formatDuration(seconds) {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return null
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (h > 0) return `${h} h ${String(m).padStart(2, '0')} min`
  if (m > 0) return `${m} min`
  return `${Math.floor(seconds)} s`
}
