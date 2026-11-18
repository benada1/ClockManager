import Banner from '../../components/Banner.jsx'

const MORE_THAN_USUAL =
  'Clocking out records the moment you press the button, so tell your manager if you really finished earlier.'

/** Notices for shifts the server flagged. Nothing is ever closed for the user. */
export default function ShiftNotices({ status }) {
  const forgotten = status.flags?.includes('forgotten_clock_out')
  const longOpen = status.openShift?.flags?.includes('long_shift')
  const longLast = !status.openShift && status.lastClosedShift?.flags?.includes('long_shift')
  return (
    <>
      {(forgotten || longOpen) && (
        <Banner tone="warning" title="This shift has been open for a very long time.">
          You may have forgotten to clock out. {MORE_THAN_USUAL}
        </Banner>
      )}
      {longLast && (
        <Banner tone="warning" title="Your last shift was unusually long.">
          If that is not right, tell your manager.
        </Banner>
      )}
    </>
  )
}
