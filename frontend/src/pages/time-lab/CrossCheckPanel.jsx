import Card from '../../components/Card.jsx'
import Pill from '../../components/Pill.jsx'
import { formatMs } from '../../lib/serverTime.js'
import { Fact, Facts, PanelHeader } from './Facts.jsx'

function CrossCheckPanel({ crossCheck }) {
  if (!crossCheck) {
    return (
      <Card aria-labelledby="crosscheck-heading" className="flex flex-col gap-3">
        <PanelHeader id="crosscheck-heading" title="Cross-check" pill={<Pill as="span">Not cross-checked</Pill>} />
        <p className="text-base text-ink-2">Only one provider (or none) answered, so there was nothing to compare against.</p>
      </Card>
    )
  }

  const { deltaMs, toleranceMs, roundTripBoundMs, agrees } = crossCheck
  return (
    <Card aria-labelledby="crosscheck-heading" edge={agrees ? 'ok' : 'danger'} className="flex flex-col gap-4">
      <PanelHeader
        id="crosscheck-heading"
        title="Cross-check"
        pill={
          <Pill as="span" tone={agrees ? 'success' : 'danger'}>
            {agrees ? 'Agree' : 'Disagree'}
          </Pill>
        }
      />
      <Facts inline>
        <Fact inline label="Difference between providers">{formatMs(deltaMs)}</Fact>
        <Fact inline label="Tolerance">{formatMs(toleranceMs)}</Fact>
        <Fact inline label="Round-trip bound">{formatMs(roundTripBoundMs)}</Fact>
      </Facts>
      <p className="text-sm text-muted">
        {agrees
          ? 'Both answers are close enough to describe the same instant.'
          : 'The answers are too far apart to trust either one, so no punch time can be issued.'}{' '}
        They agree when the difference is within the tolerance, or smaller than the round-trip bound (the gap that network latency
        alone could explain).
      </p>
    </Card>
  )
}

export default CrossCheckPanel
