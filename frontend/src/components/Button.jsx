const VARIANTS = {
  primary: 'bg-accent text-white border-accent hover:bg-accent-strong hover:border-accent-strong',
  secondary: 'bg-surface text-ink border-line-strong hover:bg-canvas',
  quiet: 'bg-transparent text-accent border-transparent hover:bg-accent-soft',
  onAccent: 'bg-surface text-accent-strong border-surface hover:bg-accent-soft hover:border-accent-soft focus-visible:outline-white', // sits on an accent block
}
const SIZES = {
  md: 'min-h-11 px-5 text-base rounded-ui',
  lg: 'min-h-16 px-6 text-xl font-semibold rounded-ui',
  xl: 'min-h-20 px-8 text-2xl font-semibold rounded-block',
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
      className={`inline-flex items-center justify-center gap-2 border font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${VARIANTS[variant]} ${SIZES[size]} ${block ? 'w-full' : ''} ${className}`}
      {...rest}
    >
      {busy && (
        <span aria-hidden="true" className="size-4 animate-spin rounded-full border-2 border-current border-t-transparent" />
      )}
      {busy && busyLabel ? busyLabel : children}
    </button>
  )
}
