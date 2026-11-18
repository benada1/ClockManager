import Card from './Card.jsx'

/** Shared frame of the sign up and log in pages: title, one card, a link to the other page. */
export default function AuthLayout({ title, intro, children, footer }) {
  return (
    <div className="mx-auto flex w-full max-w-md flex-col gap-6">
      <div className="flex flex-col gap-2">
        <h1 tabIndex={-1} className="text-xl font-semibold text-ink">
          {title}
        </h1>
        {intro && <p className="text-base text-muted">{intro}</p>}
      </div>
      <Card>{children}</Card>
      <p className="text-base text-muted">{footer}</p>
    </div>
  )
}
