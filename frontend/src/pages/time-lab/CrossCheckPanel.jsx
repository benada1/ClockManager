import { formatMs } from '../../lib/serverTime.js'

function CrossCheckPanel({ crossCheck }) {
  if (!crossCheck) {
    return (
      <section className="card panel" aria-labelledby="crosscheck-heading">
        <header className="card__header">
          <h2 id="crosscheck-heading">Cross-check</h2>
          <span className="badge badge--neutral">Not cross-checked</span>
        </header>
        <p>Only one provider (or none) answered, so there was nothing to compare against.</p>
      </section>
    )
  }

  const { deltaMs, toleranceMs, roundTripBoundMs, agrees } = crossCheck
  return (
    <section className={`card panel ${agrees ? 'is-ok' : 'is-failed'}`} aria-labelledby="crosscheck-heading">
      <header className="card__header">
        <h2 id="crosscheck-heading">Cross-check</h2>
        <span className={`badge ${agrees ? 'badge--ok' : 'badge--error'}`}>{agrees ? 'Agree' : 'Disagree'}</span>
      </header>
      <dl className="facts facts--inline">
        <div>
          <dt>Difference between providers</dt>
          <dd>{formatMs(deltaMs)}</dd>
        </div>
        <div>
          <dt>Tolerance</dt>
          <dd>{formatMs(toleranceMs)}</dd>
        </div>
        <div>
          <dt>Round-trip bound</dt>
          <dd>{formatMs(roundTripBoundMs)}</dd>
        </div>
      </dl>
      <p className="small muted">
        {agrees
          ? 'Both answers are close enough to describe the same instant.'
          : 'The answers are too far apart to trust either one, so no punch time can be issued.'}{' '}
        They agree when the difference is within the tolerance, or smaller than the round-trip bound (the gap that
        network latency alone could explain).
      </p>
    </section>
  )
}

export default CrossCheckPanel
