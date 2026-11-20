const EDGES = { line: 'border-line', ok: 'border-ok-line', danger: 'border-danger-line' }

/** The rounded surface every screen is built from. Padding is set by the caller when the default does not fit. */
export default function Card({ as: Tag = 'section', edge = 'line', className = '', children, ...rest }) {
  return (
    <Tag className={`min-w-0 rounded-card border bg-surface p-6 shadow-card ${EDGES[edge]} ${className}`} {...rest}>
      {children}
    </Tag>
  )
}

/** Small grey card title with an optional muted note on the right ("Last shift · Fri 20 Nov"). */
export function CardTitle({ as: Tag = 'h2', id, note, children }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <Tag id={id} className="text-sm font-semibold text-ink-3">{children}</Tag>
      {note && <span className="text-right text-sm text-muted">{note}</span>}
    </div>
  )
}
