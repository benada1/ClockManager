// Formatting helpers for time values the SERVER already resolved.
//
// The time lab receives the Zurich wall-clock time and its offset as separate strings
// (`zurichLocal` = "2026-11-17T13:02:58.446011", `zurichOffset` = "+01:00"). We show them
// as text and never turn them into a Date: the browser's time zone and clock play no part,
// and the offset is always visible, so the repeated 02:00-03:00 hour in October stays
// unambiguous (02:30 UTC+02:00 vs 02:30 UTC+01:00).

const LOCAL_RE = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.(\d+))?$/
const UTC_RE = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.(\d+))?(?:Z|\+00:00)$/
const OFFSET_RE = /^[+-]\d{2}:\d{2}$/

const ZURICH_ABBREVIATIONS = { '+01:00': 'CET', '+02:00': 'CEST' }

// Truncate (not round) fractional seconds to milliseconds, so the text never shows a later instant.
const millis = (fraction) => (fraction ? `.${fraction.slice(0, 3).padEnd(3, '0')}` : '')

/**
 * Splits a server-provided Zurich local time + offset into display parts.
 * Returns null when the strings are missing or not in the expected ISO shape.
 */
export function zurichParts(local, offset) {
  if (typeof local !== 'string' || typeof offset !== 'string') return null
  const m = LOCAL_RE.exec(local)
  if (!m || !OFFSET_RE.test(offset)) return null
  return {
    date: m[1],
    time: `${m[2]}${millis(m[3])}`,
    offsetLabel: `UTC${offset}`,
    abbreviation: ZURICH_ABBREVIATIONS[offset] ?? null,
  }
}

/** "2026-11-17T12:02:58.446011Z" -> "2026-11-17 12:02:58.446 UTC"; unknown shapes are shown as-is. */
export function formatUtcInstant(utc) {
  if (typeof utc !== 'string') return null
  const m = UTC_RE.exec(utc)
  return m ? `${m[1]} ${m[2]}${millis(m[3])} UTC` : utc
}

export function formatMs(value) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-'
  return `${Number.isInteger(value) ? value : value.toFixed(1)} ms`
}
