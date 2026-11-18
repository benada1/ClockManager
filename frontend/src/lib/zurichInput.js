// Turns a typed Zurich wall time ("2026-10-25" + "02:30") into an ISO string with the right offset.
// Pure: only Date.UTC arithmetic and Intl, never the browser clock.

const ZONE = 'Europe/Zurich'
const partsFmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})
const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/
const TIME_RE = /^(\d{2}):(\d{2})(?::\d{2})?$/

// Summer time first: in the repeated October hour the +02:00 moment comes first.
const CANDIDATES = [
  { hours: 2, offset: '+02:00', label: 'First time (summer time, UTC+2)' },
  { hours: 1, offset: '+01:00', label: 'Second time (winter time, UTC+1)' },
]

function wallOf(ms) {
  const p = Object.fromEntries(partsFmt.formatToParts(new Date(ms)).map((x) => [x.type, x.value]))
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`
}

/**
 * { status: 'ok', iso } | { status: 'ambiguous', choices: [{ offset, label, iso }] } | { status: 'gap' } | { status: 'invalid' }.
 * 'ambiguous' = the repeated October hour, 'gap' = the skipped March hour.
 */
export function resolveZurichWallTime(date, time) {
  const d = DATE_RE.exec(date ?? '')
  const t = TIME_RE.exec(time ?? '')
  if (!d || !t) return { status: 'invalid' }
  const [y, mo, da, h, mi] = [d[1], d[2], d[3], t[1], t[2]].map(Number)
  const base = Date.UTC(y, mo - 1, da, h, mi)
  const check = new Date(base)
  if (h > 23 || mi > 59 || check.getUTCFullYear() !== y || check.getUTCMonth() !== mo - 1 || check.getUTCDate() !== da) {
    return { status: 'invalid' }
  }
  const wall = `${d[0]}T${t[1]}:${t[2]}`
  const matches = CANDIDATES.filter((c) => wallOf(base - c.hours * 3600000) === wall).map((c) => ({
    offset: c.offset,
    label: c.label,
    iso: `${wall}:00${c.offset}`,
  }))
  if (matches.length === 0) return { status: 'gap' }
  if (matches.length === 1) return { status: 'ok', iso: matches[0].iso }
  return { status: 'ambiguous', choices: matches }
}

/** A server ISO value as the inputs' { date, time, offset } in Zurich time (to prefill the form). */
export function toZurichInput(iso) {
  if (typeof iso !== 'string') return { date: '', time: '', offset: null }
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return { date: '', time: '', offset: null }
  const wall = wallOf(ms)
  const [y, mo, d, h, mi] = wall.split(/[-T:]/).map(Number)
  const hours = (Date.UTC(y, mo - 1, d, h, mi) - (ms - (((ms % 60000) + 60000) % 60000))) / 3600000
  const offset = CANDIDATES.find((c) => c.hours === hours)?.offset ?? null
  return { date: wall.slice(0, 10), time: wall.slice(11, 16), offset }
}
