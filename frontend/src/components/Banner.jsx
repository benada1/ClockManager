const TONES = {
  error: 'border-danger-line bg-danger-soft text-danger',
  warning: 'border-warn-line bg-warn-soft text-warn',
  success: 'border-ok-line bg-ok-soft text-ok',
  info: 'border-line-strong bg-canvas text-ink',
}
// A word as well as a color, so the tone never rests on color alone.
const LABELS = { error: 'Problem', warning: 'Notice', success: 'Done', info: 'Info' }

/** Calm inline message. Errors are announced at once (alert), the rest politely (status). */
export default function Banner({ tone = 'info', title, children, action, className = '' }) {
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      className={`flex flex-col gap-3 rounded-ui border p-4 text-base ${TONES[tone]} ${className}`}
    >
      <div className="flex flex-col gap-1">
        <p className="font-semibold">
          <span className="sr-only">{LABELS[tone]}: </span>
          {title}
        </p>
        {children && <div className="text-ink">{children}</div>}
      </div>
      {action}
    </div>
  )
}
