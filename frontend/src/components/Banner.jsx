const TONES = {
  error: 'border-danger-line bg-danger-soft',
  warning: 'border-warn-line bg-warn-soft',
  success: 'border-ok-line bg-ok-soft',
  info: 'border-line bg-subtle',
}
const TITLE = { error: 'text-danger', warning: 'text-warn', success: 'text-ok', info: 'text-ink' }
// A word as well as a color, so the tone never rests on color alone.
const LABELS = { error: 'Problem', warning: 'Notice', success: 'Done', info: 'Info' }

/**
 * Calm inline message. Errors are announced at once (alert), the rest politely (status).
 * `announce={false}` when a live line elsewhere already says it.
 */
export default function Banner({ tone = 'info', title, children, action, announce = true, className = '' }) {
  return (
    <div
      role={announce ? (tone === 'error' ? 'alert' : 'status') : undefined}
      className={`flex flex-col gap-3 rounded-field border px-4 py-3.5 text-sm ${TONES[tone]} ${className}`}
    >
      <div className="flex flex-col gap-1">
        <p className={`text-base font-semibold ${TITLE[tone]}`}>
          <span className="sr-only">{LABELS[tone]}: </span>
          {title}
        </p>
        {children && <div className="text-ink-2">{children}</div>}
      </div>
      {action && <div className="flex flex-wrap gap-2">{action}</div>}
    </div>
  )
}
