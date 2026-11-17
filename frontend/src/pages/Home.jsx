import { useEffect, useState } from 'react'
import { getHealth } from '../api/health.js'
import { href } from '../router.js'

function Home() {
  const [status, setStatus] = useState('checking...')

  useEffect(() => {
    const controller = new AbortController()
    getHealth({ signal: controller.signal })
      .then((d) => setStatus(d?.status ?? 'unknown'))
      .catch((err) => {
        if (err.name !== 'AbortError') setStatus('unreachable')
      })
    return () => controller.abort()
  }, [])

  return (
    <>
      <h1 tabIndex={-1}>Clock Manager</h1>
      <p role="status">API status: {status}</p>
      <p>
        Want to see where punch times come from? Open the <a href={href('/time-lab')}>Time lab</a> to ask the
        external time providers live.
      </p>
    </>
  )
}

export default Home
