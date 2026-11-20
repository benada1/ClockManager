import { useState } from 'react'
import { getEmployeeShifts, getEmployees } from '../api/manager.js'
import Avatar from '../components/Avatar.jsx'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import EmptyState from '../components/EmptyState.jsx'
import { href } from '../router.js'
import { shiftTimesText } from './history/labels.js'
import useHistory from './history/useHistory.js'
import WeekView from './history/WeekView.jsx'
import { formatDayHeading } from './history/weeks.js'
import BackLink from './manager/BackLink.jsx'
import CorrectionDialog from './manager/CorrectionDialog.jsx'
import { savedText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

/** One employee's week (the History view) with Details and Correct per shift. */
export default function ManagerEmployee({ employeeId }) {
  const history = useHistory((range, signal) => getEmployeeShifts(employeeId, range, signal))
  const team = useLoad(getEmployees)
  const [correcting, setCorrecting] = useState(null)
  const [saved, setSaved] = useState(null)

  const employee = history.data?.employee ?? team.data?.find((e) => String(e.id) === String(employeeId))
  const name = employee?.displayName
  const firstName = name?.split(' ')[0]

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6">
      <BackLink href={href('/manager')}>Team</BackLink>

      <WeekView
        history={history}
        manager
        loadingText="Loading shifts…"
        notices={
          <>
            {employee && employee.isActive === false && (
              <Banner tone="info" title="This account is switched off.">
                The employee can't clock in. You can still correct past shifts.
              </Banner>
            )}
            {saved && (
              <Banner tone="success" title={saved.title}>
                <a className="font-semibold text-accent-ink no-underline hover:underline" href={href(`/manager/shift/${encodeURIComponent(saved.shiftId)}`)}>
                  See the change history
                </a>
              </Banner>
            )}
          </>
        }
        header={
          <div className="flex min-w-0 items-center gap-4">
            {name && <Avatar name={name} size="xl" />}
            <div className="flex min-w-0 flex-col gap-0.5">
              <h1 tabIndex={-1} className="text-2xl font-semibold tracking-tight wrap-anywhere">
                {name ?? 'Shifts'}
              </h1>
              {name && <p className="text-sm text-muted">Shifts</p>}
            </div>
          </div>
        }
        empty={
          <EmptyState title="No shifts yet">
            Shifts will show up here as soon as {firstName ?? 'this person'} clocks in for the first time.
          </EmptyState>
        }
        renderAction={(shift) => (
          <span className="flex items-center gap-1">
            <a
              className="inline-flex min-h-11 items-center rounded-control px-3 text-sm font-medium text-accent-ink no-underline hover:bg-accent-soft"
              href={href(`/manager/shift/${encodeURIComponent(shift.shiftId)}`)}
            >
              Details<span className="sr-only">, {formatDayHeading(shift.workDate)}, {shiftTimesText(shift)}</span>
            </a>
            <Button variant="secondary" onClick={() => (setSaved(null), setCorrecting(shift.shiftId))}>
              Correct<span className="sr-only">, {formatDayHeading(shift.workDate)}, {shiftTimesText(shift)}</span>
            </Button>
          </span>
        )}
      />

      <p className="text-sm text-muted">All times are Zurich time and come from the server. Hours are counted from the exact moments.</p>

      {correcting != null && (
        <CorrectionDialog
          shiftId={correcting}
          onClose={() => setCorrecting(null)}
          onChanged={() => history.retry()}
          onSaved={(detail, field) => {
            setCorrecting(null)
            setSaved({ shiftId: correcting, title: savedText(detail, field) })
            history.retry()
          }}
        />
      )}
    </div>
  )
}
