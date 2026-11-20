function prettyPrint(raw) {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw // not JSON (HTML error page, plain text...): show exactly what the provider sent
  }
}

/** Collapsible, sanitized raw provider body as returned by our server. */
function RawPayload({ raw, providerName }) {
  if (raw == null || raw === '') {
    return <p className="text-sm text-muted">No response body was received.</p>
  }
  return (
    <details className="group">
      <summary className="inline-flex min-h-11 cursor-pointer items-center text-sm font-semibold text-accent-ink">Raw response</summary>
      {/* tabIndex lets keyboard users scroll long payloads horizontally */}
      <pre
        tabIndex={0}
        aria-label={`Raw response from ${providerName}`}
        className="mt-2 max-h-80 overflow-auto rounded-field border border-line bg-subtle p-3 font-mono text-xs text-ink"
      >
        {prettyPrint(raw)}
      </pre>
    </details>
  )
}

export default RawPayload
