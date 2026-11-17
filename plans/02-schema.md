# 02 — Data model and database

**Status:** Approved 2026-11-17 (Q1 SQL Express, Q2 tables move to Phases 3 and 7, Q3 punch columns on Shifts) · Step 1 done (schema, migration, seeder, 19 tests); step 2 (qa race + constraint tests, CI SQL) next
**Lead agent:** db · **Supporting:** qa (constraint, race and precision tests), backend (consumes it in Phase 3)
**Master plan refs:** D4, D5, D8 · §4 "Concurrency" · CLAUDE.md §2 (domain model), rule 2 (DB enforces the rules)

---

## 1. Goal

A SQL Server schema where **invalid states can't be stored**, even if the C# code has a bug or two
requests race: at most one open shift per employee, clock-out after clock-in, a clock-out that is
either fully recorded or not at all. Every punch keeps its **evidence** from the trusted clock
(provider, raw answer, round trip, cross-check), so a reviewer can later see why a time was trusted.

Phase 2 builds only the storage and proves the constraints with tests against a **real** SQL Server.
The punch logic that writes to it is Phase 3.

## 2. Scope

### In scope
- EF Core 8 + SQL Server: `ClockManagerDbContext`, `Employee`, `Shift` (with an owned `PunchStamp` for each punch).
- Explicit configuration per entity (`IEntityTypeConfiguration<T>`), NodaTime ↔ SQL value converters.
- Constraints in the DB: filtered unique index, check constraints, FK without cascade, `rowversion`.
- The initial migration (committed), applied automatically **in Development only**; seed data (1 manager,
  3 employees) by a Development-only seeder.
- Integration-test fixture against a real SQL Server; constraint, race and precision tests.
- CI: a SQL Server container in GitHub Actions so the integration tests run on every push.
- README: database setup for SQL Server Express (detected on this machine), LocalDB and Docker.

### Out of scope (moved, see Q2)
- `IdempotencyRecord` → **Phase 3**, designed together with the punch endpoint that needs it.
- `ShiftCorrection` → **Phase 7**, with the manager feature.
- Login credentials (`PasswordHash`) → **Phase 4** migration.
Each later phase adds its own migration. The migration history then shows how the schema grew.

## 3. Environment findings (checked 2026-11-17)
- **LocalDB is not installed** (CLAUDE.md assumes it). **SQL Server 2025 Express** is running as `.\SQLEXPRESS`,
  Windows authentication works, and the user is `sysadmin` (can create test databases).
- **Docker is not installed**, so Testcontainers can't run locally. CI (Linux) has Docker.
- `dotnet-ef` is not installed: a **local tool manifest** (`.config/dotnet-tools.json`) pins it to the repo,
  so `dotnet tool restore` gives everyone the same version.

## 4. Schema

### `Employees`
| Column | Type | Rules |
|---|---|---|
| `Id` | `int` identity | PK |
| `Username` | `nvarchar(64)` | required, **unique** |
| `DisplayName` | `nvarchar(128)` | required |
| `Role` | `nvarchar(16)` | required, `CHECK (Role IN ('Employee','Manager'))` |
| `IsActive` | `bit` | default 1 (a deactivated employee can't punch, Phase 3) |
| `RowVersion` | `rowversion` | optimistic concurrency |

### `Shifts`
| Column | Type | Rules |
|---|---|---|
| `Id` | `bigint` identity | PK |
| `EmployeeId` | `int` | FK → `Employees`, **ON DELETE NO ACTION** (shifts are records, never cascade-deleted) |
| `ClockIn_Utc` | `datetimeoffset(7)` | required, always offset +00:00 (the instant) |
| `ClockIn_ZurichOffsetMinutes` | `smallint` | required, `IN (60, 120)` |
| `ClockIn_ZurichDate` | `date` | required, the **work date** (Zurich date of clock-in, D4) |
| `ClockIn_Provider` | `nvarchar(32)` | required |
| `ClockIn_RoundTripMs` | `int` | required, ≥ 0 |
| `ClockIn_CrossCheckedBy` | `nvarchar(32)` | null = single source (D-009) |
| `ClockIn_CrossCheckDeltaMs` | `int` | null when not cross-checked |
| `ClockIn_LowPrecision` | `bit` | required |
| `ClockIn_RawPayload` | `nvarchar(1024)` | required (sanitized, D-006) |
| `ClockOut_*` | same 9 columns | **all null** while the shift is open |
| `RowVersion` | `rowversion` | Clock Out vs manager correction race (Phase 7) |

**Constraints (the heart of this phase):**
1. `UX_Shifts_OneOpenPerEmployee`: `UNIQUE (EmployeeId) WHERE ClockOut_Utc IS NULL`. Two tabs or two servers
   clocking in at once: the second insert fails, whatever the C# code did (D5).
2. `CK_Shifts_ClockOutAfterClockIn`: `ClockOut_Utc IS NULL OR ClockOut_Utc > ClockIn_Utc`. Strictly greater:
   a zero-length shift is refused (T10, T11).
3. `CK_Shifts_ClockOutComplete`: the ClockOut columns are either **all null or all set**. A half-written
   clock-out can't exist.
4. Offsets restricted to +01:00 / +02:00 for both punches; `RoundTripMs ≥ 0`.
5. Indexes: `(EmployeeId, ClockIn_ZurichDate)` for history by day/week (Phase 6).

**Why `datetimeoffset` at +00:00 plus a separate offset column, not a local `datetimeoffset`:** SQL compares
`datetimeoffset` by instant, so constraints and ordering are correct either way. Keeping the stored value in UTC
means nobody reads a local wall-clock time as the truth by accident. The Zurich offset and the work date are
stored **as seen at punch time**, so reports stay stable even if tz rules change later.

### NodaTime mapping (no extra package)
Small EF value converters: `Instant ↔ DateTimeOffset (UTC)`, `Offset ↔ smallint minutes`,
`LocalDate ↔ DateOnly (date)`. The domain code keeps NodaTime types (D-007), and the database keeps standard SQL types.
Precision: `datetimeoffset(7)` keeps 100 ns, more than any provider sends (time.now: µs, timeapi.io: 100 ns).

## 5. Running it
- **Dev default:** `appsettings.Development.json` → `Server=.\SQLEXPRESS;Database=ClockManager;Integrated Security=true;TrustServerCertificate=true`.
  No password, so it's allowed in git (CLAUDE.md rule 3). Any other server goes in user-secrets or an env var.
- **Startup in Development:** apply pending migrations, then seed if empty. **Production:** never migrates
  itself; use `dotnet ef database update` or a migration bundle (documented).
- **Integration tests:** connection from env var `CLOCKMANAGER_TEST_SQL`, default `.\SQLEXPRESS` locally. Each
  test class gets its **own throwaway database** (`ClockManager_Test_<guid>`), migrated with the real
  migrations (not `EnsureCreated`, so the migrations themselves are tested) and dropped afterwards. In CI
  (`CI=true`) a missing SQL Server **fails** the run instead of skipping silently.
- **CI:** a step starts `mcr.microsoft.com/mssql/server:2022-latest` with a **password generated in the run**
  (`openssl rand`), so no secret is stored anywhere, waits until it accepts connections, and sets `CLOCKMANAGER_TEST_SQL`.

## 6. Edge cases → tests (integration, real SQL Server)
| # | Scenario | Expected |
|---|---|---|
| S1 | Second open shift for the same employee | insert fails with unique-index violation (SQL 2601) |
| S2 | 10 parallel inserts of an open shift, same employee, separate connections | **exactly 1** row |
| S3 | Open shifts for two different employees | both allowed |
| S4 | Closed shift + new open shift, same employee | allowed (the index only covers open shifts) |
| S5 | Clock-out = clock-in (T11) / 2 s before clock-in (T10) | check violation (SQL 547) |
| S6 | ClockOut_Utc set but ClockOut_Provider null (half-written) | check violation |
| S7 | Offset 0 or 180 minutes | check violation |
| S8 | Delete an employee who has shifts | FK violation; shifts survive |
| S9 | Round trip of T6 (`2026-10-25T00:30Z` +02 / `01:15Z` +01), T7 and a µs/100 ns instant | values come back **exactly** equal |
| S10 | Updating a shift changes `RowVersion`; a stale update fails with a concurrency exception | yes |
| S11 | Seeder run twice | still 1 manager + 3 employees |
| S12 | Migrations: the model has no pending changes (`HasPendingModelChanges` is false) | CI fails if someone forgets a migration |

## 7. Steps and commits (autonomous, CLAUDE.md §6.1)
| # | Task | Who | Commit |
|---|---|---|---|
| 1 | Local `dotnet-ef` tool, EF packages, converters, entities + configurations, DbContext, initial migration, Development-only migrate + seeder, connection settings, the test fixture + S1, S5, S9, S11, S12, README DB section | db | `feat(db): add Employee/Shift schema with one-open-shift index and check constraints` |
| 2 | qa: race test S2, the rest of §6, plus anything it finds; CI SQL container | qa (+ main session for CI) | `test(db): constraint, race and precision tests against real SQL Server` + `ci: run integration tests against a SQL Server container` |
| 3 | Fixes for real qa findings as separate `fix:` commits | db | only if found |

DECISIONS: SQL Express as the dev default (deviation from CLAUDE.md's LocalDB), own NodaTime converters vs a
package, owned `PunchStamp` columns vs a separate `Punches` table, UTC `datetimeoffset` + offset column, a
Development-only migrate/seed, a throwaway database per test class, moving Idempotency/Correction tables to
their phases. End-of-phase summary for the developer when done.

## 8. Risks
| Risk | Mitigation |
|---|---|
| Integration tests slow down the suite | One database per test class (not per test); creating it takes about 1–2 s. |
| Tests leave databases behind after a crash | Names are prefixed `ClockManager_Test_`; the fixture drops stale ones older than the run. |
| SQL Server 2025 locally vs 2022 in CI behave differently | We only use long-standing features (filtered index, check, rowversion). CI is the referee. |

## 9. Questions for the user (answered 2026-11-17: recommended option for all three)
- **Q1. Use the SQL Server Express already on your machine as the dev database (recommended)?** It works now.
  LocalDB would need an install, and the README will still document LocalDB and Docker as alternatives.
- **Q2. Move the `IdempotencyRecord` and `ShiftCorrection` tables to Phases 3 and 7 (recommended)?** Each is
  designed together with the feature that uses it, instead of being guessed now.
- **Q3. Store each punch's evidence as columns on the `Shifts` row (recommended)**, or in a separate `Punches`
  table (one row per punch)? Columns keep the "one open shift" index and the "out after in" check as plain
  single-row SQL constraints. A separate table would need triggers or app logic for those rules.
