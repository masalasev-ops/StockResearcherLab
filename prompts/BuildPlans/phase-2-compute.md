# Phase 2 — Compute

**Target** phase 2, the compute layer.
**Authored** 2026-08-10, at phase 1's sign-off, as `BUILD_PLAN.md` requires.
**Read against** `ARCHITECTURE.html` §01, §03, §04, §05, §07, §16 and §19 at HEAD
`34c403e`.
**Status** `DRAFT`. The numbered checkpoints below are phase scope and are authored
into `BUILD_PLAN.md` by a human, not from here [`CLAUDE.md` §13]. Five items in §3 are
authored content and block the checkpoints that name them.

---

## Context

Phase 1 signed off and the post phase 1 reconciliation merged at `34c403e`. Ingest
runs: `price_daily` takes the whole US bulk feed nightly, `security` builds to 2,840
names, `fundamental_snapshot` is keyed on `filing_date_effective`, `sentiment_daily`,
`insider_transaction`, `institutional_holding` and `events` land, and `flow_daily` is
already derived by C34 FlowEngine, which is a layer 2 component built early with
D-61's ingest.

What does not exist is every number the five screens rank on. `indicator_daily`,
`valuation_daily` and `market_context_daily` have their DDL from `0001_snapshot.sql`
and have never held a row. Nothing computes a percentile. That is phase 2: the layer
that turns ingested series into the metrics and the cell-relative ranks that phase 4
selects on.

`BUILD_PLAN.md` states the scope as "indicators, valuation, market context including
sector relative strength, and the percentile engine with size-and-sector cells and the
fifteen-member fallback", done when "a known ticker's indicators match a hand-computed
reference; a percentile spot-check confirms cell membership is correct and the fallback
fires where cells are thin", with invariants 10, 11 and 12 at risk.

Two things make this phase larger than that sentence reads. Four metrics the screens
rank on have no column anywhere, and three more have no component that computes them.
And `SCHEMA.md` forbids the write shape C11 needs. Both are documented below rather
than discovered mid-build.

---

## 1. The overview, from the architecture

Layer 2 holds five components. C34 is built. Four are not, and this plan adds a fifth.

| ID | Component | Runs | Reads | Writes | State |
|---|---|---|---|---|---|
| C08 | IndicatorEngine | 18:05 | `price_daily`, `security` | `indicator_daily` | to build |
| C09 | ValuationEngine | 18:05 | `price_daily`, `fundamental_snapshot` | `valuation_daily` | to build |
| C10 | MarketContextEngine | 18:05 | index and sector prices, `indicator_daily` | `market_context_daily` | to build |
| C34 | FlowEngine | 18:05 | `insider_transaction`, `institutional_holding` | `flow_daily` | built, 1.8 |
| C11 | PercentileEngine | 18:15 | all metric stores, `security` | `*_pctile` columns | to build |
| C35 | SentimentEngine | 18:05 | `sentiment_daily`, `security` | `sentiment_derived_daily` | proposed, §3 |

The shape of the layer is stated in §19 and it is the thing most easily got backwards.
**Indicators and valuation partition by ticker. Percentiles partition by date**, because
a percentile on a given day needs every name in the cell on that day, and §19 says so
in as many words: "Cannot partition by ticker." Getting this backwards produces wrong
numbers rather than slow ones [`CLAUDE.md` §5].

For a single nightly date that distinction is about how the statement is written rather
than about threads. C34 is the worked precedent and every engine here follows it: one
set-based statement over the whole date, window functions rather than a loop issuing a
query per ticker, an explicit `ORDER BY` before `ON CONFLICT` so write order is
deterministic, and a read-back pass folding null counts into `StageResult.Detail`
[`FlowEngine.cs:87-174`, `:181-219`]. The ticker partition key is what phase 3's
backfill parallelises on, not what phase 2 threads.

**Who consumes what**, which is what decides the column set:

- §05's Ranks-on column, five screens, nineteen named metrics.
- §07's fixed core, the dossier's ~350 tokens sent for every candidate, where "every
  metric arrives twice, as the raw value and as its percentile within its size and
  sector peer group".
- §11's risk layer, which takes `atr_pct` for the stop and `median_dollar_volume_20d`
  for the participation cap.
- §02's universe, which reads 20-day median dollar volume for D-4's liquidity floor.

---

## 2. Four decisions taken in planning

Each was a genuine fork and each changes what gets built.

**Percentiles are a per-operation split on the metric tables.** C08, C09, C34 and C35
own `Insert`; C11 owns `Update` of the declared `_pctile` column set on each. This is
INVARIANT 10 as amended, and the machinery already supports it: the attribution-split
fixture at `WriteOwnershipConformanceTests.cs:241` exists to prove exactly this case is
not a conflict. It needs the authored amendment in §3 below, because
`OnlyTheDeclaredSplitsHaveMoreThanOneWritingComponent` reads permitted splits off
`SCHEMA.md` and `SCHEMA.md` currently says there is no fourth split.

**The derived sentiment metrics are built in phase 2.** S3 ranks on nothing else and
S5's stabilisation gate reads two of the three, so without them phase 4 cannot test the
idea it exists to test. Recommended owner is a new component rather than a widened C08,
following C34's precedent exactly: ingested tables land at the source's grain and a
compute stage derives the consumption form. `indicator_daily` is declared as "all
computed locally from `price_daily`" and putting sentiment columns in it makes that
sentence false [`SCHEMA.md:206`].

**Benchmark and sector series are derived from what is already stored.** C02 writes
every row the bulk US feed returns with no universe filter [`PriceIngestor.cs:48`,
declared read set empty because the feed is the source], so `price_daily` holds around
50,000 tickers a day, SPY among them. The benchmark for `rs_change_21d` and
`rs_change_63d` is SPY read from `price_daily`. The sector series for
`rs_change_vs_sector` and for §16's `sector_relative_strength` is an equal-weighted
composite of that sector's own universe members. Breadth is the fraction of the universe
above its own 200-day average. **VIX has no path in this feed and stays null**, recorded
as a finding rather than proxied.

**The sector composite is correct for a stronger reason than convenience, and the reason
has to be recorded.** Needing no ETF and no mapping is true and is not why. The sector
SPDRs are S&P 500 sector slices, and this universe is 2,840 names mostly outside that
index. Measuring a $500M industrial against XLI measures it against Honeywell and
Caterpillar, which imports a large-cap benchmark into a small-cap universe and tilts the
trend screen by regime. That is the megacap drift §20 says this design exists to prevent,
arriving through a column nobody would look at. Stated as convenience, a later session
reading "needs no mapping" may reasonably conclude that adding the mapping is an
improvement.

**The column set is every column with a named consumer, and nothing else.**
`ARCHITECTURE.html` §04 says "~40 technical columns per name" and `SCHEMA.md:194` says
"roughly forty technical columns plus their percentiles", then names ten. Tracing the
readers gives fifteen indicator columns, four of which do not exist. Both figures are
design estimates, `PROGRESS.md` has a table for the difference, and `CLAUDE.md` §6 says
build what is needed now. Phase 2 records the count it actually built against the
estimate rather than padding to it.

---

## 3. Blocked on authored input, before any code

Five items. Each is authored content under `CLAUDE.md` §13 and none is the build
session's to write. Clauses are in the amendment format of `CLAUDE.md` §15 so they paste
into a build prompt. All five are blockers on the checkpoint that needs them, and the
plan says which.

### 3.1 The percentile write ownership, and `SCHEMA.md`'s fourth split

`SCHEMA.md:10-12` says a named `Writer: X` owns every operation on that table.
`SCHEMA.md:14-20` says "Three splits over five tables, and no fourth". `SCHEMA.md:225`
says percentile columns are "written alongside their source tables by
**PercentileEngine**". Those cannot all hold once C11 exists.

This is a contradiction between authored prose and an invariant, and `CLAUDE.md` §3 says
follow the invariant. INVARIANT 10 as amended is per operation and permits the split;
it is the enumeration that is wrong, and `SCHEMA.md:19` already concedes the shape of
that failure: "a rule that counts exceptions gets longer every time the design is
correct." So the build can proceed on the per-operation reading and report. What it
cannot do is make CI green, because the conformance test derives permitted splits from
the document. Blocks 2.3.

```
D-77 Percentile columns are a per-operation split on the metric tables. ACTIVE
The metric engine inserts the row; PercentileEngine updates the *_pctile
columns of it. Ownership is per operation and the registry declares the
column set, so a split is stated rather than counted, and SCHEMA.md's
"three splits over five tables, and no fourth" is replaced by the rule
that a table's writers are whoever its heading names. The enumeration
failed for the reason SCHEMA.md already gives for the last one.

This is the first place two stages share a table's rows rather than a
table, and its characteristic failure is not a conflict the registry
catches. It is a metric engine re-running and blanking the percentiles.
D-68's idempotence is guaranteed per stage and says nothing about the
columns a stage does not write, so the property needs a test rather than
a declaration.

DoD: SCHEMA.md's indicator_daily, valuation_daily, flow_daily and
sentiment_derived_daily headings each name two writers with the operation
each owns; the "no fourth" paragraph is replaced under D-73's clean-edit
rule with the prior wording in CHANGELOG.md;
OnlyTheDeclaredSplitsHaveMoreThanOneWritingComponent passes with the four
new splits found by the parse rather than by a literal; and a test at 2.10
asserts re-running a metric engine after PercentileEngine leaves every
_pctile value unchanged.
```

### 3.2 C35 SentimentEngine, or a widened C08

**CLOSED.** D-78 authored at 2.2, `SCHEMA.md` naming it as a writer at 2.3, and the §3
catalogue row plus the §1 layer-2 entry at 2.14. `CataloguedComponents` moved 34 to 35.
The amendment as issued said 35 to 36; the constant read 34 and §3 carried exactly 34
rows, both checked before the edit, and the figure is an observation rather than a
decision.

`article_count_z_own_90d`, `sentiment_delta_7v30` and `sentiment_7d_level` are named in
§05 as S3's entire ranking basis, in §05's gate table for S5, and in §07's fixed core.
`sentiment_daily` holds `ticker`, `date`, `article_count`, `sentiment_score` and nothing
else. No §3 catalogue row gives any component a path from one to the other. D-12 already
decides the rule, that the count is z-scored against the ticker's own history and never
cross-sectionally; what is missing is a component and a store. Blocks 2.2 and 2.8.

```
D-78 The derived sentiment metrics are computed by a compute-layer stage
from sentiment_daily. ACTIVE
sentiment_daily is what the provider sends. The three forms the screens
rank on are derived from it, exactly as flow_daily is derived from the two
flow source tables and indicator_daily from price_daily [D-61]. C35
SentimentEngine reads sentiment_daily and security and writes
sentiment_derived_daily at ticker by day. Windows named in a column stay
constants rather than keys, as FlowEngine's 90 does.

DoD: ARCHITECTURE.html §3 carries a C35 row with those Reads and Writes;
§01's layer 2 list names it; SCHEMA.md declares sentiment_derived_daily
with a Grain line; RegistryNameTests passes with CataloguedComponents 35.
```

### 3.3 Two Reads cells that name their sources in prose

`ReadTablesByComponent` keeps only `<code>` spans intersected with the schema's table
list [`ArchitectureDocument.cs:71-109`]. C10's cell reads "Index and sector prices,
`indicator_daily`" and C11's reads "All metric stores, `security`", so both stages will
fail `EveryTableAStageDeclaresIsNamedInItsReadsCell` for every table they really read.
The precedent for the fix is recorded at `ArchitectureDocument.cs:66-69`: "the fix is
the markup rather than the assertion." `ARCHITECTURE.html` is human-edited only.
Blocks 2.11.

```
C10's Reads cell becomes: price_daily for the index and the sector
composites, security, indicator_daily. C11's becomes the four metric
stores named individually plus security. Both in <code> tags, since a
conformance test reads them.
DoD: EveryTableAStageDeclaresIsNamedInItsReadsCell and
EveryTableAReadsCellNamesIsDeclaredByItsStage both pass for C10 and C11
with no entry added to RecordedDeviations.
```

### 3.4 The regime label has no enumerated values

`market_context_daily.regime_label` is written by C10, carried in the cached prefix
[§07], stored on every attribution row [`SCHEMA.md:268`], displayed on screen U1, and
named as what makes S1's and S5's size tilt "regime dependent" [§05]. No document
enumerates the labels or the rule that assigns one. Blocks 2.9.

A proposal, offered for authoring rather than taken: three labels from two facts already
computed, breadth and the index's own distance from its 200-day average. `risk-on` when
both are above their thresholds, `risk-off` when both are below, `mixed` otherwise, with
the two thresholds as config keys. The virtue is that it reuses numbers C10 computes
anyway and adds no input.

### 3.5a `fcf_yield` had no capital expenditure figure

**CLOSED at 2.2 by D-79.** Raised here as a finding, authored as a decision, and taken in
`0004` with C03's parse widened. `fcf_yield` is TTM cash from operating less TTM capital
expenditure rather than plus total investing cash flow.

One thing it leaves open, which 2.7 closes: the sign convention. Nothing in this
repository shows the field, the phase P transcripts never printed it, and providers
differ on whether capital expenditure is a negative outflow or a positive magnitude.
Getting it backwards doubles free cash flow rather than halving it and nothing errors, so
2.7 confirms it against real rows before the formula is fixed.

### 3.5 `last_two_earnings_surprises` has no input

The column is `real[]` in `valuation_daily`, is bolded in §07's fixed core, and is one
of the five fields §07 calls out as able to flip a verdict.
`0002_statement_fields_and_grains.sql` adds no EPS actual and no estimate to
`fundamental_snapshot`, and `events` carries only `event_type`, `event_date` and
`announced_date`. Nothing says where a surprise comes from.

This is ingest work landing in a compute phase and `CLAUDE.md` §3 says a build session
does not add to its own scope. **Recommendation: the column stays null through phase 2**,
recorded as a finding and a carried obligation owed to whichever phase reopens ingest,
which is phase 3. Does not block anything.

---

## 4. The column set, stated once

Everything below is what gets built. Types follow `SCHEMA.md`'s existing rule: 32-bit
`real` for technicals and ratios, `numeric` for money, and every new `real` column needs
a row in `SCHEMA.md`'s "Columns that are not money" table or `guards.ps1` check 4 and
`SchemaParityTests` both go red.

### `indicator_daily` — C08, fifteen columns

Eleven exist. Four are new and every one of them has a named reader that cannot work
without it.

| Column | Type | Reader |
|---|---|---|
| `atr_pct` | real | §07 core, RiskGate stop [§11] |
| `adx14` | real | S2 rank, §07 core |
| `dist_200dma` | real | S2 rank, S5 rank, §07 core |
| `dist_52w_high` | real | §07 core |
| `rs_change_21d` | real | §07 core |
| `rs_change_63d` | real | §07 core |
| `rs_change_vs_sector` | real | §07 core, one of §07's five verdict-flipping fields |
| `volume_vs_50d_avg` | real | §07 core, one of the five |
| `ma50_200_slope` | real | S2 rank |
| `base_breakout_flag` | boolean | S2 rank |
| `median_dollar_volume_20d` | numeric | D-4 universe floor, participation cap, §07 core |
| `rs_21d_63d_change` | real | **new.** S2 rank, named in §05 with no column |
| `dist_52w_high_20d_change` | real | **new.** S2 rank, same |
| `rs_20d_slope` | real | **new.** S5 stabilisation gate |
| `dist_20dma` | real | **new.** S5 gate's `close > 20dma`, as a signed fraction |

`dist_20dma` rather than a stored 20-day average, so the gate reads `dist_20dma > 0`
and the table stores no price. It matches `dist_200dma`'s existing shape and it keeps a
money value out of a `real` column, which INVARIANT 16 would otherwise have something to
say about.

### `valuation_daily` — C09, thirteen columns, twelve computed

All thirteen exist in DDL. Twelve are computed. `last_two_earnings_surprises` stays null
per §3.5. `ev_ebit_vs_own_5y` needs five years of the ticker's own history and computes
null below a configured minimum, which on a cold database is every name until phase 3
backfills.

Every fundamental input is read on `filing_date_effective <= date` and never on
`period_end` [INVARIANT 12, D-46, D-62]. This is the invariant this phase is most likely
to break, because `period_end` is the natural-looking key and reading it hands you
quarterly numbers weeks before they were public.

### `sentiment_derived_daily` — C35, new table, five columns of which three are derived

`ticker`, `date`, `article_count_z_own_90d`, `sentiment_delta_7v30`,
`sentiment_7d_level`, primary key `(ticker, date)`. Three derived metrics on a
ticker-by-day grain. The three windows are named in the columns, so they are constants
and not config keys, following `FlowEngine.WindowDays` [`FlowEngine.cs:33`].

### `market_context_daily` — C10, four columns, three computed

`breadth` as the fraction of the universe above its own 200-day average.
`sector_relative_strength` as a jsonb object of sector to trailing relative return
against the universe composite. `regime_label` per §3.4. `vix` stays null.

### Percentile columns — C11, thirty

One `_pctile` per ranked metric, `real`, added to the source table.

| Table | Percentiled | Count |
|---|---|---|
| `indicator_daily` | every column above except `base_breakout_flag` | 14 |
| `valuation_daily` | the ten ratio columns, not `cash_on_hand`, `quarterly_burn_rate` or the array | 10 |
| `flow_daily` | all three | 3 |
| `sentiment_derived_daily` | all three | 3 |

**`median_dollar_volume_20d` is percentiled, and the first draft of this plan excluded it
without a reason.** §07 says every metric in the fixed core arrives twice, as the raw
value and as its percentile, and the core's identity group names it. A field the document
promises and the schema does not hold is found at phase 6 rather than here. A liquidity
percentile inside a size and sector cell is also meaningful on its own terms, since it
separates a thinly traded name from a heavily traded one among its actual peers rather
than against megacaps. So the count is thirty and `median_dollar_volume_20d_pctile`
exists.

Two traps worth naming before they are hit.

**Two percentile names match the monetary pattern**, `insider_net_90d_usd_pctile` through
`_usd` and `median_dollar_volume_20d_pctile` through `dollar`, so `guards.ps1` check 4
will demand both be `numeric` [`guards.ps1:109`, matched against the column name]. A
percentile of a money column is not money. Both are declared in `SCHEMA.md`'s non-money
table, which is the exception route the guard already provides. Neither moves
`ExpectedMonetary`, which counts monetary columns rather than matching names.

**`base_breakout_flag` is boolean and S2 ranks on it.** A percentile over a two-valued
column collapses to two values and carries no information the flag does not. It is the
one column with a named reader that is not percentiled, and how a boolean enters a screen
score is phase 4's to author.

---

## 5. The rules that have to be pinned before code

The phase's definition of done is a hand-computed reference. A reference needs a
definition, and several of these are the kind that break silently.

**Wilder smoothing.** ATR(14) and ADX(14) are recursive and not expressible as a plain
window aggregate. The definition must state the warm-up: how many trailing bars the
recursion starts from and how the first value is seeded. Without it two runs over
different amounts of history disagree and neither is wrong, which fails determinism
[`CLAUDE.md` §6] and makes the reference unwritable. Proposal: seed on the simple mean
of the first fourteen true ranges inside a fixed trailing window, window length as a
config key.

**Insufficient history is null, never a shorter window.** A name with 120 bars has no
`dist_200dma`. Null means unknown [`CLAUDE.md` §6]. Substituting a 120-day average is a
silent correctness bug of the kind that makes the quality screens look excellent in
backfill and ordinary live.

**The percentile function is `PERCENT_RANK`, scaled to 0 to 100.**
`WORKED_EXAMPLE.md:35-36` prints 84 and 12, so the scale is decided. `PERCENT_RANK` gives
tied values the same rank, which removes the tie-break entirely and with it the
determinism hazard that `ROW_NUMBER` would introduce.

**Null metrics are excluded from the cell population, and the fifteen-member test counts
per metric.** This is the one most worth stating. If the cell has twenty members and only
five carry a value for one metric, ranking that metric over five while calling the cell
twenty produces a percentile that looks like a percentile over twenty. D-10's fifteen is
a test on the population being ranked, so it is evaluated per metric over non-null
values.

**A null sector goes straight to the bucket-only fallback.** `security.sector` is
nullable and C01 writes null rather than a guess on an HTTP failure, with a comment
already anticipating this [`UniverseBuilder.cs:283-285`]. A null sector does not form a
cell of its own.

**A null `size_bucket` cannot arise, and that is stated rather than left out.** A
universe member cleared D-4's $300M market cap floor, so it has a market cap, so C01
assigned it a bucket from `universe.bucket_large_floor` and `universe.bucket_mid_floor`
[`UniverseBuilder.cs:50-51`]. The sector hole is real and the bucket hole is not, and
without saying so the next reader cannot tell which of the two was reasoned about. A test
asserts `size_bucket` is non-null for every active row, which is what turns the claim
into something that fails if C01 ever changes.

**Re-running a metric engine must not blank the percentiles it does not own.** This is
the split's characteristic failure and the registry does not catch it, because two writers
on the same rows with different operations is exactly the case INVARIANT 10 as amended
permits. It is safe today only by construction: the staged path builds its staging table
as `SELECT <written columns> ... WITH NO DATA` [`BulkUpsertSql.CreateStaging`,
`:32-33`], so the upsert's `SET` covers the written columns and leaves `_pctile` alone.
Nothing asserts it. D-68's idempotence guarantee is per stage, and this is the first place
in the codebase where two stages share a table's rows. The assertion is a test, in 2.10.

**When the size bucket alone also has fewer than fifteen non-null members, the percentile
is null.** Never ranked across buckets. Ranking a $1B name against megacaps is the exact
thing D-10 exists to prevent, and a second fallback that does it is worse than no value.

**The fallback is reported, not stored per row.** How many cells fell back, per metric,
goes into `StageResult.Detail` and the run log, following
`FlowEngine.UnknownCountsAsync` [`FlowEngine.cs:181-219`]. A per-row column cannot carry
it, because the fallback is per metric and a row spans metrics.

**`median_dollar_volume_20d` is computed in two places and both must agree.** C01 applies
D-4's liquidity floor weekly by computing it inline from `price_daily`
[`UniverseBuilder.LiquidAsync`, `:169-209`]; C08 writes the column daily. One SQL
definition, extracted to a shared constant, with a test asserting both produce the same
number for a seeded ticker.

---

## 6. Stages and checkpoints

Thirteen checkpoints in four stages, one commit each, `Phase 2 / 2.n - what it did`.
Run `ci.ps1` per checkpoint, which is the per-checkpoint verification since 1.12 and is
the only thing that cannot reach the test step off a stale binary.

### Stage A — Definitions, schema and config

Nothing computes yet. This stage exists so the engines have something to be checked
against.

**2.1 The metric definition reference.** Every column in §4 with its formula, its window,
its warm-up, its null rule and its source columns, in one place, in the form a hand
computation can be worked from. Includes the rules in §5. Authored content where it
states a rule, so what this checkpoint produces is the draft and the human authors it.
*Done when:* every column in §4 has a definition a second reader could compute from
without reading code.

**2.2 Migration `0004`.** Four new `indicator_daily` columns, thirty `_pctile` columns
across four tables, `sentiment_derived_daily` with five columns of which three are
derived. `IF NOT EXISTS` throughout, because `ci.ps1` step 10 asserts a second migrate
prints `nothing to apply`. Blocked on D-78 for the new table.
*Done when:* migrate runs clean from empty and again as a no-op; `SchemaParityTests`
finds the new table with exactly five columns; the `_pctile` count in the migration
matches §4's thirty rather than being counted twice and differently.

**2.3 `SCHEMA.md` declarations.** The four split-writer headings per D-77, the
`sentiment_derived_daily` section with its Grain line, and a non-money row for every new
`real` column including the two whose names match the monetary pattern,
`insider_net_90d_usd_pctile` and `median_dollar_volume_20d_pctile`. Blocked on D-77.
*Done when:* `guards.ps1` check 4 passes all three assertions and reports its swept file
count; `ExpectedMonetary` is unmoved and that is asserted rather than assumed;
`SchemaParityTests` passes in both directions.

**2.4 Config keys.** Seeded through `ConfigSeeder.Keys`, `Set by` naming the checkpoint,
and `CONFIG_REFERENCE.md`'s Consumer column filled from the composition code rather than
the key name. `percentile.cell_min_members` already exists at 15 and is `unverified`;
this is where it becomes verified. New keys are the Wilder warm-up window, the valuation
own-history floor, the breadth moving-average length and the regime thresholds. Windows
named inside a column stay constants.
*Done when:* `seed.ps1` is a no-op on a second run; every phase 2 key resolves for a
simulated date; no magic number at a call site.

### Stage B — Ticker-partitioned compute

**2.5 IndicatorEngine, the price-derived columns.** C08 over `price_daily` and
`security`: ATR, ADX, the moving-average distances and slopes, the 52-week high
distances, volume against its 50-day average, the base breakout flag, and
`median_dollar_volume_20d` sharing C01's SQL definition. One statement over the whole
date with `PARTITION BY ticker` window functions.
*Done when:* a seeded ticker's every column reproduces a reference hand-computed in the
test, so a change to the statement that alters a number fails rather than passing with a
different one; a ticker with too little history carries null and not a short window;
`median_dollar_volume_20d` agrees with C01 for the same ticker and date.

**2.6 IndicatorEngine, the benchmark and sector-relative columns.** `rs_change_21d`,
`rs_change_63d` and `rs_20d_slope` against SPY read from `price_daily`;
`rs_change_vs_sector` against the equal-weighted composite of that sector's universe
members; `rs_21d_63d_change` and `dist_52w_high_20d_change`.
*Done when:* a hand-computed reference reproduces; a ticker with a null sector carries
null for `rs_change_vs_sector` rather than a universe-wide comparison; a sector with one
member is handled and the rule is stated; the commit body or the definition reference
names the constituent mismatch as the reason the composite is built from universe members
rather than from the sector SPDRs, so that adding a mapping later reads as the regression
it would be.

**2.7 ValuationEngine.** C09 over `price_daily` and `fundamental_snapshot`, twelve
columns, every fundamental read filtered `filing_date_effective <= date`.
*Done when:* a hand-computed reference reproduces; a test asserts no fundamental value
reaches a valuation row before its effective filing date, with a fixture whose filing
date is later than the date being computed; `ev_ebit_vs_own_5y` is null below the
configured history floor rather than computed over less.

**2.8 SentimentEngine.** C35 over `sentiment_daily` and `security`. Blocked on D-78.
*Done when:* a hand-computed reference reproduces on a ticker with rows on only a handful
of days in the window, which is the ordinary small-cap case phase P measured at 4 to 34
days of 180; a ticker with fewer than the minimum days carries null; the z-score is
against the ticker's own baseline and a test asserts it is not cross-sectional [D-12].

### Stage C — Date-partitioned compute

**2.9 MarketContextEngine.** C10 over `price_daily`, `security` and `indicator_daily`.
Breadth, sector relative strength, regime label. `vix` written null. Runs after C08
because it reads `indicator_daily`. Blocked on §3.4 for the regime label.
*Done when:* one row lands per trading date; breadth reproduces a hand count over a
seeded universe; `sector_relative_strength` is a jsonb object with one entry per sector
present and its key ordering is explicit rather than dictionary order [`CLAUDE.md` §6].

**2.10 PercentileEngine.** C11 over the four metric stores and `security`, `Update` on
the declared `_pctile` column sets, thirty columns. A single statement per source table
with `PERCENT_RANK() OVER (PARTITION BY size_bucket, sector ORDER BY metric)` and the
fifteen-member fallback. Every rule in §5 applies here.
*Done when:* a spot check over a seeded universe confirms cell membership; the fallback
fires where a cell is thin and does not where it is not; a null metric carries a null
percentile and is absent from its cell's denominator; a bucket with fewer than fifteen
non-null members carries null rather than a cross-bucket rank; `size_bucket` is asserted
non-null for every active row; the fallback counts per metric appear in the run log.

**Also 2.10, and it is the checkpoint's real risk.** Re-running each metric engine over
the same date after C11 has written leaves every `_pctile` value byte-identical. One test
per split table or one parameterised over the four. 2.11's column-in-SQL test does not
reach this: it asserts a declared column appears in the statement, not that an undeclared
one is untouched.
*Done when:* re-running any metric engine after C11 leaves every `_pctile` value
byte-identical; the test fails when the staging table is widened to the full column set,
which is the change that would silently break it; 2.12's byte-identical night still
passes.

### Stage D — Wiring, conformance and proof

**2.11 Conformance.** `ExpectedOwners` 9 to 14 and `ExpectedStages` 8 to 13 with the
comment that already says "Phase 2 adds four" corrected to five; `CataloguedComponents`
34 to 35; the five new components named in both conformance tests. A test in the shape
of `FlowEngineTests.cs:186-197` asserting each engine's declared column list appears in
its own SQL, because `WriteAsync` checks the table and the operation but not the column
list and every phase 2 engine is on that route. Blocked on §3.2 and §3.3 for the
`ARCHITECTURE.html` markup.
*Done when:* both conformance tests pass with no entry added to `RecordedDeviations`; a
deliberately conflicting registry still fails; the column-in-SQL test fails when a
declared column is removed from a statement.

**2.12 The nightly run.** Five names appended to `NightlyRun.EveningOrder` in dependency
order, C10 after C08, C11 last at 18:15. `run-night` end to end against real data.
*Done when:* one night runs all twelve stages and every phase 2 table holds rows for the
blessed trading date; a stage writing zero rows halts everything after it; re-running the
night over the same date produces byte-identical output.

**2.13 Recording.** `PROGRESS.md` with what was built, the HEAD sha, the test count, the
actual indicator column count against the "roughly forty" estimate, the measured fallback
rate per metric, and every finding. `FIXTURES.md` with the new fixtures.
`CONFIG_REFERENCE.md`'s Consumer column verified against the composition code.
`BUILD_PLAN.md`'s carried obligations with what phase 2 hands forward.
*Done when:* every figure in `PROGRESS.md` traces to something that produced it.

---

## 7. Definition of done

The authored two lines from `BUILD_PLAN.md`, unchanged:

- A known ticker's indicators match a hand-computed reference.
- A percentile spot-check confirms cell membership is correct and the fallback fires
  where cells are thin.

Made runnable, the phase is done when: `ci.ps1` is green at HEAD; migrations run clean
from empty and again as a no-op; `run-night` completes with all twelve stages and every
phase 2 table populated for the blessed date; a second run over the same date is
byte-identical; a fundamental is unreadable before its effective filing date in a
valuation row; a thin cell falls back to size bucket alone and a thin bucket carries null;
a null metric carries a null percentile; **re-running any metric engine after
PercentileEngine leaves every `_pctile` value byte-identical**; both conformance tests
pass with the five new components and no new recorded deviation; and every phase 2 config
key resolves as of a simulated date.

**Invariants at risk.** `BUILD_PLAN.md` names 10, 11 and 12. Two more are live and this
is a finding rather than an edit to authored scope: **1**, because C11 ranks and a
percentile engine that skipped a slice of the universe would be an absolute filter
outside the universe definition, and **16**, because this phase adds the largest batch of
numeric columns the schema has seen and `median_dollar_volume_20d` and the `_usd`
percentile both sit on the boundary.

---

## 8. Verification

Per checkpoint, `ci.ps1` from a clean tree at the committed sha. It checks out HEAD into
a worktree, runs `guards.ps1` and parses its swept-file count, builds with warnings as
errors, drops and recreates the database, migrates from empty, migrates again expecting
nothing to apply, and runs the tests. Uncommitted work is not tested, so commit the
checkpoint and then run it.

End to end, after 2.12:

```
dotnet run --project src/StockResearcherLab.Worker -- migrate
dotnet run --project src/StockResearcherLab.Worker -- seed
dotnet run --project src/StockResearcherLab.Worker -- run-night
dotnet run --project src/StockResearcherLab.Worker -- run-night   # same date, twice
```

Then read back, rather than trusting the run summary: row counts per phase 2 table for
the blessed date, the null count per metric column, the fallback count per metric from
the run log, and a diff of the two runs' output for the byte-identical assertion.

Three queries answer questions the phase is supposed to answer and belong in
`PROGRESS.md` rather than in a test: how many cells fell back to size bucket alone and
for which metrics, how many names carry a null percentile because their bucket was thin
too, and how many of the fifteen indicator columns are null across the universe on a
settled date.

---

## 9. Findings and obligations carried forward

Filed rather than fixed, per `CLAUDE.md` §15.

| Item | Owed to |
|---|---|
| `security.size_bucket` and `market_cap` are one row per ticker, so a backfill of a 2021 date reads 2026's bucket for every name. C11 ranks within size bucket, so phase 2's cells are correct live and not as-of. Already carried from phase 1 | 3 |
| `last_two_earnings_surprises` has no input column and stays null. Closing it is an ingest change | 3 |
| `market_context_daily.vix` has no path in the bulk US feed and stays null | 3, or whichever phase reopens ingest |
| S1 ranks on `net_debt_ebitda_inv`, `accruals_inv` and `share_count_change_inv`. C11 stores one ascending percentile per metric and the direction belongs in `screens.<id>.metrics`. The `_inv` names in §05 have no column and are not meant to | 4 |
| `base_breakout_flag` is boolean and is not percentiled. How a boolean enters a screen score is unauthored | 4 |
| §07's screen-specific dossier blocks name fields with no column: breakout level and distance from base for S2, peak-to-trough decline and days since peak for S5. Phase 2 builds §05's rank metrics and §07's fixed core; the screen blocks are the dossier's | 6 |
| `FundamentalsIngestor` still does not read `events`, so earnings do not jump the rotation queue. Unchanged by this phase and already carried | 3 |
| `prompts/README.md` opens "Two different kinds of thing live here and they follow opposite rules" and this directory is a third. The README is authored prose stating a rule, so it is reported rather than edited | the next authored amendment to that file |

---

## 10. What phase 2 does not do

No screen scores, no floors, no gate, no candidate selection. Phase 4 owns those and
`CLAUDE.md` §11 forbids tuning screens on forward returns before the researcher has
judged anything.

No backfill. Phase 3 runs five years, and phase 2's job is that there is something
correct to run.

No measurement added to its own scope [`CLAUDE.md` §3]. The three queries in §8 are
reads against tables this phase populates, taken at sign-off, not a measuring task.
