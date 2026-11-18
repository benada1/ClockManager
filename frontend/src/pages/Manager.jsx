import { getAnomalies, getEmployees } from '../api/manager.js'
import Banner from '../components/Banner.jsx'
import Button from '../components/Button.jsx'
import Card from '../components/Card.jsx'
import Loading from '../components/Loading.jsx'
import Pill from '../components/Pill.jsx'
import { href } from '../router.js'
import { shiftTimesText } from './history/labels.js'
import { formatDayHeading } from './history/weeks.js'
import { anomalyText, employeeFlagTexts, employeeStatusText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

const TH = 'border-b border-line px-4 py-3 text-left text-sm font-medium text-muted'
const TD = 'border-b border-line px-4 py-3 align-middle text-base'
const PROBLEMS_LIMIT = 100 // the server returns at most this many, newest first
const OPEN_LINK = 'inline-flex min-h-11 items-center rounded-ui px-3 font-medium text-accent underline hover:bg-accent-soft'

/** Loading, failure (with retry unless it can't help) and the loaded content of one section. */
function Section({ title, count, load, loadingText, children }) {
  const { data, loading, failure, reload } = load
  return (
    <section aria-labelledby={`${title}-h`} className="flex flex-col gap-3">
      <h2 id={`${title}-h`} className="text-xl font-semibold">
        {title}
        {data && <span className="font-normal text-muted"> ({count(data)})</span>}
      </h2>
      {!data && loading && <Loading>{loadingText}</Loading>}
      {failure && (
        <Banner
          tone="error"
          title={failure.title}
          action={
            failure.kind !== 'forbidden' && (
              <Button variant="secondary" busy={loading} busyLabel="Trying again…" onClick={reload} className="self-start">
                Try again
              </Button>
            )
          }
        >
          {data ? `${failure.text} The list below is the last one we loaded.` : failure.text}
        </Banner>
      )}
      {data && <Card className="overflow-x-auto p-0">{children(data)}</Card>}
    </section>
  )
}

function EmployeesTable({ employees }) {
  if (!employees.length) return <p className="p-6 text-base text-muted">No employees yet.</p>
  return (
    <table className="w-full border-collapse">
      <thead>
        <tr>
          <th scope="col" className={TH}>Name</th>
          <th scope="col" className={TH}>Status</th>
          <th scope="col" className={TH}>Warnings</th>
          <th scope="col" className={TH}><span className="sr-only">Open</span></th>
        </tr>
      </thead>
      <tbody>
        {employees.map((e) => {
          const flags = employeeFlagTexts(e.flags)
          return (
            <tr key={e.id} className={e.isActive ? '' : 'text-muted'}>
              <th scope="row" className={`${TD} text-left font-medium wrap-anywhere`}>
                {e.displayName}
                {e.role === 'Manager' && <span className="ml-2 text-sm font-normal text-muted">Manager</span>}
                {!e.isActive && <span className="block text-sm font-normal text-muted">Switched off</span>}
              </th>
              <td className={TD}>
                <span className="inline-flex items-center gap-2">
                  {e.status === 'clocked_in' && <span aria-hidden="true" className="size-2 rounded-full bg-ok-line" />}
                  {employeeStatusText(e)}
                  {e.status === 'clocked_in' && <span className="sr-only"> (Zurich time)</span>}
                </span>
              </td>
              <td className={TD}>
                {flags.length ? (
                  <ul className="m-0 flex list-none flex-wrap gap-2 p-0">
                    {flags.map((f) => (
                      <Pill key={f.key} tone="warning">{f.text}</Pill>
                    ))}
                  </ul>
                ) : (
                  <span className="text-muted">None</span>
                )}
              </td>
              <td className={`${TD} text-right`}>
                <a className={OPEN_LINK} href={href(`/manager/employee/${encodeURIComponent(e.id)}`)}>
                  Open week<span className="sr-only"> of {e.displayName}</span>
                </a>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

function ProblemsTable({ anomalies }) {
  if (!anomalies.length) return <p className="p-6 text-base text-muted">No problems right now.</p>
  return (
    <table className="w-full border-collapse">
      <thead>
        <tr>
          <th scope="col" className={TH}>Problem</th>
          <th scope="col" className={TH}>Employee</th>
          <th scope="col" className={TH}>Day</th>
          <th scope="col" className={TH}>Times</th>
          <th scope="col" className={TH}><span className="sr-only">Open</span></th>
        </tr>
      </thead>
      <tbody>
        {anomalies.map((a) => (
          <tr key={`${a.shiftId}-${a.kind}`}>
            <th scope="row" className={`${TD} text-left font-medium`}>
              <Pill as="span" tone="warning">{anomalyText(a.kind)}</Pill>
            </th>
            <td className={`${TD} wrap-anywhere`}>{a.displayName}</td>
            <td className={`${TD} whitespace-nowrap`}>{formatDayHeading(a.workDate)}</td>
            <td className={TD}>{shiftTimesText(a)}</td>
            <td className={`${TD} text-right`}>
              <a className={OPEN_LINK} href={href(`/manager/shift/${encodeURIComponent(a.shiftId)}`)}>
                Open shift<span className="sr-only"> of {a.displayName}, {formatDayHeading(a.workDate)}</span>
              </a>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

export default function Manager() {
  const employees = useLoad(getEmployees)
  const anomalies = useLoad(getAnomalies)
  const forbidden = employees.failure?.kind === 'forbidden' || anomalies.failure?.kind === 'forbidden'

  return (
    <div className="flex w-full flex-col gap-8">
      <h1 tabIndex={-1} className="text-2xl font-semibold">
        Team
      </h1>

      {forbidden ? (
        <Banner tone="error" title="You need a manager account for this page.">
          <a className="font-medium text-accent underline" href={href('/')}>
            Go to the clock
          </a>
        </Banner>
      ) : (
        <>
          <Section title="Problems" count={(d) => d.length} load={anomalies} loadingText="Loading problems…">
            {(d) => (
              <>
                <ProblemsTable anomalies={d} />
                {d.length >= PROBLEMS_LIMIT && <p className="border-t border-line p-4 text-sm text-muted">Showing the newest {PROBLEMS_LIMIT} problems.</p>}
              </>
            )}
          </Section>
          <Section title="Employees" count={(d) => d.length} load={employees} loadingText="Loading employees…">
            {(d) => <EmployeesTable employees={d} />}
          </Section>
        </>
      )}

      <p className="text-sm text-muted">All times are Zurich time and come from the server.</p>
    </div>
  )
}
