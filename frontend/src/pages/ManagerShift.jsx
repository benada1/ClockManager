import { useState } from 'react'
import { getShiftDetail } from '../api/manager.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card from '../components/Card.jsx'
import Loading from '../components/Loading.jsx'
import Pill from '../components/Pill.jsx'
import { needsOffset } from '../lib/zurich.js'
import { href } from '../router.js'
import { shiftBadges, shiftDurationText } from './history/labels.js'
import { formatDayHeading } from './history/weeks.js'
import CorrectionDialog from './manager/CorrectionDialog.jsx'
import { FIELD_NAMES, correctionSentence, originalText, pointText, savedText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

function TimeRow({ detail, field }) {
  const { shift, corrections } = detail
  const iso = shift[field]?.zurich ?? null
  const original = originalText(corrections, field, iso)
  const withOffset = needsOffset(shift.clockIn?.zurich, shift.clockOut?.zurich)
  return (
    <div className="flex flex-col gap-1">
      <dt className="text-sm text-muted">{FIELD_NAMES[field]}</dt>
      <dd className="text-xl font-semibold">
        {iso ? `${pointText(iso, { withOffset, sameDayAs: shift.clockIn.zurich })}` : 'Still open'}
      </dd>
      {original && <dd className="text-sm text-muted">{original}</dd>}
    </div>
  )
}

/** One shift: its current times, a Correct button and the full change history. */
export default function ManagerShift({ shiftId }) {
  const detail = useLoad((signal) => getShiftDetail(shiftId, signal))
  const [correcting, setCorrecting] = useState(false)
  const [saved, setSaved] = useState(null)
  const d = detail.data

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <a
        className="self-start font-medium text-accent underline wrap-anywhere"
        href={href(d?.employee ? `/manager/employee/${encodeURIComponent(d.employee.id)}` : '/manager')}
      >
        <span aria-hidden="true">&larr;</span> {d?.employee ? `${d.employee.displayName}'s shifts` : 'Team'}
      </a>
      <h1 tabIndex={-1} className="text-2xl font-semibold wrap-anywhere">
        {d ? `${d.employee?.displayName}: ${formatDayHeading(d.shift.workDate)}` : 'Shift'}
      </h1>

      {!d && detail.loading && <Loading>Loading the shift…</Loading>}
      {detail.failure && (
        <Banner
          tone="error"
          title={detail.failure.title}
          action={
            !['forbidden', 'notfound'].includes(detail.failure.kind) && (
              <Button variant="secondary" busy={detail.loading} busyLabel="Trying again…" onClick={detail.reload} className="self-start">
                Try again
              </Button>
            )
          }
        >
          {detail.failure.text}
        </Banner>
      )}

      {saved && <Banner tone="success" title={saved} />}

      {d && (
        <>
          <Card className="flex flex-col gap-6">
            <dl className="grid gap-6 sm:grid-cols-3">
              <TimeRow detail={d} field="clockIn" />
              <TimeRow detail={d} field="clockOut" />
              <div className="flex flex-col gap-1">
                <dt className="text-sm text-muted">Duration</dt>
                <dd className="text-xl font-semibold">{shiftDurationText(d.shift, { manager: true })}</dd>
              </div>
            </dl>
            {shiftBadges(d.shift, { manager: true }).length > 0 && (
              <ul className="m-0 flex list-none flex-wrap gap-2 p-0">
                {shiftBadges(d.shift, { manager: true }).map((b) => (
                  <Pill key={b.key} tone={b.tone}>{b.text}</Pill>
                ))}
              </ul>
            )}
            <Button size="lg" onClick={() => (setSaved(null), setCorrecting(true))} className="self-start">
              Correct this shift
            </Button>
          </Card>

          <section aria-labelledby="changes-h" className="flex flex-col gap-3">
            <h2 id="changes-h" className="text-xl font-semibold">
              Change history
            </h2>
            {d.corrections.length === 0 ? (
              <p className="text-base text-muted">No changes. These are the times as punched.</p>
            ) : (
              <ol className="m-0 flex list-none flex-col gap-3 p-0">
                {d.corrections.map((c) => (
                  <li key={c.id}>
                    <Card as="div" className="flex flex-col gap-1 p-4">
                      <p className="text-base wrap-anywhere">{correctionSentence(c)}</p>
                      <p className="text-base text-muted wrap-anywhere">Reason: {c.reason}</p>
                    </Card>
                  </li>
                ))}
              </ol>
            )}
          </section>
        </>
      )}

      <p className="text-sm text-muted">All times are Zurich time and come from the server. A correction never deletes the original value.</p>

      {correcting && d && (
        <CorrectionDialog
          shiftId={shiftId}
          initial={d}
          onClose={() => setCorrecting(false)}
          onChanged={detail.replace}
          onSaved={(next, field) => {
            setCorrecting(false)
            detail.replace(next)
            setSaved(savedText(next, field))
          }}
        />
      )}
    </div>
  )
}
