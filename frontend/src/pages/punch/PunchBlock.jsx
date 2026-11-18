import Button from '../../components/Button.jsx'

/**
 * One of the two big switch blocks. Active = accent color and a working button;
 * inactive = calm grey, dashed edge, a disabled button and the reason in words.
 */
export default function PunchBlock({ label, active, busy, busyLabel, disabled, onPress, reason, className = '', children }) {
  return (
    <section
      aria-label={label}
      className={`flex flex-col justify-between gap-8 rounded-block border-2 p-6 transition-colors duration-300 md:p-10 ${
        active ? 'border-accent bg-accent text-white shadow-md' : 'border-dashed border-line-strong bg-canvas text-muted'
      } ${className}`}
    >
      <div className="flex flex-col gap-4">
        <h2 className="text-lg font-semibold uppercase tracking-wide">{label}</h2>
        {children}
      </div>

      <div className="flex flex-col gap-3">
        <Button
          size="xl"
          block
          variant={active ? 'onAccent' : 'secondary'}
          busy={active && busy}
          busyLabel={busyLabel}
          disabled={!active || disabled}
          onClick={onPress}
        >
          {label}
        </Button>
        {reason && <p className="text-base">{reason}</p>}
      </div>
    </section>
  )
}
