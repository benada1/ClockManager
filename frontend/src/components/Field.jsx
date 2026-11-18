import { useId } from 'react'

/** Label + input + hint + inline error, wired together for screen readers. */
export default function Field({ label, error, hint, className = '', ...inputProps }) {
  const id = useId()
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      <label htmlFor={id} className="text-base font-medium text-ink">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={[hintId, errorId].filter(Boolean).join(' ') || undefined}
        className={`min-h-11 w-full rounded-ui border bg-surface px-3 text-base text-ink ${
          error ? 'border-danger-line' : 'border-line-strong'
        }`}
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
