import Card from './Card.jsx'
import { CalendarIcon } from './icons.jsx'

/** Centered "nothing here yet" card: icon, a short title, one sentence, an optional next step. */
export default function EmptyState({ title, children, action, headingLevel: H = 'h2' }) {
  return (
    <Card className="flex flex-col items-center gap-4 px-8 py-14 text-center">
      <span aria-hidden="true" className="inline-flex size-16 items-center justify-center rounded-card bg-subtle text-muted">
        <CalendarIcon />
      </span>
      <div className="flex max-w-sm flex-col gap-1.5">
        <H className="text-lg font-semibold">{title}</H>
        {children && <p className="text-base text-muted">{children}</p>}
      </div>
      {action}
    </Card>
  )
}
