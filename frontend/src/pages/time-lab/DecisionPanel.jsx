import { formatUtcInstant } from '../../lib/serverTime.js'
import ZurichTime from './ZurichTime.jsx'

const FAILURE_EXPLANATIONS = {
  'time.providers_disagree':
    'Both providers answered, but their times differ by more than the tolerance, so neither can be trusted.',
  'time.unavailable': 'No provider gave an answer in time (timeouts, HTTP errors, rate limits or network failures).',
  'time.invalid_response':
    'A provider answered, but the answer failed validation (bad format, wrong time zone or offset), and no other provider gave a valid one.',
}

function DecisionPanel({ decision }) {
  if (!decision) return null

  if (decision.result !== 'accepted') {
    const code = decision.code ?? 'unknown'
    return (
      <section className="card panel decision is-failed" aria-labelledby="decision-heading">
        <header className="card__header">
          <h2 id="decision-heading">Trusted clock decision</h2>
          <span className="badge badge--error">Failed closed</span>
        </header>
        <p>
          Error code <code>{code}</code>. A punch right now would get{' '}
          <strong>HTTP {decision.httpStatus ?? 503}</strong> and nothing would be saved.
        </p>
        <p>{FAILURE_EXPLANATIONS[code] ?? 'The server could not obtain a trustworthy time.'}</p>
      </section>
    )
  }

  return (
    <section className="card panel decision is-ok" aria-labelledby="decision-heading">
      <header className="card__header">
        <h2 id="decision-heading">Trusted clock decision</h2>
        <span className="badge badge--ok">Accepted</span>
      </header>
      <p className="decision__time">
        <ZurichTime local={decision.zurichLocal} offset={decision.zurichOffset} />
      </p>
      <dl className="facts facts--inline">
        <div>
          <dt>Time from</dt>
          <dd>{decision.provider}</dd>
        </div>
        <div>
          <dt>Cross-checked by</dt>
          <dd>{decision.crossCheckedBy ?? <span className="muted">nobody (single source)</span>}</dd>
        </div>
        <div>
          <dt>Instant (UTC)</dt>
          <dd>
            <code>{formatUtcInstant(decision.utc) ?? '-'}</code>
          </dd>
        </div>
        <div>
          <dt>Precision</dt>
          <dd>
            {decision.lowPrecision ? (
              <span className="badge badge--warn">Low precision</span>
            ) : (
              'Normal'
            )}
          </dd>
        </div>
      </dl>
      {decision.lowPrecision && (
        <p className="small">
          The provider was slow to answer, so the instant is less exact (it may be off by up to half the round
          trip). A punch would still be accepted.
        </p>
      )}
      <p className="small muted">This is the time a Clock In or Clock Out would have been recorded with.</p>
    </section>
  )
}

export default DecisionPanel
