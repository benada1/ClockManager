import { useId } from 'react'

export const INPUT =
  'min-h-12 w-full rounded-field border bg-input px-3.5 text-base text-ink transition-[border-color,box-shadow] placeholder:text-faint focus:border-accent focus:ring-4 focus:ring-ring'

/** Label + input + hint + inline error, wired together for screen readers. */
export default function Field({ label, error, hint, className = '', ...inputProps }) {
  const id = useId()
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  return (
    <div className={`flex flex-col gap-2 ${className}`}>
      <label htmlFor={id} className="text-sm font-medium text-ink-2">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={[hintId, errorId].filter(Boolean).join(' ') || undefined}
        className={`${INPUT} ${error ? 'border-danger' : 'border-line-strong'}`}
        {...inputProps}
      />
      {hint && (
        <p id={hintId} className="text-sm text-muted">
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className="text-sm font-medium text-danger">
          {error}
        </p>
      )}
    </div>
  )
}
