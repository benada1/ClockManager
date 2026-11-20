import Card from '../../components/Card.jsx'
import Pill from '../../components/Pill.jsx'
import { formatMs, formatUtcInstant } from '../../lib/serverTime.js'
import { Code, Fact, Facts, PanelHeader } from './Facts.jsx'
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
    <Card as="article" aria-labelledby={headingId} className="flex flex-col gap-4">
      <PanelHeader
        id={headingId}
        as="h3"
        eyebrow={ROLE_LABELS[role] ?? role ?? 'Provider'}
        title={name}
        pill={
          <Pill as="span" tone={ok ? 'success' : 'danger'}>
            {label}
            {!ok && outcome ? <code className="font-mono font-normal">{outcome}</code> : null}
          </Pill>
        }
      />

      {!ok && (detail || httpStatus != null) && (
        <p className="rounded-field bg-danger-soft px-3 py-2 text-sm text-danger">
          {httpStatus != null && <strong>HTTP {httpStatus}. </strong>}
          {detail}
        </p>
      )}

      <Facts>
        <Fact label="Round trip">{formatMs(roundTripMs)}</Fact>
        <Fact label="Instant (UTC)">{utc ? <Code>{formatUtcInstant(utc)}</Code> : <span className="text-muted">no trusted instant</span>}</Fact>
        <Fact label="Zurich time">
          <ZurichTime local={zurichLocal} offset={zurichOffset} />
        </Fact>
      </Facts>

      <RawPayload raw={rawPayload} providerName={name} />
    </Card>
  )
}

export default ProviderCard
