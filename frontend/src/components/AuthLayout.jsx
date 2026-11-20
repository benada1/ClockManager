/** Shared frame of the sign up and log in pages: centered title, one card, a link to the other page. */
export default function AuthLayout({ title, intro, children, footer }) {
  return (
    <div className="mx-auto my-auto flex w-full max-w-md flex-col gap-6 pb-10">
      <div className="flex flex-col gap-2 text-center">
        <h1 tabIndex={-1} className="text-3xl font-semibold tracking-tight text-ink">
          {title}
        </h1>
        {intro && <p className="text-base text-muted">{intro}</p>}
      </div>
      <div className="rounded-card border border-line bg-surface p-6 shadow-card sm:p-8">{children}</div>
      <p className="text-center text-sm text-muted">{footer}</p>
    </div>
  )
}
