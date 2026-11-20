import { useEffect, useState } from 'react'
import { NO_ANCHOR, nextAnchor } from '../../lib/serverClock.js'

/** { syncAt, anchor } for the latest status; the rules live in lib/serverClock.js. */
export function useServerAnchor(status, syncAt) {
  const [kept, setKept] = useState(NO_ANCHOR)
  const next = nextAnchor(kept, status, syncAt)
  if (next !== kept) setKept(next) // adjust state while rendering (React's documented pattern), no effect needed
  return next
}

/** Server time in ms, ticking on each full second, or null without a trusted anchor. */
export function useServerNow(anchor) {
  const [now, setNow] = useState(null)

  useEffect(() => {
    if (anchor === null) return undefined
    let id
    const tick = () => {
      const value = anchor + performance.now() // clock-ok: display only, re-synced from server
      setNow(value)
      id = setTimeout(tick, 1000 - (value % 1000) + 5)
    }
    tick()
    return () => clearTimeout(id)
  }, [anchor])

  return anchor === null ? null : now
}
