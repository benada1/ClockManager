import { zurichParts } from '../../lib/serverTime.js'

/** Server-resolved Zurich wall time, shown from strings (never via the browser's Date/time zone). */
function ZurichTime({ local, offset }) {
  const parts = zurichParts(local, offset)
  if (!parts) {
    return local || offset ? <code>{`${local ?? '?'} ${offset ?? '?'}`}</code> : <span className="muted">-</span>
  }
  return (
    <span className="zurich-time">
      <span className="zurich-time__clock">{parts.time}</span>{' '}
      <span className="zurich-time__offset">
        ({parts.offsetLabel}
        {parts.abbreviation ? `, ${parts.abbreviation}` : ''})
      </span>
      <span className="zurich-time__date">{parts.date}</span>
    </span>
  )
}

export default ZurichTime
