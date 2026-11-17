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
    return <p className="muted small">No response body was received.</p>
  }
  return (
    <details className="raw">
      <summary>Raw response</summary>
      {/* tabIndex lets keyboard users scroll long payloads horizontally */}
      <pre tabIndex={0} aria-label={`Raw response from ${providerName}`}>
        {prettyPrint(raw)}
      </pre>
    </details>
  )
}

export default RawPayload
