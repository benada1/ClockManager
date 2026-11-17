# 01 — Trusted clock

**Status:** Approved 2026-11-17 (Q1–Q3 answered, see §8) · Steps 1–3b implemented (`/health/time` D-012; Time lab D-013, page D-014–D-017; steps 3 and 3b landed in one commit because they share the wiring) · Step 4 (qa) next
**Lead agent:** backend · **Supporting:** qa (failure-mode tests, adversarial review)
**Master plan refs:** D1, D2, D3, D7, D9 · §4 "Time provider" · CLAUDE.md rule 1

---

## 1. Goal

One place in the backend, `ITrustedClock`, answers "what time is it **right now** in Europe/Zurich?"
with a value we can **justify later**: which external provider gave it, what the provider actually sent,
how long the request took, and whether a second provider agreed. If it can't produce a trustworthy
answer, it **throws a specific error** (→ HTTP 503). It never guesses and never falls back to the
server's clock.

Phase 1 builds and tests the clock on its own. Punches (Phase 3) are its first real user.

## 2. Scope

### In scope
- `ITrustedClock.GetNowAsync(ct)` → `TrustedInstant`, plus typed failure exceptions.
- Two provider clients (typed `HttpClient`s), each with its own response validation.
- An orchestrator that calls both, cross-checks them, and decides: accept / fall back / fail closed.
- Options (`TrustedClock` section) validated on startup.
- NodaTime for time-zone rules (tzdb), `Europe/Zurich` resolved once.
- `GET /health/time`: status of each provider (for ops and the UI banner in Phase 5).
- `FakeTrustedClock` in the test project, for every later phase.
- Tests for every failure mode in §5 with a stub `HttpMessageHandler` (no network in CI).

### Out of scope (later phases)
- The monotonic guard "new punch ≥ last punch" (D7) needs the DB → **Phase 3** (`PunchService`).
- Storing `TrustedInstant` fields in the DB → **Phase 2** schema (this plan defines what to store).
- `serverNowUtc` for the UI's live clock → **Phase 3** (`/api/me/status`).
- A dev switch to simulate an outage for the manual UI test → **Phase 5**.

## 3. Provider research (probed 2026-11-17 from the dev machine)

| Provider | Endpoint | Response | Verdict |
|---|---|---|---|
| **time.now** | `GET https://time.now/developer/api/timezone/Europe/Zurich` | `utc_datetime` (`...Z`), `unixtime`, `datetime` **with offset**, `utc_offset`, `timezone`, `dst`. Unknown zone → 404 JSON. | **Primary.** Gives an unambiguous UTC instant. |
| **timeapi.io** | `GET https://timeapi.io/api/time/current/zone?timeZone=Europe%2FZurich` | `dateTime` **without offset** (`2026-11-17T13:03:02.4715932`), `timeZone`, `dstActive`. Unknown zone → 400. | **Secondary.** The local time alone is ambiguous in October; `dstActive` resolves it (see §4.3). |
| worldtimeapi.org | `GET https://worldtimeapi.org/api/timezone/Europe/Zurich` | Connection failed on every attempt (4 tries over the day, https and http). | Rejected; the master plan's candidate was unreachable. |
| timezonedb.com | — | Needs an API key. | Not needed; we'd have a secret to manage for no gain. |

**Latency (~15 samples per provider over the day):** usually 0.33–0.42 s; spikes of 0.5–1.1 s; **one burst where both took
~4 s at the same time**. Neither publishes rate limits. → Timeout **3 s** (not the 2 s guessed in the master
plan), "low precision" flag above **1.5 s** (master plan §7).

## 4. Design

### 4.1 The contract
```csharp
public interface ITrustedClock
{
    /// Throws TrustedTimeException (→ 503) if no trustworthy time is available. Never returns a guess.
    Task<TrustedInstant> GetNowAsync(CancellationToken ct);
}

public sealed record TrustedInstant(
    Instant Utc,                 // NodaTime instant: the exact moment, no time zone attached
    Offset ZurichOffset,         // +01:00 or +02:00 at that instant (from tzdb, checked against the provider)
    string Provider,             // "time.now" | "timeapi.io"
    int RoundTripMs,             // request duration = how uncertain the value is
    string RawPayload,           // provider's body, sanitized (client_ip removed), max 1 KB
    string? CrossCheckedBy,      // the other provider if it answered and agreed, else null
    int? CrossCheckDeltaMs,      // how far apart the two providers were
    bool LowPrecision);          // RoundTripMs > LowPrecisionThresholdMs
```
`ZurichLocal` / `ZurichDate` are computed from `Utc` + tzdb, never taken from the provider's text.

### 4.2 Flow of `GetNowAsync`
1. Call **both providers in parallel**, each with its own 3 s timeout, timing each with a monotonic
   stopwatch (`Stopwatch.GetTimestamp`, not the wall clock).
2. Validate each response (§4.3). A provider that times out, returns 4xx/5xx/429, or sends an invalid body
   counts as **failed**, with a typed reason.
3. Decide:

| Primary | Secondary | Result |
|---|---|---|
| ✅ | ✅, agrees within 2 s | **Accept primary**, `CrossCheckedBy = secondary` |
| ✅ | ✅, **differs > 2 s** | **503 `time.providers_disagree`**: we can't tell which is right |
| ✅ | ❌ | Accept primary, `CrossCheckedBy = null` (single source, see Q2) |
| ❌ | ✅ | Accept secondary, `CrossCheckedBy = null` |
| ❌ | ❌ | **503 `time.invalid_response`** if any provider answered with a bad body, else **503 `time.unavailable`** |

4. Log one structured line per call: provider results, RTTs, delta, decision. No retries on the punch path:
   the other provider *is* the retry.

**Comparing two clocks fairly:** the two requests don't land at exactly the same moment. Each provider's
value is compared after correcting for when its request was sent (the midpoint of its own round trip), so
the network delay isn't mistaken for disagreement.
*(Step 2, QA-04)* The midpoint method can't be more precise than `(RttA + RttB) / 2`, so "agrees" means
`|delta| ≤ CrossCheckToleranceMs` **or** `|delta| < (RttA + RttB) / 2` (D-008).

### 4.3 Validation per provider (reject, never "fix")
Common: HTTP 200, JSON body, required fields present, `timezone == "Europe/Zurich"`, year in a plausible
fixed range (≥ 2026 and < 2100; *not* compared with the server clock, which rule 1 forbids), parsed with
`CultureInfo.InvariantCulture`.

- **time.now**: instant = `utc_datetime`. Check that `unixtime` agrees (±1 s), that `utc_offset` equals the tzdb
  offset of Zurich **at that instant**, and that `datetime` = instant + offset.
- **timeapi.io**: the local `dateTime` has no offset, so derive it: `dstActive ? +02:00 : +01:00`, giving the
  instant. Then check that tzdb agrees that this local time **with that offset** really exists in Zurich.
  - In the October repeated hour, `02:30` + `dstActive=true` → `00:30Z`; `02:30` + `dstActive=false` → `01:30Z`.
    Both are valid and distinct. ✅
  - A local time in the March gap (`02:30` on 2026-03-29) can't exist → invalid response.
  - If `dstActive` contradicts tzdb (e.g. `true` in January) → invalid response.

### 4.4 Errors (extend `Common/Errors`, D-003)
`TrustedTimeException : AppException`, always **503**, with `code` one of `time.unavailable`,
`time.invalid_response`, `time.providers_disagree`. ProblemDetails extension `providers`:
`[{ "name": "time.now", "outcome": "timeout" }, ...]`. These are short reason codes only; raw bodies go
to the log, not the client.

### 4.5 Configuration (`TrustedClockOptions`, section `TrustedClock`, validated on start)
```json
"TrustedClock": {
  "TimeoutMs": 3000,                  // 500–10000
  "CrossCheckToleranceMs": 2000,      // 100–30000
  "LowPrecisionThresholdMs": 1500,    // must be < TimeoutMs
  "Primary":   { "Name": "time.now",   "BaseUrl": "https://time.now/" },
  "Secondary": { "Name": "timeapi.io", "BaseUrl": "https://timeapi.io/" }
}
```
No API keys are needed, so there are no secrets.

### 4.6 Guard rails
- Add `NodaTime.SystemClock` to `BannedSymbols.txt` so NodaTime's own "now" can't sneak in.
- Measuring round trips uses `Stopwatch` (elapsed time only, allowed by rule 1). It sits behind a tiny
  `IElapsedTimer` interface so tests control "how long the request took" without real waiting.
- `/health/time` caches its result for 10 s (local clock used only for the cache TTL, marked `clock-ok`),
  so the health check can't hammer the providers. The punch path **never** uses that cache.

### 4.7 `GET /health/time`
```json
200 { "canPunch": true,  "providers": [ { "name": "time.now", "status": "ok", "roundTripMs": 341 },
                                        { "name": "timeapi.io", "status": "ok", "roundTripMs": 402 } ],
      "crossCheck": "agree", "deltaMs": 37 }
503 { "canPunch": false, "providers": [ ... "status": "timeout" ... ], "code": "time.unavailable" }
```
Public (no auth until Phase 4), with nothing sensitive in it.

## 5. Edge cases → handling → test

| # | Scenario | Expected | Test |
|---|---|---|---|
| E1 | Both OK and agree | primary accepted, cross-checked | `Accepts_primary_when_both_agree` |
| E2 | Providers differ by 5 s | 503 `time.providers_disagree` | `Fails_closed_when_providers_disagree` |
| E3 | Primary timeout (3 s+) | secondary accepted, single source | `Falls_back_to_secondary_on_timeout` |
| E4 | Primary 500 / 503 / 429 | secondary accepted | theory over status codes |
| E5 | Both down | 503 `time.unavailable`, both reasons listed | `Fails_closed_when_all_down` |
| E6 | 200 with HTML / non-JSON / empty | provider invalid → fallback; all invalid → `time.invalid_response` | theory |
| E7 | Missing `utc_datetime` / `dstActive` | invalid | theory |
| E8 | Wrong zone (`Europe/London`) | invalid | `Rejects_wrong_zone` |
| E9 | time.now `utc_offset` ≠ tzdb (e.g. `+02:00` in January) | invalid | `Rejects_offset_not_matching_tzdb` |
| E10 | timeapi.io `dstActive=true` in January | invalid | `Rejects_dst_flag_not_matching_tzdb` |
| E11 | Repeated hour: 02:30 dstActive true/false (T6) | 00:30Z / 01:30Z, distinct | `Resolves_repeated_hour_with_dst_flag` |
| E12 | Exact DST start instant 2026-03-29T01:00Z (T7) | local 03:00 +02:00 | `Dst_start_instant_maps_to_0300` |
| E13 | Local time in the March gap (02:30, 2026-03-29) | invalid | `Rejects_nonexistent_local_time` |
| E14 | Year 1970 / 2150 | invalid | theory |
| E15 | RTT 1.8 s (within timeout) | accepted, `LowPrecision = true` | `Flags_slow_response_low_precision` |
| E16 | Server process in another TZ (`TZ=Pacific/Kiritimati`) | identical results | run the suite with TZ set (CI step) |
| E17 | Caller cancels (client disconnects) | `OperationCanceledException`, not 503 | `Propagates_caller_cancellation` |
| E18 | `client_ip` in time.now payload | removed from `RawPayload` | `Sanitizes_client_ip` |

Provider fixtures (sample bodies) are saved from real responses into `ClockManager.Tests/TestData/`.
An opt-in **live** test (`[Trait("Category","Live")]`, skipped in CI) hits both real providers once.

## 6. Steps and commits (one commit per task, each reviewed by the user before it lands)

| # | Task | Who | Commit |
|---|---|---|---|
| 1 | Contract, options, NodaTime, `FakeTrustedClock`, time.now client + validation + its tests (E7–E9, E12, E14, E18) | backend | `feat(time): add ITrustedClock with time.now provider and response validation` |
| 2 | qa writes the failing tests for the orchestration (E1–E6, E15, E17) and timeapi.io (E10, E11, E13) **from this plan**; backend adds timeapi.io + orchestrator + 503 mapping until they pass | qa → backend | `feat(time): add timeapi.io fallback, cross-check and fail-closed errors` |
| 3 | `/health/time` + its tests; README section; plan status | backend | `feat(time): add /health/time endpoint` |
| 3b | **Time lab** (added 2026-11-17 at the user's request, to see the providers live): a Development-only endpoint `GET /api/dev/time-lab` that runs the **real** `ITimeSource`s + orchestrator once and returns, per provider, the outcome, RTT, sanitized raw body and parsed UTC/offset, plus the final decision (accepted provider, cross-check delta, or the 503 code). It's not mapped outside Development. A frontend page with an "Ask both providers" button shows it. | backend (endpoint) → frontend (page) | `feat(time): add dev-only time lab to inspect providers live` |
| 4 | qa adversarial review of the finished clock (E16 TZ run, anything missed; plus: the one unexplained test failure seen once on 2026-11-17 and not reproduced in 31 runs — stress the suite under CPU load; the ~2.5 s **cold first request** after startup seen end-to-end (TLS/DNS), which can make the first punch low-precision or near the timeout; health check during shutdown; a provider that throws in the Time lab; `/health/time` isn't behind the Vite `/api` proxy — decide before the Phase 5 banner) → each real finding = a failing test + a fix, as **separate `fix:` commits** | qa → backend | `fix(time): ...` (only if found) |

DECISIONS.md entries go **in the commit that makes the decision**: D-006 providers (time.now + timeapi.io,
worldtimeapi rejected), D-007 NodaTime over `TimeZoneInfo`, D-008 both providers on every punch + 2 s
tolerance, D-009 single-source punches (per Q2), D-010 3 s timeout / 1.5 s low precision, D-011 deriving the
offset from `dstActive` for timeapi.io. Edge-case rows E1–E18 go into the edge-case table.

Knowledge check at the end of the phase (master plan §5).

## 7. Risks
| Risk | Mitigation |
|---|---|
| Both free providers slow down together (seen: ~4 s burst) | 3 s timeout + clear 503 + `/health/time`; documented as a known limit. Never the local clock. |
| A provider changes its JSON format | Strict validation → it counts as failed, the other provider takes over; fixture tests show what we expect. |
| Rate limiting (unpublished) | Low volume (one call per provider per punch), the health check is cached, 429 is handled as a failure. |
| Two calls per punch add latency | Parallel calls: the cost is the slower of the two (~0.4 s typical, 3 s max). |

## 8. Questions for the user (answered 2026-11-17: recommended option for all three)
- **Q1 → both providers on every punch.** **Q2 → single-source punches accepted, `CrossCheckedBy = null`.**
  **Q3 → NodaTime.**

- **Q1. Call both providers on every punch (recommended)**, or call the secondary only when the primary fails?
  Both = every punch is cross-checked and "up but wrong" providers are caught; cost = one extra HTTP call.
- **Q2. When only one provider answers, accept the punch and mark it "not cross-checked" (recommended)**,
  or refuse it? Refusing doubles the chance of a 503. Accepting still uses a real external time, which is
  what rule 1 demands.
- **Q3. NodaTime (recommended)**, or .NET's built-in `TimeZoneInfo`? NodaTime ships its own time-zone database,
  so Windows and Linux (CI) give identical answers, and it has clear types (`Instant`, `LocalDateTime`,
  `ZonedDateTime`) that make "is this UTC or local?" bugs hard to write. Cost: one dependency.
