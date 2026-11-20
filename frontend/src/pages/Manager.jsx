import { useState } from 'react'
import { getAnomalies, getEmployees } from '../api/manager.js'
import Avatar from '../components/Avatar.jsx'
import Banner from '../components/Banner.jsx'
import Button, { LinkButton } from '../components/Button.jsx'
import { ChevronRight } from '../components/icons.jsx'
import Loading from '../components/Loading.jsx'
import Pill from '../components/Pill.jsx'
import { zurichDay } from '../lib/zurich.js'
import { href } from '../router.js'
import { ShiftTimes } from './history/DayCard.jsx'
import { formatDayHeading } from './history/weeks.js'
import { anomalyText, employeeFlagTexts, employeeStatusText } from './manager/labels.js'
import useLoad from './manager/useLoad.js'

const TH = 'px-5 py-3.5 text-left text-xs font-semibold tracking-wide text-muted uppercase'
const TD = 'border-t border-subtle px-5 py-3.5 align-middle text-sm'
const PROBLEMS_LIMIT = 100 // the server returns at most this many, newest first
const FILTERS = [
  { key: 'all', label: 'All' },
  { key: 'forgotten_clock_out', label: 'Forgot to clock out' },
  { key: 'long_shift', label: 'Unusually long' },
  { key: 'short_shift', label: 'Very short' },
]

/** Loading and failure (with retry unless it can't help) of one section; the content is drawn by the caller. */
function LoadState({ load, loadingText, what }) {
  const { data, loading, failure, reload } = load
  return (
    <>
      {!data && loading && <Loading>{loadingText}</Loading>}
      {failure && (
        <Banner
          tone="error"
          title={failure.title}
          action={
            failure.kind !== 'forbidden' && (
              <Button variant="secondary" busy={loading} busyLabel="Trying again…" onClick={reload}>
                Try again
              </Button>
            )
          }
        >
          {data ? `${failure.text} The ${what} below is the last one we loaded.` : failure.text}
        </Banner>
      )}
    </>
  )
}

function FilterChips({ value, onChange }) {
  return (
    <div role="group" aria-label="Filter problems" className="flex flex-wrap gap-2">
      {FILTERS.map((f) => {
        const on = f.key === value
        return (
          <button
            key={f.key}
            type="button"
            aria-pressed={on}
            onClick={() => onChange(f.key)}
            className={`min-h-11 cursor-pointer rounded-full border px-4 text-sm font-medium transition-colors ${
              on ? 'border-ink bg-ink text-canvas' : 'border-control bg-surface text-ink-2 hover:bg-subtle'
            }`}
          >
            {f.label}
          </button>
        )
      })}
    </div>
  )
}

function ProblemsTable({ rows }) {
  return (
    <table className="w-full min-w-3xl border-collapse">
      <thead>
        <tr>
          <th scope="col" className={TH}>Problem</th>
          <th scope="col" className={TH}>Employee</th>
          <th scope="col" className={TH}>Day</th>
          <th scope="col" className={TH}>Times</th>
          <th scope="col" className={TH}><span className="sr-only">Action</span></th>
        </tr>
      </thead>
      <tbody>
        {rows.map((a) => {
          const day = zurichDay(a.clockIn?.zurich) ?? formatDayHeading(a.workDate)
          return (
            <tr key={`${a.shiftId}-${a.kind}`} className="transition-colors hover:bg-subtle">
              <th scope="row" className={`${TD} text-left font-normal`}>
                <Pill as="span" tone={a.kind === 'forgotten_clock_out' ? 'danger' : 'warning'}>
                  {anomalyText(a.kind)}
                </Pill>
              </th>
              <td className={TD}>
                <span className="inline-flex items-center gap-2.5 font-medium wrap-anywhere">
                  <Avatar name={a.displayName} size="sm" />
                  {a.displayName}
                </span>
              </td>
              <td className={`${TD} whitespace-nowrap text-ink-2`}>{day}</td>
              <td className={`${TD} whitespace-nowrap text-ink-2 tabular-nums`}>
                <ShiftTimes shift={a} />
              </td>
              <td className={`${TD} text-right`}>
                <LinkButton href={href(`/manager/shift/${encodeURIComponent(a.shiftId)}`)}>
                  Review<span className="sr-only"> {anomalyText(a.kind)} of {a.displayName}, {day}</span>
                </LinkButton>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

function Problems({ load }) {
  const [filter, setFilter] = useState('all')
  const all = load.data ?? []
  const rows = filter === 'all' ? all : all.filter((a) => a.kind === filter)

  return (
    <section aria-labelledby="problems-h" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h2 id="problems-h" className="flex items-center gap-2.5 text-lg font-semibold">
          Needs review
          {load.data && (
            <Pill as="span" tone={all.length ? 'danger' : 'success'} className="text-sm">
              {all.length}
              <span className="sr-only"> {all.length === 1 ? 'shift' : 'shifts'}</span>
            </Pill>
          )}
        </h2>
        {all.length > 0 && <FilterChips value={filter} onChange={setFilter} />}
      </div>
      <LoadState load={load} loadingText="Loading problems…" what="list" />
      {load.data && (
        <div className="overflow-hidden rounded-card border border-line bg-surface shadow-card">
          {all.length === 0 ? (
            <p className="px-6 py-10 text-center text-base text-muted">Nothing to review right now.</p>
          ) : rows.length === 0 ? (
            <p className="px-6 py-10 text-center text-base text-muted">No problems of this kind right now.</p>
          ) : (
            <div className="overflow-x-auto">
              <ProblemsTable rows={rows} />
            </div>
          )}
          {all.length > 0 && (
            <p role="status" className="flex flex-wrap justify-between gap-3 border-t border-line px-5 py-3.5 text-sm text-muted">
              <span>
                Showing {rows.length} of {all.length}
              </span>
              {all.length >= PROBLEMS_LIMIT && <span>Only the newest {PROBLEMS_LIMIT} problems are listed.</span>}
            </p>
          )}
        </div>
      )}
    </section>
  )
}

function PersonCard({ e }) {
  const flags = employeeFlagTexts(e.flags)
  const clockedIn = e.status === 'clocked_in'
  return (
    <li>
      <a
        href={href(`/manager/employee/${encodeURIComponent(e.id)}`)}
        className="flex h-full items-center gap-3.5 rounded-card border border-line bg-surface p-4.5 text-ink no-underline shadow-card transition-colors hover:bg-subtle"
      >
        <Avatar name={e.displayName} size="lg" />
        <span className="flex min-w-0 flex-1 flex-col gap-1">
          <span className="text-base font-semibold wrap-anywhere">
            {e.displayName}
            {e.role === 'Manager' && <span className="ml-2 text-xs font-medium text-muted">Manager</span>}
          </span>
          <span className="flex items-center gap-1.5 text-sm text-muted">
            {clockedIn && <span aria-hidden="true" className="size-2 shrink-0 rounded-full bg-ok-dot" />}
            {e.isActive ? employeeStatusText(e) : 'Switched off'}
            {clockedIn && <span className="sr-only"> (Zurich time)</span>}
          </span>
          {flags.length > 0 && (
            <span className="flex flex-wrap gap-1.5">
              {flags.map((f) => (
                <Pill key={f.key} as="span" tone={f.key === 'forgotten_clock_out' ? 'danger' : 'warning'}>
                  {f.text}
                </Pill>
              ))}
            </span>
          )}
        </span>
        <span className="text-faint">
          <ChevronRight />
        </span>
      </a>
    </li>
  )
}

function People({ load }) {
  return (
    <section aria-labelledby="people-h" className="flex flex-col gap-4">
      <h2 id="people-h" className="text-lg font-semibold">
        People
        {load.data && <span className="font-normal text-muted"> ({load.data.length})</span>}
      </h2>
      <LoadState load={load} loadingText="Loading employees…" what="list" />
      {load.data &&
        (load.data.length === 0 ? (
          <p className="text-base text-muted">No employees yet.</p>
        ) : (
          <ul className="grid list-none grid-cols-[repeat(auto-fill,minmax(15rem,1fr))] gap-4 p-0">
            {load.data.map((e) => (
              <PersonCard key={e.id} e={e} />
            ))}
          </ul>
        ))}
    </section>
  )
}

export default function Manager() {
  const employees = useLoad(getEmployees)
  const anomalies = useLoad(getAnomalies)
  const forbidden = employees.failure?.kind === 'forbidden' || anomalies.failure?.kind === 'forbidden'

  return (
    <div className="flex w-full flex-col gap-8">
      <div className="flex flex-col gap-1">
        <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight">
          Team
        </h1>
        <p className="text-base text-muted">Review flagged shifts and your team's hours.</p>
      </div>

      {forbidden ? (
        <Banner tone="error" title="You need a manager account for this page.">
          <a className="font-semibold text-accent-ink no-underline hover:underline" href={href('/')}>
            Go to the clock
          </a>
        </Banner>
      ) : (
        <>
          <Problems load={anomalies} />
          <People load={employees} />
        </>
      )}

      <p className="text-sm text-muted">All times are Zurich time and come from the server.</p>
    </div>
  )
}
