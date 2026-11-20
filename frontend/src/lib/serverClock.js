// The Clock page's "now", built only from the server's trusted time (status.serverNow, D-035) and a
// monotonic stopwatch: now = anchor + performance.now(), anchor = serverNow.utc + ageMs - syncAt (D-084).
// The device's wall clock is never read. Formatting is Europe/Zurich via Intl.

const ZONE = 'Europe/Zurich'
const partsFmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  weekday: 'short',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
  timeZoneName: 'shortOffset',
})
const longDateFmt = new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })

// Without an offset the browser's zone would decide, so such a value is refused.
const parseServer = (iso) => (typeof iso === 'string' && /(Z|[+-]\d{2}:?\d{2})$/i.test(iso) ? Date.parse(iso) : NaN)

/** Server ms at monotonic time 0, or null when the status has no trusted time. */
export function anchorFrom(status, syncAt) {
  const now = status?.serverNow
  if (typeof syncAt !== 'number') return null
  const t = parseServer(now?.utc) // a server string, not the current time
  if (!Number.isFinite(t)) return null
  return t + (Number.isFinite(now.ageMs) ? now.ageMs : 0) - syncAt
}

export const NO_ANCHOR = { syncAt: null, anchor: null }

/**
 * The anchor to show after a status change. A punch clears `serverNow` but keeps `syncAt`: the old anchor
 * is still right, so it is kept until the next refresh. A new sync without a trusted time clears it.
 * Returns `prev` itself when nothing changed (safe to compare with !==).
 */
export function nextAnchor(prev, status, syncAt) {
  const fresh = anchorFrom(status, syncAt)
  if (fresh !== null) return prev.syncAt === syncAt && prev.anchor === fresh ? prev : { syncAt, anchor: fresh }
  if (syncAt === prev.syncAt) return prev
  return { syncAt, anchor: null }
}

/** Display parts of a server-derived instant in Zurich. */
export function zurichClock(ms) {
  if (!Number.isFinite(ms)) return null
  const d = new Date(ms) // a server-derived instant, not the device clock
  const p = Object.fromEntries(partsFmt.formatToParts(d).map((x) => [x.type, x.value]))
  return {
    hm: `${p.hour}:${p.minute}`,
    ss: p.second,
    hour: Number(p.hour),
    date: `${p.year}-${p.month}-${p.day}`,
    dateLabel: longDateFmt.format(d),
    offsetLabel: (p.timeZoneName ?? '').replace('GMT', 'UTC'),
    // Last Sunday of March or October: the offset is shown so a repeated hour can be told apart.
    dstDay: p.weekday === 'Sun' && Number(p.day) >= 25 && (p.month === '03' || p.month === '10'),
  }
}

/** Greeting for a Zurich hour (0-23). */
export function greetingFor(hour) {
  if (hour >= 5 && hour < 12) return 'Good morning'
  if (hour >= 12 && hour < 18) return 'Good afternoon'
  return 'Good evening'
}

/** Ms since a server instant (ISO with offset), never below 0; null when either side is missing. */
export function elapsedSince(nowMs, iso) {
  const start = parseServer(iso)
  if (!Number.isFinite(nowMs) || !Number.isFinite(start)) return null
  return Math.max(0, nowMs - start)
}
