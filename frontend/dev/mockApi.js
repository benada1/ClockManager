import { handleAuthMock } from './mockAuth.js'

// DEV-ONLY mock of the backend, used by `npm run dev:mock` (vite --mode mock). It is a Vite
// dev-server plugin, so it never ends up in the production bundle.
//
// Pick a scenario with a query string on the PAGE url (the browser sends it as Referer),
// e.g. http://localhost:5173/?mock=open. Scenarios: see dev/mockAuth.js.
//
// Fixture times are fixed strings. The mock never reads a clock.

export function mockApi() {
  return {
    name: 'clockmanager-mock-api',
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        if (await handleAuthMock(req, res)) return
        const path = req.url?.split('?')[0]
        if (path === '/api/health') {
          res.writeHead(200, { 'Content-Type': 'application/json' })
          res.end(JSON.stringify({ status: 'ok (mock)' }))
          return
        }
        next()
      })
      server.config.logger.info('  Mock API enabled: add ?mock=<scenario> to the page URL (see dev/mockAuth.js)')
    },
  }
}
