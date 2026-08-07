# BUILD_PLAN.md

Eleven phases in dependency order. A phase is not done because the code exists. It
is done when the stated proof passes, which in every case is something that can be
run rather than something that can be argued.

Each phase names the invariants from `CLAUDE.md` §2 that its work can plausibly
break. That list is not a summary of the phase. It is what the review at sign-off
looks at first, and what `guards.ps1` checks on every push where the invariant is
grep-checkable [D-67].

No phase below carries a status. `PROGRESS.md`'s phase status table is where that
lives, and it is the only place [D-66]. This document holds what is decided.

---

## Checkpoints, commits and sign-off

**A phase is divided into numbered checkpoints and a commit maps to exactly one.**
Checkpoint `4.3` is committed as `Phase 4 / 4.3 - <what it did>`. That is the whole
convention, and it exists so that a commit can be traced to the plan clause that asked
for it without anyone remembering what was happening that week.

Churn commits put `chore` where the checkpoint number goes: `Phase 4 / chore - line
endings`. A correction pass uses its letter as the phase: `Phase O / O.1 - <what it
did>`. A commit that spans two checkpoints means the checkpoints were drawn wrong;
split it.

**Detail is authored one phase ahead, not eleven.** Phases below carry scope and a
definition of done from the outset, because those are decisions. Numbered checkpoints
are authored when the previous phase signs off, because writing them earlier means
writing against assumptions the build has not tested yet. Each phase's checkpoints
are authored at the previous phase's sign-off.

### Sign-off

Two steps.

1. **CI green on the phase branch.** The definition of done runs in CI or
   `PROGRESS.md` names the line CI does not cover and why. Nothing is
   recorded by hand that a run can record.

2. **A review in a session that did not build the phase.** Not a checklist.
   Three questions:
   - does the code match the architecture sections this phase implements
   - does every number in `PROGRESS.md` or `DECISIONS.md` trace to something
     that produced it
   - did the build resolve any contradiction silently instead of reporting
     it

   It writes what it found into `PROGRESS.md` and corrects nothing.

Then author the next phase's checkpoints, now that what exists is known.

A session that has committed in this repository does not run step 2.

**The prompt issued for each phase is still archived verbatim in `prompts/spent/`
as `phase-<n>-<slug>.md`, and is corrected afterwards only to match the text
actually issued [D-63].** It is one cheap file that explains why the code looks
the way it does. Nothing is reconciled against it [D-67].

---

## Phase P — Data probe

**Runs before phase 0, because its answers change the schema.**

A scratch tool under `tools/probe`. A measuring instrument, not a component. Print
raw counts. No interfaces, no repository pattern, no retry policy, no abstraction
over the data provider. It will be deleted or rewritten and that is fine.

Run everything against five or six companies known to sit between $300M and $2B
across different sectors, plus one megacap as a control. Everything looks fine on a
megacap, which is exactly why a megacap tells you nothing.

Four questions:

1. **News archive depth.** Request news for a small cap with a from-date five years
   back. Report the earliest article actually returned. If it stops short, the
   digest field is null for the earlier backfill and that has to be known now.
2. **Sentiment coverage.** Pull the sentiment endpoint for each small cap. Report
   days of series returned and article counts per day. A sparse or empty series
   means the sentiment screen cannot see small caps at all.
3. **Flow coverage.** Insider transactions and short interest for each. Report
   transaction counts over 90 days and whether short interest is populated. If
   these are thin below $2B, the flow screen is a large and mid cap screen in
   practice and the size quota arithmetic changes.
4. **Filing dates.** Pull fundamentals for each and report, per quarter, whether a
   filing date is present and what the gap is between it and period end. If it is
   missing or equals period end on small caps, D-46 is not achievable from this
   source, which is a design problem rather than an inconvenience.

Also confirm the bulk end-of-day endpoint returns a full US day and report the row
count, since that number is the basis of the freshness guard tolerance.

### Checkpoints

| # | Scope |
|---|---|
| P.1 | Repository baseline: solution, console project at `tools/probe`, secrets wiring |
| P.2 | Probe scaffold: one file, raw HTTP and JSON, no abstraction, failures printed not thrown |
| P.3 | Sample selection, programmatic, six names in band across four or more sectors plus one control |
| P.4 | The four measurements plus the bulk end-of-day row count |
| P.5 | Recording into `PROGRESS.md`, editing the existing table in place |

**Done when:** all four questions have numeric answers recorded in `PROGRESS.md`,
and any that came back badly has a corresponding entry in `DECISIONS.md`.

**No API key enters the repository.** ~~Environment variable or user secrets.~~
[superseded, D-55] Secrets live in `appsettings.Secrets.json` beside the project that
needs them, excluded by wildcard, with `appsettings.Secrets.example.json` as the
committed template. D-55 replaced the older mechanism before this phase ran, and the
phase was executed against the corrected instruction. Check the gitignore before the
first commit.

---

## Phase 0 — Rails

Repository structure, solution layout, Postgres schema and migrations, the stage
registry, run logging, and the conformance test over write ownership. One trivial
stage end to end to prove the rails work.

Conventions: .NET 10, `.slnx` solution format, `Directory.Build.props` and
`Directory.Packages.props` for central package management, `guards.ps1` for the CI
greps, `migrate.ps1` and `seed.ps1` at the root. Migrations are snapshot-first.
Also opens `FIXTURES.md` and `CHANGELOG.md`.

Project layout is `CLAUDE.md` §4 and D-56. The `Api` project must not reference
`Pipeline`, and phase 0 adds a test asserting that, since it is the structural form of
the read-only guarantee.

Also a bare run viewer. Not the run health screen, just enough to see stages,
durations and row counts. Debugging a thirty-four component pipeline through raw
SQL for eight phases is miserable, and this costs an afternoon.

The rails are the anti-drift mechanism. Building them after the first real component
means the component defines the pattern instead of the pattern defining the
component.

### Checkpoints

| # | Scope |
|---|---|
| 0.1 | Solution layout per `CLAUDE.md` §4 and D-56, central package management, `.gitattributes`, `guards.ps1` stub |
| 0.2 | Postgres schema and migrations from `SCHEMA.md`, snapshot-first, running clean from empty |
| 0.3 | Stage abstraction and registry: declared read and write sets, date and config version in, pure |
| 0.4 | Run logging, and the conformance test asserting that no two components claim the same component-table-operation triple, matching the per-operation declarations in `SCHEMA.md` [INVARIANT 10 as amended] |
| 0.5 | The `Api` must not reference `Pipeline` test |
| 0.6 | Bare run viewer: stages, durations, row counts. Not the run health screen |
| 0.7 | One no-op stage end to end, proving the rails |
| 0.8 | Open `FIXTURES.md` and `CHANGELOG.md`, ~~first corpus version bump~~ [retired, D-67] |

**Done when:** a no-op stage runs, logs, and appears in the viewer; the
write-ownership test reads the registry and passes; migrations run clean from empty.

**Invariants at risk:** 10, 11.

---

## Phase 1 — Ingest and universe

Typed HTTP client for the data provider. Bulk end-of-day, fundamentals keyed on
filing date, sentiment for the whole universe, flow, events. The universe builder
applying D-4. The freshness guard.

**C34 FlowEngine is a layer 2 component and is built here rather than in phase 2**,
because its work is inseparable from the ingest it derives from: the two source tables
and the three daily metrics computed off them are one decision, D-61, and splitting
them across phases would leave `flow_daily` declared and unwritten for a phase.

### Checkpoints

| # | Scope |
|---|---|
| 1.1 | Typed HTTP client: `api_token` query auth, explicit `fmt=json` on every call, the `::` filter form with colons percent-encoded, and the 1,000-requests-a-minute limit |
| 1.2 | Bulk end-of-day ingest into `price_daily`, plus the settled-day rule: the most recent available day is still accreting and is not valid. C02 re-loads a trailing window of dates every night rather than tonight alone, so a day loaded short tops up on a later run, which is what actually heals accretion; D-68's upsert is what makes that safe. The window length is `price.reload_window_days`, coupled to `freshness.settled_window_days` [A10, A26]. **Its dates are the last N calendar dates counting back from the run date, with an empty response for a non-session tolerated rather than treated as a fault** [A26]: there is no trading calendar until 1.3, a weekend returns an empty array rather than an error, and keying on calendar dates does not trip 1.14's zero-row halt, which counts the stage total rather than any one date |
| 1.3 | Freshness guard ~~on D-59's thresholds, abort below 40,000 and alert below 45,000~~ [widened, D-65]: recency against the exchange calendar, completeness on D-59's unchanged thresholds, and settledness as a relative test, the date's row count at or above a configured fraction of the median count of the last N dates strictly before it, computed from `price_daily` alone and on first sight [A10]. ~~settledness by comparing a re-fetch against the rows already stored~~ [replaced, A10: a stage is a pure function of its date and config version, and a guard reading what it saw on a previous wall-clock run is not, so two databases with identical `price_daily` contents would disagree]. The guard owns all three, writes nothing, makes exactly one provider call, and returns the trading date the rest of the run uses. C02 loads what the provider offers; C07 decides what is usable. **With fewer than `freshness.settled_window_days` prior dates in `price_daily` there is no median to compare against, and settledness is not evaluated: the date passes that check and the run log records that it was skipped for want of history** [A26]. Neither alternative is right on its own. Aborting means the first run ever cannot bootstrap; passing silently turns the guard off during exactly the period the data is least trustworthy. Skipping visibly is neither, because recency and completeness still apply and D-64 gives the absolute floors precisely that job, answering whether a file is catastrophically short. The guard is degraded during bootstrap rather than absent, and the run log says so |
| 1.4 | Fundamentals ingest keyed on `filing_date_effective`, implementing D-62's per-ticker substitution, setting `filing_date_unknown_reason` across its four states, and recording enough for the count to be computed downstream. The exclusion itself is 1.5's, not this checkpoint's [D-4, INVARIANT 1] |
| 1.5 | Universe builder applying D-4, including the clean filing-gap exclusion computed for the date being built rather than read from any stored total [D-62, M.1], with 20-day median dollar volume computed from `price_daily` rather than any provider average |
| 1.6 | Sentiment ingest for the whole universe, tolerating a series with rows only on days carrying news |
| 1.7 | Flow ingest from `sec-filings/form4` and not the legacy endpoint, into `insider_transaction` and `institutional_holding` at natural grain per D-61, with `transaction_code` retained so open-market purchases are separable. Paged on `page[offset]` and `page[limit]`, which is the form the endpoint accepts; `limit` and `offset` are silently ignored and a call using them returns one page and looks complete [1.9]. The two halves differ in what they can promise: form4 is fully backfillable, `meta.total` matching the filings index on every ticker checked, while `Holders::Institutions` is a top-20 snapshot at one or two report dates with no 13f endpoint, so `inst_ownership_change` accumulates forward only [1.9, D-69] |
| 1.8 | Events ingest, and the derived `flow_daily` at ticker-by-day |
| 1.9 | Endpoint sweep at phase start, recording what the subscription reaches |
| 1.10 | Tests: no fundamental readable before its effective filing date; the substitution and exclusion fixtures registered in `FIXTURES.md`; a stale end-of-day file aborts the run |
| 1.11 | Layer folders in `Pipeline` per `CLAUDE.md` §4, and `NoOpStage` retired at the first real stage that replaces it. Its trespassing-stage fixture re-anchors onto a test-local stage, since that fixture proves the guard rather than the registry |
| 1.12 | A bulk load path on `IStageData`, declared-access-checked exactly as the other two routes are, over Npgsql binary COPY, with row order into the stream explicitly sorted [`ARCHITECTURE.html` §19] |
| 1.13 | As-of config resolution in `Core`, resolving a key for a simulated date as `MAX(version)` among rows set at or before it, and the ~~nine~~ [corrected, A5 then A10 then A14] **fourteen** keys this phase consumes seeded through `seed.ps1`: six `universe.*`, three `fundamentals.*` including `widest_gap_alert_days`, four `freshness.*` including `settled_fraction` and `settled_window_days`, and `price.reload_window_days`. Seeded rows carry a `set_at` at or before the earliest date the system will ever resolve for, and version 1 inserts `ON CONFLICT DO NOTHING` so a second run is a no-op [A9]. `Worker`'s hardcoded config version goes [D-43, INVARIANT 13] |
| 1.14 | The nightly run sequence: stages in declared order, a failure or a writing stage producing zero rows halting everything after it, and the trading date the guard returned carried into every stage after it [RUNBOOK failure table, D-65] |

Four of these were not in the phase as first authored. The phase assumes rails
phase 0 did not build: there is no bulk load path though `ARCHITECTURE.html` §19
specifies binary COPY, no as-of config resolution though this phase brings the
first nine real keys, no layer folders though `CLAUDE.md` §4 requires them, and
`StageRunner` runs one named stage, so 1.3's abort has no run to abort. Numbers
are appended rather than inserted because a checkpoint number is a plan
reference and not an execution order, which 1.9 already establishes.

**Done when:** one night of the whole US market lands; the universe builds to
roughly 2,000 names, with the count of names excluded by the clean-gap criterion
recorded rather than assumed; feeding the freshness guard deliberately stale data
aborts the run and produces no orders; a date that fails settledness is re-read on a
later run rather than skipped [D-65]; a test asserts no fundamental value is
readable before its ~~filing date~~ [corrected, D-62] effective filing date, with the
equality, null and negative-gap cases each exercised; sentiment lands for the whole
universe and a name with rows on only a handful of days in the window is ingested
without error; `insider_transaction` and `institutional_holding` land at their own
grain with `transaction_code` retained, and `flow_daily` derives from them at
ticker-by-day; the endpoint sweep from 1.9 is recorded in `PROGRESS.md`;
`NoOpStage` is gone and the registry holds no component name `ARCHITECTURE.html`
§3 does not have; a stage that COPYs into a table it does not declare throws
before a connection is opened; two versions of one config key resolve to the
older value for a date between them and the newer for a date after; and one
command runs the night end to end, with a guard abort leaving no rows in any
table a later stage writes.

**Invariants at risk:** 1, 10, 11, 12, 13.

**Carried obligation:** whatever the probe found about news depth and flow coverage
constrains what this phase can promise downstream. Record it.

---

## Phase 2 — Compute

Indicators, valuation, market context including sector relative strength, and the
percentile engine with size-and-sector cells and the fifteen-member fallback.

C34 FlowEngine belongs to this layer and was built in phase 1, with D-61's ingest.

**Done when:** a known ticker's indicators match a hand-computed reference; a
percentile spot-check confirms cell membership is correct and the fallback fires
where cells are thin.

**Invariants at risk:** 10, 11, 12.

---

## Phase 3 — Backfill of ingest and compute

Five years, including delisted tickers, two-pass and parallel. Ticker-partitioned
for ingest, indicators and valuation. Date-partitioned for percentiles.

**Done when:** five years present; a spot check confirms delisted names are included;
a full rebuild finishes in minutes rather than hours; a replay of one historical
date produces byte-identical output to the first run.

**Invariants at risk:** 11, 12, 13.

---

## Phase 4 — Screens and candidate selection

**This is the phase where you find out whether the idea works, and it is the last
one before money is spent on models.**

Gate engine, screen engine with the five screens as config rows, floors, the
candidate allocator with quotas and no backfill, the attribution write, and the
concentration monitor. Then backfill screen scores over the five years using the
two-pass approach, so floors are live from real history.

**Done when:** a night yields roughly 26 to 30 candidates; the 2/3/3 size
distribution holds; megacap share sits under a third including inside the 2022
drawdown; distinct tickers over any 60-day window exceed 250; overlap between
screens falls somewhere near 10 to 20 percent; attribution rows exist for every
candidate with the config version stamped.

**Invariants at risk:** 1, 2, 3, 4, 10, 13.

**What this phase does not test.** Profitability. The screens are a candidate
generator and their job is to surface a plausible and varied set for judgement.
Discrimination is the researcher's job, which is the entire reason the researcher
exists. Screen scores failing to predict forward returns on their own is not a
failure here. See D-42 and the rule in `CLAUDE.md` §11 against tuning screens on
forward returns before anything has judged them.

---

## Phase 5 — Digest chain

Local client over the OpenAI-compatible endpoint, Haiku client, the ordered chain
with health checks, the nightly rotation of two candidates to the secondary, and the
gate.

**Done when:** digests are produced for a night and recorded with provider and model
name; stopping the local server falls through to the secondary and the fallthrough
is visible in the record; stopping both halts the run before any researcher call and
produces no orders; the rotation is present every night regardless of primary health.

**Invariants at risk:** 7, 10, 15.

---

## Phase 6 — The researcher

Dossier builder producing the cached prefix and per-candidate blocks. Researcher
client running twice, Opus 5 batched and V4 Pro synchronous. Proposal validator with
all three checks.

**Done when:** the prefix snapshot test passes across a full night of calls; cache
hit rate exceeds 90 percent; a deliberately corrupted citation in a test dossier is
rejected; a proposal whose stated probability contradicts its own stop and target is
rejected; the batch path completes and the 09:00 deadline behaviour is exercised.

**Invariants at risk:** 6, 9, 10, 16.

**Carried obligation:** this is the first phase that spends money. The cost ledger
must be recording per model before the first full night, not after.

---

## Phase 7 — Risk, execution and portfolios

Risk gate with the arbitration steps, paper broker, position manager, portfolio
registry, portfolio runner.

**Done when:** a night produces orders for every active portfolio; the worked
arbitration example in `ARCHITECTURE.html` §10 reproduces exactly as a fixture;
exposure matching holds across a sequence including a full-abstention night;
retiring a portfolio by config change stops new entries while open positions run to
natural exit.

**Invariants at risk:** 8, 10, 14.

---

## Phase 8 — Learning loops

Forward return filler with all three benchmark columns, screen tuner, lesson writer,
calibration reporter.

**Done when:** forward returns fill correctly including the acquisition and
delisting cases; a test asserts the tuner's query never touches the SPY column; the
tuner moves slots on backfilled data and respects the floor of four and cap of
twelve; a reliability diagram renders from real backfilled attribution.

**Invariants at risk:** 5, 10, 13, 14.

---

## Phase 9 — API and UI

Read-only query API, read model builder, Blazor client, the six screens.

**Done when:** all six screens render from real data; the digest chain indicator
shows all three states correctly including running-on-secondary; the readiness check
fires ahead of the evening window; no API endpoint can write anything except the
local model connection config.

**Invariants at risk:** 10, 15.

---

## Phase 10 — Soak

Run nightly with nothing acted on. Watch cost, failures, concentration, and the
validator rejection rate.

**Done when:** twenty consecutive clean nights; annual cost forecast tracking within
range of the estimate; no concentration alert unexplained; validator rejection rate
per model recorded and stable.

**Nothing is concluded here.** A soak proves the machine runs, not that the idea
works. Portfolio returns over twenty nights carry no information at all, and the
temptation to read them is strongest exactly when the system has just started
producing them. The first readable evidence is the validator rejection rate at four to
six weeks; the primary claim needs a year [VALIDITY.md §3].

**Invariants at risk:** all of them, which is the point of a soak.

---

## Carried obligations

Items that a phase inherits rather than discovers. Add here rather than losing them
in a phase prompt.

| From | Owed to | Item |
|---|---|---|
| P | 1 | Whatever the probe finds about news depth, sentiment and flow coverage constrains what phase 1 can promise |
| P | 4 | If filing dates are absent on small caps, D-46 needs revisiting before screens are built |
| P | 1 | Entitlement is per endpoint and invisible in the account payload, which showed identical fields on both sides of the mid-phase upgrade. Phase 1 determines what a subscription reaches by sweeping endpoints, never by reading a field |
| P | 1 | The base rate of unknown filing dates across the whole universe is unknown, and seven names is a hint rather than an answer. It is a query against `fundamental_snapshot` once 1.4 has ingested, read at phase 1 sign-off, and it decides whether D-62's exclusion rule removes a handful of names or a meaningful slice |
| P | 1 | The dollar volume proxy does not survive into phase 1. Sample selection used `avgvol_50d * adjusted_close` because the bulk feed carries no median dollar volume, and average volume is unadjusted while `adjusted_close` is adjusted, so the product understates for any name that split inside the window. **Closed by 1.5**, which computes the metric from `price_daily`. Recommended by the build session at P.5 and never entered here until K.9 |
| P | 4 | The S4 open-market purchase base rate is unknown. Transaction code P was 0 on all seven names over 90 days, so `distinct_buyer_count` had nothing to rank on in that window, and six small caps plus a control cannot say whether that is the market or the sample. It is a query against `insider_transaction` once 1.7 has ingested, read at phase 1 sign-off, not a task for any phase |
| 0 | all | The write-ownership test must be extended as each phase adds tables |
| 0 | 9 | The Ui references the Api project, so Data and Npgsql are in its compiled closure. A contracts assembly is the fix and is worth doing when the real UI is built, not before |
| 0 | 1 | `NoOpStage` is a registered component name `ARCHITECTURE.html` §3 does not have. It proved the rails and must not survive the first real stage |
| 0 | 1 | `TableWrite.Columns` is declared and asserted by nothing. Column-level write ownership is unenforced until a component declares a partial write, which C21 ForwardReturnFiller is the first to need |
| 0 | 1 | Five patterns in the phase P probe that phase 1's ingest must not inherit. All five were read in the deleted source before it went: an unguarded `.First()` selecting the control ticker, which throws mid-run and loses a transcript written only at the end; `?? 0` on `shares_amount` and `price_per_share` feeding a dollar total, so an absent price contributes zero to money [`CLAUDE.md` §6]; a superseded constant surviving in executable code, D-57's 65 days classifying gaps after D-62 replaced it, corrected at K.2 and found by a conformance pass rather than by a test; a clean-gap list built before the `g < 1` test, so gaps D-62 calls unknown counted toward the widest gap and toward the floor of four; and a paged endpoint read with one `limit=1000` call and no offset loop, which silently returns a cap rather than a count |
| 1 | 3 | `security` is one row per ticker carrying a single `size_bucket` and `market_cap`. C11 PercentileEngine ranks within size bucket, so a backfill of a 2021 date reads 2026's bucket for every name. `first_seen`, `last_seen` and `delisted_date` make membership reconstructable per date [D-48]; bucket and market cap are not |
| 1 | 4 | D-69. `inst_ownership_change` has no history from this source, `Holders::Institutions` being a top-20 snapshot at one or two report dates with no 13f endpoint. S4's other two inputs are fully backfillable. Decide before screen floors are drawn, since a floor from a two-input backfill against a three-input live screen is what D-58 rejected |
| 4 | 8 | Config version must be stamped on attribution rows from the first write, or the tuner cannot segment history |
| 6 | 10 | Cost ledger recording per model before the first full night |
