import { useState } from 'react'
import { getShiftDetail } from '../api/manager.js'
import Avatar from '../components/Avatar.jsx'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card, { CardTitle } from '../components/Card.jsx'
import Loading from '../components/Loading.jsx'
import Pill from '../components/Pill.jsx'
import { needsOffset } from '../lib/zurich.js'
import { href } from '../router.js'
import { shiftBadges, shiftDurationText } from './history/labels.js'
import { formatDayHeading } from './history/weeks.js'
import BackLink from './manager/BackLink.jsx'
import CorrectionDialog from './manager/CorrectionDialog.jsx'
import { FIELD_NAMES, correctionSentence, originalText, pointText, savedText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

function TimeRow({ detail, field }) {
  const { shift, corrections } = detail
  const iso = shift[field]?.zurich ?? null
  const original = originalText(corrections, field, iso)
  const withOffset = needsOffset(shift.clockIn?.zurich, shift.clockOut?.zurich)
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs text-muted">{FIELD_NAMES[field]}</dt>
      <dd className="text-xl font-semibold tabular-nums">{iso ? pointText(iso, { withOffset, sameDayAs: shift.clockIn.zurich }) : 'Still open'}</dd>
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
  const name = d?.employee?.displayName
  const badges = d ? shiftBadges(d.shift, { manager: true }) : []

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <BackLink href={href(d?.employee ? `/manager/employee/${encodeURIComponent(d.employee.id)}` : '/manager')}>
        {d?.employee ? `${name}'s shifts` : 'Team'}
      </BackLink>

      <div className="flex min-w-0 items-center gap-4">
        {name && <Avatar name={name} size="xl" />}
        <div className="flex min-w-0 flex-col gap-0.5">
          <h1 tabIndex={-1} className="text-2xl font-semibold tracking-tight wrap-anywhere">
            {d ? `${name}: ${formatDayHeading(d.shift.workDate)}` : 'Shift'}
          </h1>
          {d && <p className="text-sm text-muted">One shift and its change history</p>}
        </div>
      </div>

      {!d && detail.loading && <Loading>Loading the shift…</Loading>}
      {detail.failure && (
        <Banner
          tone="error"
          title={detail.failure.title}
          action={
            !['forbidden', 'notfound'].includes(detail.failure.kind) && (
              <Button variant="secondary" busy={detail.loading} busyLabel="Trying again…" onClick={detail.reload}>
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
          <Card className="flex flex-col gap-6 sm:p-8">
            <dl className="grid gap-6 sm:grid-cols-3">
              <TimeRow detail={d} field="clockIn" />
              <TimeRow detail={d} field="clockOut" />
              <div className="flex flex-col gap-0.5">
                <dt className="text-xs text-muted">Duration</dt>
                <dd className="text-xl font-semibold tabular-nums">{shiftDurationText(d.shift, { manager: true })}</dd>
              </div>
            </dl>
            {badges.length > 0 && (
              <ul className="flex list-none flex-wrap gap-2 border-t border-subtle p-0 pt-5">
                {badges.map((b) => (
                  <Pill key={b.key} tone={b.tone}>
                    {b.text}
                  </Pill>
                ))}
              </ul>
            )}
            <Button size="lg" onClick={() => (setSaved(null), setCorrecting(true))} className="self-start">
              Correct this shift
            </Button>
          </Card>

          <Card aria-labelledby="changes-h" className="flex flex-col gap-4">
            <CardTitle id="changes-h">Change history</CardTitle>
            {d.corrections.length === 0 ? (
              <p className="text-base text-muted">No changes. These are the times as punched.</p>
            ) : (
              <ol className="flex list-none flex-col p-0">
                {d.corrections.map((c) => (
                  <li key={c.id} className="flex gap-3 border-t border-subtle py-4 first:border-t-0 first:pt-0 last:pb-0">
                    <span aria-hidden="true" className="mt-2 size-2.5 shrink-0 rounded-full bg-accent" />
                    <div className="flex min-w-0 flex-col gap-1">
                      <p className="text-base wrap-anywhere">{correctionSentence(c)}</p>
                      <p className="text-sm text-muted wrap-anywhere">Reason: {c.reason}</p>
                    </div>
                  </li>
                ))}
              </ol>
            )}
          </Card>
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
