export default function Loading({ children = 'Loading…' }) {
  return (
    <p role="status" className="flex items-center justify-center gap-3 py-10 text-base text-muted">
      <span aria-hidden="true" className="size-5 animate-spin rounded-full border-2 border-accent border-t-transparent" />
      {children}
    </p>
  )
}
