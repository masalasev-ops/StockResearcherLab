# Phase 3 — Backfill of ingest and compute

**Target** phase 3, five years of ingest and compute over history.
**Authored** 2026-08-11, at phase 2's sign-off, as `BUILD_PLAN.md` requires.
**Read against** `ARCHITECTURE.html` §02, §03, §04, §14, §16 and §19, `SCHEMA.md`,
`METRICS.md` §1 and §6, `VALIDITY.md` §5 and §6, and `SCREEN_LIFECYCLE.md` §3 and §9,
at HEAD `443f3cf`.
**Status** `DRAFT`, which is the only value this corpus uses for a plan:
`phase-2-compute.md` is still `DRAFT` and phase 2 is signed off and merged. The
numbered checkpoints below are phase scope and were authored into `BUILD_PLAN.md` by a
human rather than from here [`CLAUDE.md` §13].

**§3's four items are closed.** D-92, D-93, D-94 and D-95 were authored into
`DECISIONS.md` on 2026-08-11 under a new Backfill section, verbatim from §3 and each
`ACTIVE`. `BUILD_PLAN.md` phase 3 gained the eighteen checkpoints, the amended fourth
done-when line and eleven carried-obligation landings in the same pass, with the prior
wording of the done-when line in `CHANGELOG.md`. The clauses stay below as the text that
was authored from, following `phase-2-compute.md` §3.2's precedent of marking an item
closed rather than deleting it.

---

## Context

Phase 2 signed off at `68aafa4` and merged at `443f3cf`. The pipeline runs one night end
to end: twelve stages, 221 tests, the compute layer digesting identically on re-run. What
it has never done is run over anything but the present.

`BUILD_PLAN.md` states the scope as "five years, including delisted tickers, two-pass and
parallel. Ticker-partitioned for ingest, indicators and valuation. Date-partitioned for
percentiles", done when five years are present, a spot check confirms delisted names are
included, a full rebuild finishes in minutes rather than hours, and a replay of one
historical date produces byte-identical output to the first run. Invariants 11, 12 and 13
are at risk.

Everything after this phase depends on it. Phase 4's screen floors need 250 days of
history before they mean anything [D-9]; the tuner and the calibration report need
populated attribution from month one [D-47]; and the window has to contain a real
drawdown, because that is the only condition under which the quality and mean reversion
screens converge on the same megacaps, and it is better seen in history than discovered
live [`ARCHITECTURE.html` §05].

Three things make this phase larger than that sentence reads. Each is documented here
rather than found mid-build.

1. **`security` is one row per ticker carrying today's bucket.** C11 ranks inside
   size-bucket-by-sector cells, so a backfilled 2021 date would rank every name in its
   2026 cell. Carried obligation `1 → 3`.
2. **The backfill is not the nightly pipeline in a loop.** Every per-ticker endpoint
   returns full history in one call, so the ingest backfill is one coverage pass per
   source. Only prices are date-shaped, and even they flip to per-ticker.
3. **Nothing in the codebase runs a stage over a range.** C08 and C09 read a trailing
   window and compute one date. At the 14.4 seconds phase 2 measured, the naive loop over
   1,260 trading dates is about five hours, against a done-when line that says minutes.

Nine carried obligations land here. They are in §7 rather than scattered.

---

## 1. What exists, and what the backfill has to reach around

| Component | Shape today | What a backfill needs |
|---|---|---|
| C01 UniverseBuilder | Weekly. Upserts one row per ticker on `ON CONFLICT (ticker)`. Everything already computed as-of the date being built | A per-date landing place. Replayed per date into `security` it overwrites 1,260 times and ends holding the last date |
| C02 PriceIngestor | Trailing calendar window of `eod-bulk-last-day/US?date=`, 100 units a call | `eod/{t}`, 1 unit for a name's whole history. §19 already says historical price ingest partitions by ticker |
| C03 FundamentalsIngestor | Rotation of 500 a run, `fundamentals/{t}` at 10 units returning every period | One full pool sweep. History is not the problem; coverage is |
| C04 SentimentIngestor | Whole universe nightly, `sentiment.lookback_days` = 30 | One pass with `from` at the window start. Metered flat per ticker |
| C05 FlowIngestor | Rotation of 250 a run. **Carries C03's frozen-rotation defect unchanged** [D-91] | The defect closed first, then one universe sweep. form4 is fully backfillable [D-69] |
| C06 EventsIngestor | `calendar/earnings` forward 90 and backward 7, plus two bulk-by-date calls | `splits/{t}` and `div/{t}` at 1 unit, whole history. Earnings deliberately not, at 3.10 |
| C08, C09 | Read a trailing window, compute one date in C#, one COPY. `Compute` is public static | A range mode calling the same `Compute`, one read per ticker |
| C10, C34, C35, C11 | One set-based statement per date | A range statement, or a looped one against a date-leading index. Measured rather than guessed |

**No table carries a date-leading index.** `price_daily`, `indicator_daily`,
`valuation_daily`, `flow_daily` and `sentiment_derived_daily` are all
`PRIMARY KEY (ticker, date)` [`0001_snapshot.sql`, `0004_compute_columns.sql`], and every
per-date read constrains the column the index does not lead on.
`SCREEN_LIFECYCLE.md` §9.3 makes precisely this argument for `screen_score_daily` and
lands it in phase 4. The same argument lands here, at phase 3, for the 1.2 GB and 540 MB
tables C11 updates once per backfilled date.

---

## 2. The unit budget, which is the real constraint

The provider meters weighted units rather than requests, at 100,000 a day
[`PROGRESS.md`, endpoint weights, measured 2026-08-09]. A steady-state night already
spends 45,518 of them.

| Source | Endpoint | Weight | Pass | Units |
|---|---|---|---|---|
| Prices | `eod/{t}` | 1 | every admitted common stock, live and delisted | **50,785**, counted at 3.1 |
| Fundamentals | `fundamentals/{t}` | 10 | the ~4,800 candidate pool **plus every delisted common stock with a bar inside the window** | **48,000 + 10D**, D counted at 3.6 |
| Sentiment | `sentiments` | 5 per ticker | 2,841 names, one wide range | ~14,200 |
| Flow | `sec-filings/{t}/form4` | 10 per page | the universe, live names only [3.1] | ~258,000 |
| Splits, dividends | `splits/{t}`, `div/{t}` | 1 | the universe | ~5,700 |
| | | | **total** | **376,685 + 10D** |

**The price line is measured rather than estimated.** 3.1 read 18,174 live common
stocks and 32,611 delisted ones, disjoint, so the pool is 50,785 at one unit each
against the ~30,000 this table first carried.

**D is the count 3.6 produces and nothing here estimates it.** It is the number of
delisted common stocks with at least one `price_daily` bar at or after
`backfill.window_start`, which 3.6 is what makes computable: the symbol list carries no
delisting date, so the last bar is the only date there is and the call is what reveals
it. Names that stopped trading before the window start are excluded and cost nothing.

**The bracket, stated so nobody is surprised by either end.** D is between 0 and
32,611, so the phase total is between **376,685 and 702,795 units**, which is four days
and seven. Three of 3.1's five sampled delisted names last traded before the window and
two inside it, and five names is not a rate; no figure is claimed from it.

**Seven days is affordable and is stated rather than avoided.** The allowance gate at
3.4 makes a multi-day sweep work by construction: it halts cleanly at the wall, records
where it reached, and resumes on D-68's per-grain idempotence. What the upper bound
costs is calendar time, not correctness, and the alternative is a reconstruction that is
survivorship-clean on price and not on membership. If 3.6 returns a D that changes that
reading, the number goes in `PROGRESS.md` and the pool is not narrowed to fit.

Roughly four to seven days of allowance, and the flow sweep is three of them. That
figure is a fixed cost the design already accepted; deferring it does not reduce it, and
deferring it would guarantee S4 has no backfilled history at exactly the moment D-69
asks whether S4 survives, which is the deferral pre-deciding the question it claims to
be waiting on.

**Four days of allowance needs a day boundary, and the checkpoints carry it.** A bounded
parallel loop over roughly 30,000 tickers hits the wall mid-sweep and learns by failing.
D-68 makes resumption free, so an interrupted sweep costs nothing; what costs something is
not knowing where it stopped. Every ingest sweep therefore reads the remaining allowance
before each unit of work and halts cleanly when the next one will not fit, recording where
it reached. The mechanism is at 3.4 and the keys are at 3.3.

**An exhausted allowance and a short page are different observations and must not collapse
into each other.** D-71 records a short page and continues, because a provider disagreeing
with itself cannot be recovered by asking again. An allowance wall is a pre-flight verdict
from `/api/user` before a call is made, not a response shape that arrived, which is the
same structural distinction D-71 draws between which condition ended the loop. A `402` or
a `429` reaching the paging client is neither: it is an in-flight failure and stays fatal,
because an allowance error absorbed as a shortfall reports a complete sweep over a partial
one.

**The price pool is every admitted common stock, not the universe.** Price, liquidity,
market cap and history are per-date criteria, and pre-applying them to decide what to load
would delete from history exactly the names that later failed. Instrument type is the one
criterion that is not per-date and `SymbolList` already applies it, which is the same
universe filter applied sooner rather than a second one [D-2, D-4, INVARIANT 1].

**Prices take whatever `eod/{t}` returns.** One unit for five years or twenty, so the
depth is a disk decision and disk is the cheapest thing in this project while units are
the scarcest. The compute window is the only bound, and a bound stated as a number of
years is the family of defect D-83 removed. Deep history cannot be re-fetched cheaply once
it is the deep past.

---

## 3. Decisions that need authoring before code

Four. Each is authored content under `CLAUDE.md` §13 and none is the build session's to
write. Clauses are in `CLAUDE.md` §15's amendment format so they paste into a build
prompt, and each names the checkpoints it blocks. The numbers run in the order the
decisions were drafted rather than in section order, and nothing is missing between them.

### 3.1 `security_daily`, and per-date membership

Blocks 3.2, 3.11, 3.12. The largest item in the phase.

```
D-92 Universe membership, size bucket, market cap and sector are stored per
date in security_daily. security keeps identity alone. ACTIVE

C11 ranks inside size-bucket-by-sector cells [D-10] and security carries one
row per ticker, so a backfilled 2021 date would rank every name in its 2026
cell. The cell is the population D-10 defines, and a percentile computed over
a slightly wrong cell is not inspectable afterwards: nothing downstream can
see it or correct it.

Deriving it at read time was weighed and does not work. The bucket itself
derives, being a comparison of one name's market cap against two absolute
config floors [UniverseBuilder.Bucket], but C11's cell is (size_bucket,
sector) and the fifteen-member test counts the non-null population in that
cell on that date, so ranking one row needs every other row's bucket and
sector on the same date. Market cap as-of needs shares_outstanding readable
on filing_date_effective <= date, and sector is a provider call with no
history from this source at all. The derivation is therefore the whole store
recomputed per date, which is a store.

A month-end grain was weighed and rejected for the reason the derivation was
rejected against: it puts an approximation underneath cell membership rather
than beside it.

security_daily is ticker by date, written by UniverseBuilder, carrying
sector, size_bucket, market_cap and is_active. security keeps ticker, name,
first_seen, last_seen and delisted_date, which is identity and lifespan.
Every reader meaning "the universe on a date" reads security_daily; a reader
meaning "this ticker" reads security.

This subsumes the is_active obligation rather than sitting beside it. C01
having no path that deactivates a name was a phase 1 finding [PROGRESS.md,
2026-08-09] and it stops mattering for any read, because membership is
reconstructable per date by construction rather than by a flag someone has to
remember to clear.

Sector is the one column that is not point-in-time, and that is stated rather
than hidden. This provider carries no sector history, so a ticker's sector is
fetched once and carried across the window. A reclassification inside the
window is invisible, which is a bounded distortion of cell membership and is
recorded in PROGRESS.md rather than proxied.

DoD: migration 0007 creates security_daily with PRIMARY KEY (ticker, date);
SCHEMA.md gains its section with UniverseBuilder as declared writer and
security's section loses the four moved columns under D-73's clean-edit rule
with the prior wording in CHANGELOG.md; market_cap is numeric and
guards.ps1's ExpectedMonetary moves from 18 to 19 [INVARIANT 16]; C11's join
reads the most recent security_daily row at or before the date; a test
asserts a name that crossed a bucket floor inside the window carries the
lower bucket before and the higher after.
```

**C01's cadence does not change and that is deliberate.** §3 runs it weekly on Sunday. The
backfill evaluates membership on the same weekly cadence and C11 reads the most recent
`security_daily` row at or before the date, so backfilled cells sit on the identical
population rule as live ones. That is D-58's principle applied to membership: a floor
drawn from a population the live system does not share is not a floor.

### 3.2 A backfill runs the same components, never a second implementation

Blocks 3.4, 3.13, 3.14, 3.15.

```
D-93 A backfill is the registered components executed over a range. No
component exists that a night does not run. ACTIVE

SCHEMA.md names PriceIngestor as price_daily's writer and IndicatorEngine as
indicator_daily's, and the conformance test asserts no two components claim
the same component-table-operation triple [INVARIANT 10]. A
HistoricalPriceIngestor would be a second claim on the same triple, and
ARCHITECTURE.html §3 would not name it, which RegistryNameTests catches.
Neither is an accident of the machinery: a loader with its own arithmetic is
a second implementation of every formula, and the reference fixtures at 2.5
to 2.8 would then cover half of what runs.

So the range mode is a second entry point on the same class.
IBackfillStage.ExecuteRangeAsync(from, to) beside IStage.ExecuteAsync(date).
WriteSet, ReadSet, the registry and §3 are all unchanged.

Config resolves per date being computed, not once for the range [D-43,
INVARIANT 13]. A window key resolved at the range end would give a backfilled
date a different answer from a nightly re-run of the same date, which is
exactly the equality the phase's fourth done-when line asserts.

Idempotence rather than a transaction is what makes an interrupted backfill
resumable, which D-68 already states in as many words: one transaction
spanning twelve million rows is its own failure mode.

DoD: no new component name appears in the registry; RegistryNameTests and
WriteOwnershipConformanceTests pass unchanged; a test asserts range-mode
output for one date equals nightly-stage output for that date byte for byte;
run_log records the range a range execution covered.
```

### 3.3 C05's rotation gets its own attempt record

Blocks 3.5 and 3.9. Carried obligation `0006 → 3`.

```
D-95 FlowIngestor orders on its own attempt record, and the two rotations
share their ordering rule rather than a table. ACTIVE

FlowIngestor.SelectionFor carries C03's defect unchanged: never-fetched
first, then ticker ordinal, so it freezes on the alphabetical head once the
pool is covered, and the 14 of 250 that answer 404 Symbol not found stay
never-fetched and are re-asked on every run [D-91, which names this as not
closed by it].

flow_fetch_attempt, one row per ticker, four columns, written for every
selected ticker whether or not the fetch yielded rows, read strictly before
the run date. That is D-91's shape applied to the component it explicitly
left open, and its three reasons carry over unchanged: the record is of the
attempt, attempts are read strictly before the run date, and universe
membership is a tiebreak rather than a tier.

A shared table was rejected: two components writing one table is two claims
on one triple [INVARIANT 10]. What is shared is the ordering function, so the
two rotations cannot drift apart the way the code and the catalogue did.

DoD: the three fixtures registered at 0006 have C05 counterparts; a 404 name
is re-offered on a later pass rather than held at the head; a re-run of one
date selects the same names; the run log separates new from refreshed and
names the oldest attempt in the selection.
```

### 3.4 The window start, and the floor underneath it

Blocks 3.3, 3.13, 3.15.

```
D-94 The backfill window start is a stored date, and the compute window is
the only bound on how deep the price load goes. ACTIVE

eod/{t} costs one unit for five years or twenty, so the load depth is a disk
decision rather than a unit one and the loader takes whatever the call
returns. What is bounded is the range compute runs over, and it is bounded by
a date rather than by a count of years.

A count of years resolved against the run date moves the window on every
re-run, so two backfills over one store would compute different date sets and
the phase's fourth done-when line could not be tested at all. A stored date
resolves as-of by the same rule as every other key and gives the same window
whenever it is read [D-43, INVARIANT 13]. It is also the D-83 case: a bound
stated as a number of years is a claim about what a later phase will need,
where the compute window is a bound someone can check.

There is a floor underneath it that the key does not show.
ConfigSeeder.SeedInstant is 2020-01-01 and RequireVersionAsync fails for any
earlier date [D-72], so no stage resolves config before it whatever
price_daily holds. price_daily reaching further back than the pipeline can
compute for is the intended state rather than a defect: the extra history
costs bytes now and cannot be re-fetched cheaply once it is the deep past.

DoD: backfill.window_start is seeded as a date and its Consumer column is
filled from the composition code; a test asserts a window start before
SeedInstant fails the run with the config-version error rather than computing
against a config that resolved to nothing; two backfill runs over one store
compute the same date set.
```

---

## 4. Stages and checkpoints

Eighteen checkpoints in five stages, one commit each, `Phase 3 / 3.n - what it did`. Run
`ci.ps1` per checkpoint, which is the per-checkpoint verification since 1.12 and the only
thing that cannot reach the test step off a stale binary.

**This plan is archived to `prompts/spent/` before any code, not after.** Phase 1 and
phase 2 both archived at the end, and phase 2's archive is missing half of what was issued
[`PROGRESS.md`, 2.13]. `CLAUDE.md` §3 says after writing code; two phase records say the
practice does not survive that reading.

### Stage A — Before anything is loaded

**3.1 The endpoint sweep.** Same shape as 1.9: a scratch app outside the repository,
transcript to `docs/evidence/phase-3/`, checked for token leakage before committing.
Entitlement is per endpoint and invisible in the account payload, so this is what was
called rather than what a field claims.

**Priced before it is run, because it is the first thing the phase spends and the
remaining allowance is about 9,500 units.** One symbol list at 1, three `eod/{t}` at 1,
two `sentiments` at 5 to compare a narrow range against a wide one, one `fundamentals/{t}`
at 10, one filings index plus about four `form4` pages at 10 each, and two each of
`splits/{t}` and `div/{t}` at 1. Under 150 units with slack. Every question is bracketed
between two `/api/user` reads, which cost nothing and are the mechanism the existing
weights were measured with, so the sweep's own spend is measured rather than projected and
3.3's weight keys are confirmed by the same run.

Seven questions, and two of them can stop the phase.

- Does `exchange-symbol-list/US?delisted=1` return anything, and how many? **D-48 and
  `VALIDITY.md` §6's survivorship mitigation both rest on it and neither has ever been
  tested against this subscription.**
- Does `eod/{t}` return a full series for a delisted ticker, and how far back?
- Is `sentiments` still 5 units a ticker over a five-year range? The flat-per-ticker weight
  was measured at 1, 10 and 20 tickers over a narrow range and never over a wide one. The
  whole sentiment backfill rests on it.
- Does `fundamentals/{t}` return every period or a capped set?
- Deepest `form4` page reachable, and `meta.total` against the filings index, on a delisted
  name.
- History depth from `splits/{t}` and `div/{t}`.
- **Does the `fundamentals/{t}` payload already in hand carry an EPS actual or an estimate,
  and if not, which endpoint would and at what weight?** Asked against the response the
  question above already paid for, at no extra units. `last_two_earnings_surprises` is
  bolded in §07 as one of five fields that can flip a verdict and has no input anywhere in
  the store [carried obligation `2 → 3`].

  **This question cannot come back as work for this phase, and the checkpoint says so
  rather than discovering it.** The endpoints this system calls are known and none carries
  the field, so the answer is about an endpoint C03 does not call, and adding one is a
  change to phase 1's ingest and to C03's parse. The outcome is therefore a finding naming
  what would have to be added, at what weight, and what a universe pass of it would cost,
  handed to whoever reopens that parse. A compute backfill closing an ingest gap by
  widening its own scope is what `CLAUDE.md` §3 forbids.

*Done when:* every question has a number in `PROGRESS.md`, and any that came back badly has
an entry in `DECISIONS.md`. If the delisted list is unreachable, that is a finding that
blocks the phase's second done-when line and is reported rather than worked around. The
sweep's measured spend is recorded against the ~150 projected here, since a gap between
them is the weights table going stale rather than an arithmetic slip.

**3.2 Migration `0007`.** `security_daily`, and a date-leading index on `price_daily`,
`indicator_daily`, `valuation_daily`, `flow_daily` and `sentiment_derived_daily`. Blocked
on D-92. `IF NOT EXISTS` throughout, because `ci.ps1` asserts a second migrate prints
nothing to apply.

Index rather than declarative range partitioning, and the reason is recorded: an index is
reversible and cheap on an empty table, partitioning is a schema decision far cheaper
before 1.2 GB than after, and `SCREEN_LIFECYCLE.md` §9.3 sets the precedent of deciding it
once with the numbers in hand. If 3.15 measures the per-date percentile update as the
binding cost, partitioning is what the finding recommends.

*Done when:* migrate runs clean from empty and again as a no-op; `SchemaParityTests` finds
`security_daily` in both directions; `guards.ps1` check 4 passes with `market_cap` declared
money and `ExpectedMonetary` at 19, asserted rather than assumed.

**3.3 Config keys, including the allowance.** `backfill.window_start` as a date [D-94],
`backfill.ticker_concurrency`, and whatever 3.1 forces. Seeded through
`ConfigSeeder.Keys`, `Set by` naming the checkpoint, Consumer filled in
`CONFIG_REFERENCE.md` from the composition code rather than from the key name.

**The allowance keys land here, because §2's table is the weights table and a weight at a
call site is the magic number `CLAUDE.md` §8 rules out.** `backfill.daily_unit_allowance`
at 100,000, `backfill.unit_reserve` holding back what a night costs so a sweep cannot
starve the nightly run, and one weight key per endpoint the sweeps call, seeded from §2 and
confirmed against 3.1's brackets.

The weights are measurements and can go stale if the provider re-prices, so they project
whether the next unit of work fits and never decide that it did. What decides is the
reading from `/api/user`, and a drift between the projection and the reading is recorded
rather than smoothed over.

*Done when:* `seed.ps1` is a no-op on a second run; every phase 3 key resolves for a
simulated date; no weight or allowance appears as a literal at a call site; the seeded key
count assertion moves, which is that number doing the job it was made exact for.

**3.4 The range contract, and the allowance gate.** `IBackfillStage` on the existing
classes, the driver skeleton, `run_log` recording a range. Blocked on D-93. Nothing loads
yet.

**The gate is stated once here and named in the done-when of every sweep that carries it**,
rather than written out five times, which is the duplication D-76, D-77 and D-83 each
removed. Before each unit of work a sweep reads the remaining allowance from `/api/user`,
which costs nothing, and halts cleanly when the projected weight of the next unit exceeds
what is left above `backfill.unit_reserve`. A clean halt records the position reached in
the run log and exits with a status an operator can see, so tomorrow's run resumes rather
than restarts. Resumption rests on D-68's per-grain idempotence rather than on a
transaction, which is what D-68 chose for this phase by name.

*Done when:* the registry is unchanged and both conformance tests pass; a stage
implementing both interfaces resolves config per date inside a range; an undeclared table
still throws before a connection opens; a seeded allowance below the next unit's weight
halts the sweep with its position recorded and no rows lost; a `402` or a `429` reaching
the paging client fails the stage rather than being recorded as a D-71 shortfall, asserted
as a test because those two are one absorbed observation apart.

### Stage B — Ingest over history, ticker-partitioned

**3.5 C05's rotation, closed.** Blocked on D-95. First in this stage, because the flow
sweep at 3.9 cannot complete a universe pass against a frozen head.

**3.6 C02 over `eod/{t}`, whole history.** Bounded `Parallel.ForEachAsync`, each worker
writing through its own Npgsql binary COPY stream [§19], row order into the stream
explicitly sorted. The pool is every admitted common stock, live and delisted.

*Halts on:* 3.4's allowance gate, per ticker. About 30,000 units, so it fits a day with the
reserve held back, and the gate is what makes that a measurement rather than a hope.

*Done when:* a spot-checked ticker has bars across the whole window; a name delisted inside
the window has bars to its last session and none after; `SPY.US` is present, which C08's
benchmark reads from `price_daily`; re-loading one ticker writes identical rows; a run
halted by the gate resumes from its recorded position and reaches the same store as an
uninterrupted one.

**3.7 C03, one full pool sweep.** Rotation cap lifted for the sweep. Closes two obligations
at once: `fcf_yield` at 464 of 5,713 rows and `capital_expenditures` running contiguously
from `A.US` to `CCBG.US` [`0006 → 3`], and C03 not reading `events` so earnings never jump
the queue [`1 → 3`, D-74]. `events` has been built since 1.8 and the read conformance test
carries this as its one recorded deviation, which fails the moment it stops being one.

**The pool is the live candidate pool plus every delisted common stock with a bar
inside the window, and the second half is not optional.** C03's pool is derived from
current price, liquidity and history, so it contains no name that has since delisted. A
name liquid in 2021 and delisted in 2023 would carry prices from 3.6 and no fundamental
rows at all, compute zero clean gaps, fail D-62's four-gap floor, and be absent from
`security_daily` on every historical date. The reconstructed universe would then be
exactly the survivorship-biased set §2's price-pool paragraph exists to prevent, one
table over: survivorship-clean on price and not on membership.

3.6 runs first and is what makes that set computable, so the ordering already holds and
no new information is needed. The count is D in §2's table, priced there at both ends
and recorded in `PROGRESS.md` from the run rather than estimated here.

**The sector call moves here and C01 loses it.** `fundamentals/{t}` unfiltered costs the
same as filtered and carries `General::Sector` [`PROGRESS.md`, endpoint weights], so C01's
per-member sector call is buying at 10 units what C03 can carry for nothing. That is what
makes a per-date C01 affordable at all.

*Halts on:* 3.4's allowance gate, per ticker at 10 units. 48,000 for the live pool and
ten more per in-window delisted name, so this one meets the gate on any day it shares
with another sweep and may meet it alone.

*Done when:* every pool member has a `fundamental_fetch_attempt` row; `fcf_yield` coverage
is recorded before and after; C03 declares and reads `events` and the recorded deviation is
removed rather than re-recorded; a gated halt leaves the rotation able to resume, which is
what the attempt record was built for.

**3.8 C04 sentiment at window width.** One pass, `from` at the window start. Contingent on
3.1's answer about the flat weight. Closes 453 of 2,841 names carrying the three derived
metrics.

*Halts on:* 3.4's allowance gate, per batch. About 14,200 units if the flat weight holds
over a wide range, and materially more if it does not, which is why 3.1 asks.

*Done when:* the median universe member has at least `sentiment.min_baseline_days` days
with a row inside a 90-day baseline at a spot-checked mid-window date, which is what C35
needs to compute anything at all.

**3.9 C05 flow, one universe sweep over form4.** About 258,000 units across three days.
D-71's short-page classification recorded per run and per affected ticker.

**Form 4 alone, and the holders half is not a deferral** [D-98]. C05 no longer fetches
`Holders::Institutions`: C03 writes `institutional_holding` off the unfiltered
`fundamentals/{t}` payload it was already paying for, so there is nothing here to
sweep. Nor would there be if it had stayed: the block has no series behind it [D-69],
so a universe pass would buy one current snapshot 2,841 times for 28,410 units where
C03's nightly rotation covers the pool in about ten days at no additional units.
`institutional_holding` accumulates forward only and that is recorded rather than
worked around.

*Halts on:* 3.4's allowance gate, per page at 10 units. **This is the sweep the gate exists
for.** Three days is what the arithmetic gives, so it halts twice in the ordinary course
and a halt has to read as the mechanism working rather than as a failure.

*Done when:* `meta.total` is matched per ticker or the shortfall is recorded with its
position in the history; the S4 open-market purchase base rate is answered as a query
against `insider_transaction` rather than left as the phase P reading of zero on seven
names; a gated halt mid-ticker and a D-71 short page appear as two different things in the
run log, asserted rather than assumed.

**3.10 C06 splits and dividends over history. Earnings deliberately not.** `splits/{t}` and
`div/{t}` are dated facts at 1 unit for whole history.

**Earnings are not backfilled, and the reason is a decision someone else owns.**
`calendar/earnings` sends no `announced_date`, so a backfilled row and a live-accumulated
one are indistinguishable and nothing records when a schedule became public [carried
obligation `1 → 5`]. Loading them now puts a lookahead of unknown size under C12's earnings
blackout and C15's `days_to_next_earnings` across the whole window, and phase 5 could no
longer separate it from the live rows. Recorded, with the observation that phase 5's
question is sharper for the backfill existing rather than answered by it.

*Halts on:* 3.4's allowance gate, per ticker at 1 unit each for two calls. About 5,700
units, the cheapest sweep in the phase.

*Done when:* a spot-checked split inside the window is present with its ex-date; a dividend
carries its ex-date and its declaration date and discards the other two [1.8's fixture];
`events` holds no earnings row dated before the live window, asserted, because the absence
is the decision rather than an omission.

### Stage C — The universe over history

**3.11 C01 per evaluation date into `security_daily`.** Blocked on D-92. C01 already
computes everything as-of: `LiquidAsync` filters `date <= asOf` and `FundamentalsAsync`
filters `filing_date_effective <= asOf`. What changes is where the row lands, that it runs
per weekly evaluation date across the window, and that sector comes from
`fundamental_snapshot` rather than from a call per member.

*Done when:* a spot-checked 2022 date has a membership that differs from today's; a name
delisted in 2023 is active before its `delisted_date` and absent after; a name that crossed
$10B mid-window carries `mid` before and `large` after; the member count across the window
is recorded against the 2,840 measured live.

**3.12 Every reader of the universe moves to `security_daily`.** C11's cell join, C08's
sector, C10's breadth members, C35's iteration set, C04's. Five components have `security`
in their §3 Reads cell and each has to say which of the two it means, which is authored and
is drafted with D-92 rather than taken here.

*Done when:* `ReadDeclarationConformanceTests` passes against the amended catalogue; a test
asserts C11 ranks a backfilled date against that date's membership and not today's.

### Stage D — Compute over history

**3.13 C08 and C09 in range mode.** One read of a ticker's whole series, then the existing
public `IndicatorEngine.Compute` and `ValuationEngine.Compute` called per date, one COPY
per ticker. **The arithmetic is not reimplemented**, which is what keeps the 2.5 to 2.7
reference fixtures covering the backfill rather than half of it.

*Done when:* range-mode output for one date equals the nightly stage's output for that date
byte for byte, asserted as a test; the two null-count report lines agree;
`ev_ebit_vs_own_5y` and `dist_52w_high_20d_change` compute for real names, which is the
phase 2 obligation that said they resolve here.

**3.14 C10, C34 and C35 over a range.** Each is one set-based statement per date already.
C34's 90-day insider window and C35's 90-day baseline become range joins, which may or may
not beat a looped run against the new index. **Measured over one month and the faster
taken, with which one recorded**, because a guess here is the kind that looks right in a
comment and is wrong in production.

**3.15 C11 over a range.** `PARTITION BY date, size_bucket, sector`. This is the checkpoint
that decides whether the phase meets its timing line: an `UPDATE` scanning a 1.2 GB table
once per date across 1,260 dates is the cost, and it is the cost `SCREEN_LIFECYCLE.md` §9.3
predicts in a different table.

*Done when:* percentiles for a backfilled date reproduce what the nightly stage produces
for the same date; the fifteen-member fallback fires and its counts are reported per metric.

**3.16 The driver.** `Worker backfill [from] [to]`, sources in order, resumable from where
an interruption left it, `NightlyRun`'s halt semantics preserved. Resume rests on D-68's
per-grain idempotence rather than on a transaction, which D-68 chose for exactly this phase.

### Stage E — Proof and record

**3.17 Replay, determinism and timing.** One historical date through `run-night`, asserted
byte-identical against what the backfill wrote. Full rebuild wall clock measured.

**3.18 The record.** `PROGRESS.md`, `FIXTURES.md`, `CONFIG_REFERENCE.md`.

---

## 5. Definition of done

`BUILD_PLAN.md`'s four lines, each as something that runs.

1. **Five years present.** Row counts per table against the window, and a spot-checked
   ticker with bars on every session the exchange calendar reports.
2. **A spot check confirms delisted names are included.** A named ticker that delisted
   inside the window, with bars to its last session, a `delisted_date` on `security`, and
   `security_daily` rows showing it active before and absent after. Contingent on 3.1.
3. **A full rebuild finishes in minutes rather than hours.** Measured and recorded. See §6,
   first finding.
4. **A replay of one historical date produces byte-identical output, over a fixed store.**
   The line carries its reading rather than leaving it in findings, because it is
   conditionally unsatisfiable on the other reading and a later session testing that one
   will find it fails without knowing whether that was expected.

   **What is asserted.** Re-running any compute stage over a date, against a store whose
   ingest has not moved, reproduces what the backfill wrote byte for byte. Tested at 3.13
   as range mode against nightly for one date, and run end to end at 3.17 through
   `run-night`. It is the property purity buys and the one every stage was built to have.

   **What is not claimed.** Byte identity does not survive a re-ingest of fundamentals.
   `filing_date_effective` is computed at ingest from the ticker's widest clean gap known at
   that moment, and a widest gap only grows, so a later re-ingest widens the substitution
   window and moves which rows are readable on a past date. D-62 states this as an accepted
   limitation and gives the reason it is accepted: the movement is toward readable later
   rather than earlier, which is the conservative direction and the one that decision exists
   to protect.

   Amending the line in `BUILD_PLAN.md` to carry both halves is authored work and is in §9's
   list rather than taken here.

Plus what the carried obligations require: `ev_ebit_vs_own_5y` and
`dist_52w_high_20d_change` computing for real names; sentiment clearing its baseline floor
for the median member; `fcf_yield` coverage recorded after the rotation has cycled; C05's
rotation cycling; C03 reading `events`.

**Invariants at risk:** 11, 12 and 13, as `BUILD_PLAN.md` states, and 1 and 10 are touched.
INVARIANT 1 because the price pool is a filter decision and §2 states which criterion may
be applied early and why. INVARIANT 10 because D-92 adds a table and D-95 adds another, and
each needs its writer declared in `SCHEMA.md` in the same checkpoint as its migration.

---

## 6. Findings to report, not to resolve

**"Minutes rather than hours" is the line most likely not to survive contact.** Phase 2
measured 14.4 seconds for the compute layer over one date. Times roughly 1,260 trading
dates, that is about five hours before anything else runs. Range mode and the date indexes
are what could bring C08 and C09 toward minutes; C11's per-date update is what will decide
it. This phase builds to the line and measures against it. If it lands in hours, that is a
measurement to record and a bound to ask again with numbers behind it, not a line to edit
[`CLAUDE.md` §11, §13].

**§16's 400 MB for `price_daily` is a five-year-of-universe estimate and three populations
are now in play.** The live table takes the whole 50,000-row bulk feed nightly with no
universe filter, the backfill loads per-ticker over roughly 30,000 admitted names, and the
depth is whatever the provider returns. Phase 3 measures it against the ~5 GB estimate for
the whole store.

**C09 writing 3,897 rows against a universe of 2,841 becomes 1,260 times that.** The
`2 → 3` obligation says closing it needs an authored amendment to §3's Reads cell, and
`security_daily` existing makes that amendment cheap. It is still authored, and it is
reported here rather than taken.

**`market_context_daily.vix` and `last_two_earnings_surprises` do not close here** unless
3.1 finds a source. Both are written null explicitly so a value cannot survive from a
previous run [D-80, `METRICS.md` §3].

**Sector is not point-in-time and cannot be made so from this provider.** Stated in D-92.
How many names a reclassification inside the window would affect is not answerable from
this source, so what is recorded is the limitation rather than a figure.

---

## 7. Carried obligations, and where each lands

| From | Item | Lands |
|---|---|---|
| 1 → 3 | `security`'s single `size_bucket` and `market_cap` | D-92, 3.2, 3.11, 3.12 |
| 1 → 3 | C03 does not read `events`, so earnings never jump the queue | 3.7 |
| 2 → 3 | `last_two_earnings_surprises` has no input | 3.1, then reported |
| 2 → 3 | `market_context_daily.vix` has no path | reported, unchanged [D-80] |
| 2 → 3 | `ev_ebit_vs_own_5y` and `dist_52w_high_20d_change` compute for no name | 3.13 |
| 2 → 3 | The fixed rotation head, `capital_expenditures` and `fcf_yield` | 3.7 |
| 2 → 3 | 453 of 2,841 carry the three derived sentiment metrics | 3.8 |
| 2 → 3 | C09 writes wider than the universe | reported, needs an authored amendment |
| 0006 → 3 | `FlowIngestor.SelectionFor` carries C03's defect | D-95, 3.5 |
| 0006 → 3 | `fcf_yield` coverage after the rotation cycles is unmeasured | 3.7, 3.18 |
| P → 4 | The S4 open-market purchase base rate | answerable at 3.9, recorded |

---

## 8. What phase 3 does not do

**No screen scores.** `BUILD_PLAN.md` phase 4 carries "then backfill screen scores over the
five years using the two-pass approach", and `SCREEN_LIFECYCLE.md` §3 corrects its own
brief on exactly this point. The two-pass shape in §19 and `RUNBOOK.md` is phase 4's. What
phase 3 owes it is a complete store and a driver that can run a stage over a range.

**No screen registration**, no `screens.<id>.state`, no `attribution.surfaced_as`, no
`candidate_attribution` view. Those are the `SL → 4` obligations and they are phase 4's.

**No measurement added to its own scope** [`CLAUDE.md` §3]. Every figure in §5 and §6 is a
query against a table this phase populates or a wall clock on a job it runs.

---

## 9. Files

**Authored by a human, blocking:** `DECISIONS.md` D-92, D-93, D-94 and D-95;
`BUILD_PLAN.md` phase 3's checkpoint table, its fourth done-when line carrying both halves
of §5's reading, and the carried-obligations rows; the §3 Reads cells D-92 touches.

**New:** `src/StockResearcherLab.Data/Migrations/0007_security_daily_and_date_indexes.sql`;
`src/StockResearcherLab.Core/Stages/IBackfillStage.cs`;
`src/StockResearcherLab.Pipeline/BackfillRun.cs`;
`src/StockResearcherLab.Pipeline/Ingest/RotationSelection.cs`;
`src/StockResearcherLab.Data/Eodhd/UnitAllowance.cs`, which reads `/api/user` and is the
only thing that decides a sweep has run out; `docs/evidence/phase-3/`.

**Changed:** the six ingest stages and the five compute stages, each gaining a range entry
point beside the one it has; `Worker/Program.cs` gaining `backfill`; `SCHEMA.md`,
`CONFIG_REFERENCE.md`, `CHANGELOG.md`, `FIXTURES.md`, `PROGRESS.md`.

---

## 10. Verification

**Per checkpoint:** `ci.ps1`, which runs guards, restore, build, the secrets check, migrate
twice and the test suite, and exits non-zero on the first failure.

**Per stage:** the equality assertions above, which are this phase's real tests. Range
against nightly at 3.13 and 3.15. Membership at a past date against membership today at
3.11. Rotation cycling at 3.5. The allowance gate against a D-71 shortfall at 3.4. Replay
end to end at 3.17.

**At sign-off:** the four done-when lines run and recorded in `PROGRESS.md`, then a review
in a session that did not build the phase, asking `BUILD_PLAN.md`'s three questions and
correcting nothing.
