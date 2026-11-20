const SIZES = {
  sm: 'size-7 text-xs',
  md: 'size-8 text-xs',
  lg: 'size-10 text-base',
  xl: 'size-14 text-xl',
}

/** First letter of a name in a soft circle. Decorative: the name is always written next to it. */
export default function Avatar({ name, size = 'md' }) {
  const initial = (name ?? '').trim().charAt(0).toUpperCase() || '?'
  return (
    <span
      aria-hidden="true"
      className={`inline-flex shrink-0 items-center justify-center rounded-full bg-accent-soft font-semibold text-accent-ink ${SIZES[size]}`}
    >
      {initial}
    </span>
  )
}
