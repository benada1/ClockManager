const VARIANTS = {
  primary: 'bg-accent text-on-accent border-accent hover:bg-accent-strong hover:border-accent-strong',
  secondary: 'bg-surface text-ink-2 border-control hover:bg-subtle',
  strong: 'bg-strong text-on-strong border-strong hover:opacity-90',
  quiet: 'bg-transparent text-accent-ink border-transparent hover:bg-accent-soft',
}
const SIZES = {
  md: 'min-h-11 px-4 text-sm rounded-control',
  lg: 'min-h-12 px-5 text-base font-semibold rounded-field',
  xl: 'min-h-16 px-6 text-lg font-semibold rounded-cta',
  icon: 'size-11 rounded-control',
}

/** The one button for the whole app. `busy` disables it and shows `busyLabel`. */
export default function Button({
  variant = 'primary',
  size = 'md',
  busy = false,
  busyLabel,
  block = false,
  type = 'button',
  className = '',
  children,
  disabled,
  ...rest
}) {
  return (
    <button
      type={type}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`inline-flex cursor-pointer items-center justify-center gap-2 border font-medium transition-[background-color,box-shadow,opacity] disabled:cursor-not-allowed disabled:opacity-60 ${VARIANTS[variant]} ${SIZES[size]} ${block ? 'w-full' : ''} ${className}`}
      {...rest}
    >
      {busy && (
        <span aria-hidden="true" className="size-4 animate-spin rounded-full border-2 border-current border-t-transparent" />
      )}
      {busy && busyLabel ? busyLabel : children}
    </button>
  )
}

/** A link that looks like the secondary button (Review, Open week). */
export function LinkButton({ className = '', children, ...rest }) {
  return (
    <a
      className={`inline-flex min-h-11 items-center justify-center gap-2 rounded-control border border-control bg-surface px-4 text-sm font-medium whitespace-nowrap text-ink-2 no-underline transition-colors hover:bg-subtle ${className}`}
      {...rest}
    >
      {children}
    </a>
  )
}
