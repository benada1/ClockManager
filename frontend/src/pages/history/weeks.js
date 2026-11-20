// Date arithmetic on "yyyy-MM-dd" strings (Zurich dates from the server). UTC math keeps
// daylight-saving changes out of it, and no clock is read.

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/

function parts(date) {
  const m = typeof date === 'string' ? DATE_RE.exec(date) : null
  return m ? { y: Number(m[1]), m: Number(m[2]), d: Number(m[3]) } : null
}

/** "2026-12-28" + 7 -> "2027-01-04". Returns null for a value that is not a yyyy-MM-dd date. */
export function addDays(date, days) {
  const p = parts(date)
  if (!p) return null
  const t = new Date(Date.UTC(p.y, p.m - 1, p.d + days))
  const pad = (n) => String(n).padStart(2, '0')
  return `${t.getUTCFullYear()}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`
}

/** The week before (-1) or after (+1) the given range, as an explicit { from, to }. */
export function neighbourWeek({ from, to }, direction) {
  const next = { from: addDays(from, 7 * direction), to: addDays(to, 7 * direction) }
  return next.from && next.to ? next : null
}

const dayMonth = (p) => `${p.d} ${MONTHS[p.m - 1]}`

/** "16 Nov – 22 Nov 2026", or "28 Dec 2026 – 3 Jan 2027" across a year change. */
export function formatRange(from, to) {
  const a = parts(from)
  const b = parts(to)
  if (!a || !b) return ''
  return a.y === b.y ? `${dayMonth(a)} – ${dayMonth(b)} ${b.y}` : `${dayMonth(a)} ${a.y} – ${dayMonth(b)} ${b.y}`
}

/** "Tuesday 17 Nov". */
export function formatDayHeading(date) {
  const p = parts(date)
  if (!p) return date ?? ''
  const weekday = WEEKDAYS[new Date(Date.UTC(p.y, p.m - 1, p.d)).getUTCDay()]
  return `${weekday} ${dayMonth(p)}`
}

const weekdayIndex = (p) => new Date(Date.UTC(p.y, p.m - 1, p.d)).getUTCDay() // 0 = Sunday; a given date, not the clock

/** The Monday-to-Sunday week that holds a Zurich date: '2026-12-31' -> { from: '2026-12-28', to: '2027-01-03' }. */
export function weekOf(date) {
  const p = parts(date)
  if (!p) return null
  const from = addDays(date, -((weekdayIndex(p) + 6) % 7))
  return { from, to: addDays(from, 6) }
}

/** Seven days of a loaded week, Monday first, with each day's counted seconds (0 when no shift). */
export function weekDays(data) {
  if (!parts(data?.from)) return []
  return Array.from({ length: 7 }, (_, i) => {
    const date = addDays(data.from, i)
    const day = (data.days ?? []).find((d) => d.date === date)
    const name = WEEKDAYS[weekdayIndex(parts(date))]
    return { date, name, short: name.slice(0, 3), letter: name[0], seconds: day?.totalSeconds ?? 0, shifts: day?.shifts?.length ?? 0 }
  })
}

/** '16 – 22 Nov', '30 Nov – 6 Dec' or '28 Dec – 3 Jan' (short form for the small week card). */
export function formatShortRange(from, to) {
  const a = parts(from)
  const b = parts(to)
  if (!a || !b) return ''
  return a.m === b.m && a.y === b.y ? `${a.d} – ${dayMonth(b)}` : `${dayMonth(a)} – ${dayMonth(b)}`
}
