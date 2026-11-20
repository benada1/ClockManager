const TONES = {
  info: 'bg-subtle text-ink-3',
  warning: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  success: 'bg-ok-soft text-ok',
  accent: 'bg-accent-soft text-accent-ink',
}

/** A small soft word label. Renders an <li> (inside a list) or a <span>. The words carry the meaning, not the colour. */
export default function Pill({ tone = 'info', as: Tag = 'li', className = '', children }) {
  return (
    <Tag className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-semibold whitespace-nowrap ${TONES[tone]} ${className}`}>
      {children}
    </Tag>
  )
}

/** Status pill with a dot: "On shift" (green) or "Not clocked in" (grey). */
export function StatusPill({ on, children }) {
  return (
    <span
      className={`inline-flex items-center gap-2 rounded-full px-3 py-1.5 text-sm font-semibold ${on ? 'bg-ok-soft text-ok' : 'bg-subtle text-ink-3'}`}
    >
      <span aria-hidden="true" className={`size-2 rounded-full ${on ? 'bg-ok-dot ring-4 ring-ok-dot/20' : 'bg-faint'}`} />
      {children}
    </span>
  )
}
