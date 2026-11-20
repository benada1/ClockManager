import Card from '../../components/Card.jsx'
import Pill from '../../components/Pill.jsx'
import { formatUtcInstant } from '../../lib/serverTime.js'
import { Code, Fact, Facts, PanelHeader } from './Facts.jsx'
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
      <Card aria-labelledby="decision-heading" edge="danger" className="flex flex-col gap-3">
        <PanelHeader id="decision-heading" title="Trusted clock decision" pill={<Pill as="span" tone="danger">Failed closed</Pill>} />
        <p className="text-base">
          Error code <Code>{code}</Code>. A punch right now would get <strong>HTTP {decision.httpStatus ?? 503}</strong> and nothing would be saved.
        </p>
        <p className="text-base text-ink-2">{FAILURE_EXPLANATIONS[code] ?? 'The server could not obtain a trustworthy time.'}</p>
      </Card>
    )
  }

  return (
    <Card aria-labelledby="decision-heading" edge="ok" className="flex flex-col gap-4">
      <PanelHeader id="decision-heading" title="Trusted clock decision" pill={<Pill as="span" tone="success">Accepted</Pill>} />
      <p>
        <ZurichTime local={decision.zurichLocal} offset={decision.zurichOffset} large />
      </p>
      <Facts inline>
        <Fact inline label="Time from">{decision.provider}</Fact>
        <Fact inline label="Cross-checked by">{decision.crossCheckedBy ?? <span className="text-muted">nobody (single source)</span>}</Fact>
        <Fact inline label="Instant (UTC)">
          <Code>{formatUtcInstant(decision.utc) ?? '-'}</Code>
        </Fact>
        <Fact inline label="Precision">
          {decision.lowPrecision ? (
            <Pill as="span" tone="warning">
              Low precision
            </Pill>
          ) : (
            'Normal'
          )}
        </Fact>
      </Facts>
      {decision.lowPrecision && (
        <p className="text-sm text-ink-2">
          The provider was slow to answer, so the instant is less exact (it may be off by up to half the round trip). A punch would
          still be accepted.
        </p>
      )}
      <p className="text-sm text-muted">This is the time a Clock In or Clock Out would have been recorded with.</p>
    </Card>
  )
}

export default DecisionPanel
