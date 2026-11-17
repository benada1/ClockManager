import { formatMs, formatUtcInstant } from '../../lib/serverTime.js'
import RawPayload from './RawPayload.jsx'
import ZurichTime from './ZurichTime.jsx'

const OUTCOME_LABELS = {
  ok: 'OK',
  timeout: 'Timeout',
  http_error: 'HTTP error',
  rate_limited: 'Rate limited',
  invalid_response: 'Invalid response',
  network_error: 'Network error',
  error: 'Error',
}

const ROLE_LABELS = { primary: 'Primary', secondary: 'Secondary' }

function ProviderCard({ provider }) {
  const { role, name, outcome, roundTripMs, httpStatus, detail, utc, zurichOffset, zurichLocal, rawPayload } = provider
  const ok = outcome === 'ok'
  const label = OUTCOME_LABELS[outcome] ?? outcome ?? 'Unknown'
  const headingId = `provider-${role ?? name}`

  return (
    <article className={`card provider ${ok ? 'is-ok' : 'is-failed'}`} aria-labelledby={headingId}>
      <header className="card__header">
        <h3 id={headingId}>
          <span className="eyebrow">{ROLE_LABELS[role] ?? role ?? 'Provider'}</span>
          {name}
        </h3>
        <span className={`badge ${ok ? 'badge--ok' : 'badge--error'}`}>
          {label}
          {!ok && outcome ? <code className="badge__code">{outcome}</code> : null}
        </span>
      </header>

      {!ok && (detail || httpStatus != null) && (
        <p className="problem">
          {httpStatus != null && <strong>HTTP {httpStatus}. </strong>}
          {detail}
        </p>
      )}

      <dl className="facts">
        <div>
          <dt>Round trip</dt>
          <dd>{formatMs(roundTripMs)}</dd>
        </div>
        <div>
          <dt>Instant (UTC)</dt>
          <dd>{utc ? <code>{formatUtcInstant(utc)}</code> : <span className="muted">no trusted instant</span>}</dd>
        </div>
        <div>
          <dt>Zurich time</dt>
          <dd>
            <ZurichTime local={zurichLocal} offset={zurichOffset} />
          </dd>
        </div>
      </dl>

      <RawPayload raw={rawPayload} providerName={name} />
    </article>
  )
}

export default ProviderCard
