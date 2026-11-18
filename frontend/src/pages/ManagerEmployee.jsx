import { useState } from 'react'
import { getEmployeeShifts, getEmployees } from '../api/manager.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card from '../components/Card.jsx'
import { href } from '../router.js'
import { shiftTimesText } from './history/labels.js'
import useHistory from './history/useHistory.js'
import WeekView from './history/WeekView.jsx'
import { formatDayHeading } from './history/weeks.js'
import CorrectionDialog from './manager/CorrectionDialog.jsx'
import { savedText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

/** One employee's week (the History view) with a Correct button per shift. */
export default function ManagerEmployee({ employeeId }) {
  const history = useHistory((range, signal) => getEmployeeShifts(employeeId, range, signal))
  const team = useLoad(getEmployees)
  const [correcting, setCorrecting] = useState(null)
  const [saved, setSaved] = useState(null)

  const employee = history.data?.employee ?? team.data?.find((e) => String(e.id) === String(employeeId))
  const name = employee?.displayName

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <a className="self-start font-medium text-accent underline" href={href('/manager')}>
        <span aria-hidden="true">&larr;</span> Team
      </a>
      <h1 tabIndex={-1} className="text-2xl font-semibold">
        {name ? `${name}'s shifts` : 'Shifts'}
      </h1>
      {employee && employee.isActive === false && (
        <Banner tone="info" title="This account is switched off.">
          The employee can't clock in. You can still correct past shifts.
        </Banner>
      )}

      {saved && (
        <Banner tone="success" title={saved.title}>
          <a className="font-medium text-accent underline" href={href(`/manager/shift/${encodeURIComponent(saved.shiftId)}`)}>
            See the change history
          </a>
        </Banner>
      )}

      <WeekView
        history={history}
        manager
        loadingText="Loading shifts…"
        empty={
          <Card className="flex flex-col items-start gap-2">
            <p className="text-lg font-medium">No shifts yet.</p>
            <p className="text-muted">Shifts show up here once this person clocks in.</p>
          </Card>
        }
        renderAction={(shift) => (
          <span className="flex items-center gap-1">
            <a
              className="inline-flex min-h-11 items-center rounded-ui px-3 font-medium text-accent underline hover:bg-accent-soft"
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
