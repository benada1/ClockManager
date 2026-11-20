/** Label/value pairs. `inline` lays them out in columns (the decision and cross-check panels). */
export function Facts({ inline = false, children }) {
  return <dl className={inline ? 'grid gap-4 sm:grid-cols-[repeat(auto-fit,minmax(11rem,1fr))]' : 'flex flex-col gap-3'}>{children}</dl>
}

export function Fact({ label, inline = false, children }) {
  return (
    <div className={inline ? 'flex flex-col gap-0.5' : 'grid gap-0.5 sm:grid-cols-[8.5rem_1fr] sm:gap-3'}>
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="min-w-0 text-sm text-ink wrap-anywhere">{children}</dd>
    </div>
  )
}

/** Panel heading row: optional small label above the title, a status pill on the right. */
export function PanelHeader({ id, eyebrow, title, as: Tag = 'h2', pill }) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
      <Tag id={id} className="flex flex-col text-lg font-semibold">
        {eyebrow && <span className="text-xs font-semibold tracking-wide text-muted uppercase">{eyebrow}</span>}
        {title}
      </Tag>
      {pill}
    </div>
  )
}

export const Code = ({ children }) => <code className="font-mono text-[0.9em]">{children}</code>
