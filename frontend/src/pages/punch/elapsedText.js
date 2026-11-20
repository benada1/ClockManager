// Text forms of an elapsed time (the ms come from server time, see lib/serverClock.js).

/** 33000000 ms -> "9:10:00". */
export function formatElapsed(ms) {
  const total = Math.floor(ms / 1000)
  const h = Math.floor(total / 3600)
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, '0')
  const s = String(total % 60).padStart(2, '0')
  return `${h}:${m}:${s}`
}

/** Spoken form, changes once a minute: "9 hours 10 minutes". */
export function spokenElapsed(ms) {
  const minutes = Math.floor(ms / 60000)
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  const part = (n, unit) => `${n} ${unit}${n === 1 ? '' : 's'}`
  return h > 0 ? `${part(h, 'hour')} ${part(m, 'minute')}` : part(m, 'minute')
}
