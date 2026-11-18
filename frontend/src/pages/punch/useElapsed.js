import { useEffect, useState } from 'react'

/** Elapsed ms at the moment the status arrived: server now minus clock-in, plus the server's own delay. */
function elapsedAtSync(status) {
  const now = status?.serverNow
  const open = status?.openShift
  if (!now?.utc || !open?.clockIn?.utc) return null
  const diff = new Date(now.utc).getTime() - new Date(open.clockIn.utc).getTime() // server values, not the current time
  if (!Number.isFinite(diff)) return null
  return diff + (Number.isFinite(now.ageMs) ? now.ageMs : 0)
}

/**
 * Time since clock-in in ms, ticking every second, or null without a trusted server time.
 * elapsed = (serverNow - clockIn) + ageMs + (performance.now() - syncAt); no wall clock is read.
 */
export default function useElapsed(status, syncAt) {
  const base = syncAt === null ? null : elapsedAtSync(status)
  const [ms, setMs] = useState(null)

  useEffect(() => {
    if (base === null) return undefined
    // clock-ok: display only, monotonic since the server sync
    const update = () => setMs(Math.max(0, base + (performance.now() - syncAt)))
    update()
    const id = setInterval(update, 1000)
    return () => clearInterval(id)
  }, [base, syncAt])

  return base === null ? null : (ms ?? Math.max(0, base))
}

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
