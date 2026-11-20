import { zurichParts } from '../../lib/serverTime.js'
import { Code } from './Facts.jsx'

/** Server-resolved Zurich wall time, shown from strings (never via the browser's Date/time zone). */
function ZurichTime({ local, offset, large = false }) {
  const parts = zurichParts(local, offset)
  if (!parts) {
    return local || offset ? <Code>{`${local ?? '?'} ${offset ?? '?'}`}</Code> : <span className="text-muted">-</span>
  }
  return (
    <span className="inline-flex flex-col">
      <span>
        <span className={`font-mono font-semibold tabular-nums ${large ? 'text-2xl' : ''}`}>{parts.time}</span>{' '}
        <span className="text-sm text-ink-2">
          ({parts.offsetLabel}
          {parts.abbreviation ? `, ${parts.abbreviation}` : ''})
        </span>
      </span>
      <span className="text-sm text-muted">{parts.date}</span>
    </span>
  )
}

export default ZurichTime
