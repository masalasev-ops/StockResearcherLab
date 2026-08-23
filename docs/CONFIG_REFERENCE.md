# CONFIG_REFERENCE.md

Every configuration key, its default, and the component that actually consumes it.

**The Consumer column means someone read the composition code and confirmed the
binding.** Not the assumed consumer, not the one the name implies. An unverified
entry is worse than an absent one, because it lets an audit conclude a value is wired
up when nothing reads it.

Config rows are append-only and versioned. Current is `MAX(version)` for a key. A
change inserts version + 1. Anything reading config for a simulated date resolves as
of that date, never as-now [INVARIANT 13].

**No magic numbers at call sites.** A value that could plausibly be tuned is a key
here, not a literal in a constructor [CLAUDE.md §8].

Verified column values: `unverified`, `verified <date>`, or `NOT BOUND`.

---

## Universe

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `universe.min_market_cap` | 300000000 | D-4 | UniverseBuilder | verified 2026-08-09 |
| `universe.min_price` | 5 | D-4 | UniverseBuilder, FundamentalsIngestor | verified 2026-08-09 |
| `universe.min_adv_20d` | 2000000 | D-4 | UniverseBuilder, FundamentalsIngestor | verified 2026-08-09 |
| `universe.min_history_days` | 250 | D-4 | UniverseBuilder, FundamentalsIngestor | verified 2026-08-09 |
| `universe.pool_statement_timeout_seconds` | 1800 | D-102 | `UniverseBuilder` C01 `LiquidAsync`, `FundamentalsIngestor` C03 `BootstrapPoolAsync` and `RangePoolAsync` | verified 2026-08-15 |
| `universe.bucket_large_floor` | 10000000000 | D-4 | UniverseBuilder | verified 2026-08-09 |
| `universe.bucket_mid_floor` | 2000000000 | D-4 | UniverseBuilder | verified 2026-08-09 |

## Fundamentals

**`fundamentals.widest_gap_alert_days` is in the cadence table below** rather than
here, with the other keys phase 1 wired up.


| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `fundamentals.min_clean_gaps_for_substitution` | 4 | D-62 | UniverseBuilder [L.2] | verified 2026-08-09 |
| `fundamentals.substitution_rate_alert` | 0.25 | D-57 | FundamentalsIngestor | verified 2026-08-09 |

The substitution window is each ticker's own widest clean gap observed before the
read date [D-62]. There is no universal constant left to configure: the per-ticker
value is derived from that ticker's filing history rather than set here, so it gets
no key. What is configurable is the floor beneath which no substitution is attempted
at all. Below
`fundamentals.min_clean_gaps_for_substitution` observed clean gaps the name leaves
the universe rather than being assigned a guess.

That exclusion is applied by UniverseBuilder alongside market cap, price and volume,
not by the fundamentals path [L.2, D-4]. It is an absolute filter, and INVARIANT 1
puts absolute filters in the universe definition and nowhere else, so a component
downstream applying this one would be narrowing the universe after ranking at ingest
had already decided what could be discovered. The count is computed for the date being
built rather than stored on any table, so the threshold is applied against the
ticker's filing history as it stood then rather than as it stands now [M.1].

The alert exists so that a provider change making equality universal is visible
rather than silently widening every read. It survives D-62 unchanged, and its
Set by column still names D-57 because that is the entry that set it and a
superseded entry keeps its number.

## Ingest cadence and cost

Every key here bounds how much of the universe one run touches, or how far a window
reaches. None of them changes a number the screens read: they decide what has been
looked at, not what it is worth.

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `fundamentals.max_tickers_per_run` | 500 | 1.4 | FundamentalsIngestor | verified 2026-08-08 |
| `sentiment.tickers_per_call` | 50 | 1.6 | SentimentIngestor | verified 2026-08-08 |
| `sentiment.lookback_days` | 30 | 1.6 | SentimentIngestor | verified 2026-08-08 |
| `flow.max_tickers_per_run` | 250 | 1.7 | FlowIngestor | verified 2026-08-08 |
| `flow.form4_page_size` | 50 | 1.7 | FlowIngestor | verified 2026-08-08 |
| `flow.institutional_report_lag_days` | 45 | 1.8 | FlowEngine | verified 2026-08-08 |
| `events.earnings_forward_days` | 90 | 1.8 | EventsIngestor | verified 2026-08-08 |
| `events.earnings_backward_days` | 7 | 1.8 | EventsIngestor | verified 2026-08-08 |

**The three per-run bounds exist because their endpoints are per ticker and metered
per call** [PROGRESS, endpoint weights]. Fundamentals is 10 units a ticker and form4
is 10 units a page, so a full universe pass on either is the most expensive thing in
a week. Each stage rotates, preferring tickers it has not fetched, so coverage builds
over several nights rather than a night spending its whole allowance on one stage.
`sentiment.tickers_per_call` is not one of these: sentiment is metered flat at 5
units per ticker whatever the batch size, measured at 1, 10 and 20, so that key is a
latency knob and the whole universe is covered every night [D-23].

**`flow.institutional_report_lag_days` is a point-in-time key, not a cadence one.**
`institutional_holding.report_date` is a period end and a 13F is due within
forty-five days of it, so reading on `report_date <= date` makes a quarter's
ownership readable up to forty-five days before it was filed. That is the mistake
INVARIANT 12 names for fundamentals, arriving through the other table with the same
shape. FlowEngine subtracts the key from the run date before either report date is
visible, so the substitution is inspectable and changeable rather than a literal
inside a statement. It is not a screen threshold and the tuner does not touch it
[INVARIANT 14].

**The two `events.*` windows cost nothing and are bounded for a different reason.**
`calendar/earnings` is metered at 1 unit whatever the range, measured over a 90 day
window returning 22,286 rows across every exchange the provider carries, so the width
is about what belongs in `events` rather than what it costs. Forward reaches far
enough for C12's earnings blackout to see the next report; backward reaches far
enough for C03's rotation to let a name that has just reported jump the queue.

**Three `universe.*` keys have two consumers each and the column says so** [1.8].
C01 applies `min_price`, `min_adv_20d` and `min_history_days` as three of D-4's six
absolute criteria, and C03 applies the same three to its candidate pool so a
per-ticker call is not spent on a name the universe will reject. That is one filter
read in two places rather than two filters: C03 narrows what it fetches and decides
no membership, and C01 applies all six again to everything it sees
[D-5, INVARIANT 1].

It matters for the audit this column exists for. Before 1.8 these read as C01's
alone, and a later session changing one of them would have looked at the Consumer
entry, seen a single weekly stage, and missed that it also changes what the nightly
fundamentals rotation spends its allowance on.

**Every key above and below was confirmed by reading the line that consumes it**,
not the key name: the eight in this table at `UniverseBuilder.cs:46-52` and
`FundamentalsIngestor.cs:59-64`, the four freshness keys at
`FreshnessGuard.cs:82-85`, `price.reload_window_days` at `PriceIngestor.cs:74`,
the two sentiment keys at `SentimentIngestor.cs:51-52`, the two flow keys at
`FlowIngestor.cs:59-60`, the lag at `FlowEngine.cs:44`, and the two events windows
at `EventsIngestor.cs:59-60`. Line numbers go stale; the file and the stage do not,
and both are given so the next reader can find it either way.

All ten backfill keys were confirmed the same way, at `PriceIngestor.cs:108-111` and
`FundamentalsIngestor.cs:88-94` for the range stages, and `Program.cs:175` for the
driver's read of `backfill.window_start`. The four sweep weights that read NOT BOUND
while their checkpoints were unbuilt were bound as each landed and are stamped in the
table below [corrected 2026-08-22; prior wording in `CHANGELOG.md`].

## Backfill

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `backfill.window_start` | "2021-01-04" | D-94, 3.3 | `FundamentalsIngestor` C03, `UniverseBuilder` C01 [3.11], `Worker` backfill driver | verified 2026-08-19 |
| `backfill.ticker_concurrency` | 8 | 3.3 | `PriceIngestor` C02 alone. C05 is serial by design and the other three chunk without it | verified 2026-08-19 |
| `backfill.daily_unit_allowance` | 100000 | 3.1, 3.3 | Every sweep that spends: `PriceIngestor` C02, `FundamentalsIngestor` C03, `SentimentIngestor` C04, `FlowIngestor` C05, `EventsIngestor` C06 [corrected 3.18; prior wording in `CHANGELOG.md`] | verified 2026-08-19 |
| `backfill.unit_reserve` | 50000 | 3.3 | Every sweep that spends: `PriceIngestor` C02, `FundamentalsIngestor` C03, `SentimentIngestor` C04, `FlowIngestor` C05, `EventsIngestor` C06 [corrected 3.18; prior wording in `CHANGELOG.md`] | verified 2026-08-19 |
| `backfill.weight_eod` | 1 | 3.1, 3.3 | `PriceIngestor` C02 | verified 2026-08-19 |
| `backfill.weight_fundamentals` | 10 | 3.1, 3.3 | `FundamentalsIngestor` C03 | verified 2026-08-19 |
| `backfill.weight_sentiments_per_ticker` | 5 | 3.1, 3.3 | `SentimentIngestor` C04, `ExecuteRangeAsync` | verified 2026-08-19 |
| `backfill.weight_form4_page` | 10 | 3.1, 3.3 | `FlowIngestor` C05, `ExecuteRangeAsync` | verified 2026-08-19 |
| `backfill.weight_splits` | 1 | 3.1, 3.3 | `EventsIngestor` C06, `ExecuteRangeAsync` | verified 2026-08-19 |
| `backfill.weight_dividends` | 1 | 3.1, 3.3 | `EventsIngestor` C06, `ExecuteRangeAsync` | verified 2026-08-19 |

**Every Consumer here read `NOT BOUND` after 3.4 and every one is now bound and
stamped** [corrected 2026-08-22; prior wording in `CHANGELOG.md`]. The keys are seeded
before anything resolves them, and 3.4 built the gate they are passed to rather than
the sweep that resolves them: `AllowanceRule.Decide` takes the
reserve, the weight and the configured allowance as arguments, and each sweep resolves
its own three for the date it is working on. So the column named the checkpoint that
would fill it rather than a component nothing had confirmed, and each was filled as
its sweep landed. An unverified entry is worse than an absent one, and a guessed one
is worse than both.

**The reserve is a parameter rather than resolved inside the gate, deliberately.**
Resolving it once inside `BackfillContext` would bind the key in one place, and it
would also mean resolving it as of something other than the date being computed, which
is what INVARIANT 13 rules out. The gate mechanism is stated once; the resolve is one
line per sweep and is not the duplication D-76, D-77 and D-83 removed.

**`backfill.window_start` is the only date-valued key in the store and the only one
that is not a number** [D-94]. `config_rows.value` is `jsonb`, so it is stored quoted
and read through `ConfigValue.Date`, which parses the JSON rather than the raw text. A
count of years would have needed no quoting and is what the decision rejects: resolved
against the run date it moves the window on every re-run, so two backfills over one
store would compute different date sets and the phase's fourth done-when line could not
be tested at all.

The value is the first session of 2021. It sits after `ConfigSeeder.SeedInstant`, which
is what makes every date in the range resolvable at all [D-72], and it opens the window
before the 2021 advance rather than inside it, because the window has to contain a real
drawdown and a drawdown needs the peak it fell from [`ARCHITECTURE.html` §05].

**The six weights are measurements and the gate never decides with them.** Each was
measured by bracketing one call between two `/api/user` reads, on 2026-08-09 and again
at 3.1, and every one came back the same. They project whether the next unit of work
fits; what decides is the reading itself, and a drift between the projection and the
reading is recorded rather than smoothed over. They are keys rather than literals
because a weight at a call site is the magic number §8 rules out, and because a
provider that re-prices makes every one of them wrong at once.

**`backfill.unit_reserve` is what a sweep may not eat into**, so a backfill day still
leaves the nightly run an allowance. A measured night is 45,518 units and the rest is
headroom for C04's universe-sized pass moving with the universe. C01's weekly rebuild
at 28,401 is deliberately not in it: 3.7 moves the sector call to C03 and takes that
number to about one. Until 3.7 lands, a sweep sharing a Sunday with a C01 rebuild has
less margin than this number says, which is recorded rather than padded, since padding
it would shrink every ordinary day's sweep for a case that stops existing.

**`backfill.ticker_concurrency` is not a provider bound.** `EodhdRateLimiter` already
holds the 1,000-requests-a-minute limit and a sweep of 50,785 names at one unit each is
nowhere near it. This bounds how many Npgsql binary COPY streams and sockets are open
at once, which is the resource that runs out first.

**Neither the allowance nor the reserve is tunable by the system** [INVARIANT 14]. They
are operator configuration in the same sense as the risk caps: the tuner moves screen
slots and touches nothing else.

## Record inspector

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `inspector.recent_bars` | 20 | 3.5.3 | `RecordInspector` C36 `InputsAsync` | verified 2026-08-23 |

**It is a display bound rather than a metric window, and it is the only bound on this
panel that no component already owns.** The sentiment window is
`sentiment.lookback_days` and the insider window is `FlowEngine.WindowDays`, which is a
constant precisely because `insider_net_90d_usd` carries the number in its own name.
Bars have neither, so the alternative here is a literal at a call site, which is what
`CLAUDE.md` §8 rules out.

Twenty, being the liquidity window D-4's dollar volume criterion is computed over, so
the panel shows the bars the membership panel's verdict rests on rather than an
arbitrary depth.

**Resolved as of the date being viewed**, like every key this reader reads. A viewer
resolving as of now would draw today's floors beside a 2022 verdict, which is correct
looking and wrong [D-43, INVARIANT 13].

### Keys this reader consumes without owning

`RecordInspector` reads these for display and none of them was added for it. The
Consumer columns in the sections above name it alongside their owners.

| Key | Read for | Where |
|---|---|---|
| `universe.min_price` | the membership panel's criteria table | `RecordInspector.CriterionKeys` |
| `universe.min_adv_20d` | the same | the same |
| `universe.min_history_days` | the same | the same |
| `universe.min_market_cap` | the same | the same |
| `universe.bucket_large_floor` | the same | the same |
| `universe.bucket_mid_floor` | the same | the same |
| `fundamentals.min_clean_gaps_for_substitution` | the same | the same |
| `sentiment.lookback_days` | the inputs panel's sentiment window | `RecordInspector.InputsAsync` |

**A reader falls back where a stage fails.** A stage resolving nothing is a run that must
not continue [D-72]; a viewer resolving nothing is looking at a date before the key
existed, which is a fact about that date rather than a fault, so the criteria table shows
the value absent and the two windowed panels take their stated default.

---

## Percentiles

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `percentile.cell_min_members` | 15 | D-10 | PercentileEngine | verified 2026-08-11 |

**It counts the non-null population for the metric being ranked, not the cell** [2.1].
A cell of twenty members where five carry a value for one metric otherwise produces a
percentile computed over five that is indistinguishable from one computed over twenty,
and nothing downstream could tell. So one row can be ranked inside its sector cell for a
metric with broad coverage and inside its size bucket for a metric with thin coverage,
on the same night.

It had been in this document since the corpus was written with nothing seeding it, which
`seed.ps1` closed at 2.4.

## Compute

Every key here is a window, a warm-up or a floor beneath which a metric is null rather
than computed over less. None of them is a screen threshold and the tuner touches none
of them [INVARIANT 14]. Formulas and null rules are in `METRICS.md`.

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `indicator.wilder_warmup_bars` | 250 | 2.1 | IndicatorEngine | verified 2026-08-11 |
| `indicator.base_lookback_days` | 60 | 2.1 | IndicatorEngine | verified 2026-08-11 |
| `indicator.base_max_range_pct` | 0.25 | 2.1 | IndicatorEngine | verified 2026-08-11 |
| `valuation.own_history_min_points` | 24 | 2.1 | ValuationEngine | verified 2026-08-11 |
| `market.breadth_ma_days` | 200 | 2.1 | MarketContextEngine | verified 2026-08-11 |
| `market.sector_composite_min_members` | 5 | 2.1 | IndicatorEngine, MarketContextEngine | verified 2026-08-11 |
| `sentiment.min_baseline_days` | 20 | 2.1 | SentimentEngine | verified 2026-08-11 |
| `market.regime_breadth_high` | 0.60 | D-80 | MarketContextEngine | verified 2026-08-10 |
| `market.regime_breadth_low` | 0.40 | D-80 | MarketContextEngine | verified 2026-08-10 |

**A window named inside a column is a constant, not a key.** `article_count_z_own_90d`
carries its 90, `sentiment_delta_7v30` its 7 and 30, `dist_200dma` its 200 and
`median_dollar_volume_20d` its 20. A tunable value beside a column that names it is a
second place for the number to live and the column name is wrong the first time they
disagree, which is why `FlowEngine.WindowDays` is a `const` beside
`insider_net_90d_usd`. `market.breadth_ma_days` is a key for the opposite reason: it is
the same 200 and no column names it.

**The two regime thresholds were held back at 2.4 and arrived at 2.9 with D-80**, which
is the decision that says what they threshold. Seeding a value for a rule that does not
exist puts a number in the store nothing can be read against, and seeding a key later is
version 1 and does not move the store-wide config version [D-72], so waiting cost
nothing.

**The regime rule's other half has no key and that is deliberate** [D-80]. The benchmark
contributes a sign test rather than a threshold, because a series is above or below its
own 200-day average and zero is already meaningful there. A fraction has no natural cut
and so takes two; a sign has one already.

**Every key in this table and the one above was confirmed by reading the line that
consumes it**, in the same form the ingest keys are recorded in further up: the three
indicator keys and the sector minimum at `IndicatorEngine.cs:94-101`, the own-history
floor at `ValuationEngine.cs:97`, the baseline floor at `SentimentEngine.cs:73`, the
breadth window, the sector minimum again and the two regime thresholds at
`MarketContextEngine.cs:53-56`, and the cell floor at `PercentileEngine.cs:116`. Line
numbers go stale; the file and the stage do not, and both are given so the next reader
can find it either way.

**An entry above reading `unverified` is the accurate state rather than an oversight**
where the component that consumes it does not exist yet. Each checkpoint that wires one
up moves its own row, having read the line that consumes it [CLAUDE.md §8]. The two
regime keys are verified at `MarketContextEngine.cs:56-57`.

## Screens

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `screens.slot_ceiling` | 8 | D-7 | CandidateAllocator | unverified |
| `screens.floor_percentile` | 98 | D-9 | `ScreenEngine.ExecuteAsync`, `ScreenEngine.cs` | verified 4.5 |
| `screens.floor_lookback_days` | 250 | D-9 | `ScreenEngine.ExecuteAsync`, `ScreenEngine.cs` | verified 4.5 |
| `screens.<id>.metrics` | per screen | D-6 | ScreenEngine | unverified |
| `screens.<id>.slots` | 8 each at start | D-43 | CandidateAllocator | unverified |
| `screens.<id>.state` | `live` for S1 to S5 | D-84, D-119 | ScreenEngine | unverified |
| `screens.<id>.min_inputs` | S1 5, S2 4, S3 3, S4 2 | D-112 | ScreenEngine | unverified |
| `screens.S5.quality_metrics` | S1's list, copied | D-120 | ScreenEngine | unverified |
| `screens.S5.technical_metrics` | `dist_200dma`, `dist_52w_high`, `rs_change_63d`, each high | D-120 | ScreenEngine | unverified |
| `screens.S5.quality_min_inputs` | 5 | D-112, D-120 | ScreenEngine | unverified |
| `screens.S5.technical_min_inputs` | 3 | D-112, D-120 | ScreenEngine | unverified |
| `screens.S5.quality_quintile_min` | 80 | D-120 | ScreenEngine | unverified |
| `screens.S5.technical_quintile_max` | 20 | D-120 | ScreenEngine | unverified |

Screen definitions are rows rather than code, so a sixth screen is an insert and not
a deployment.

**`screens.<id>.metrics` is an array of objects, each carrying the metric, the direction
and the weight** [D-113]. A bonus entry carries `kind` and `points` instead of a
direction and is applied after the weighted mean [D-114]:

```
[{"metric": "net_debt_ebitda", "direction": "low", "weight": 1},
 {"metric": "base_breakout_flag", "kind": "bonus", "points": 10}]
```

Direction `low` is applied as 100 minus the stored percentile. An unrecognised direction
or kind fails the stage closed rather than defaulting, because a default of `high` would
invert three of S1's seven inputs and score plausibly.

**The screen ids in these keys are uppercase, `screens.S5.quality_metrics` rather than
`screens.s5.`** D-120 writes the composite keys lowercase and writes `screens.S1.metrics`
uppercase in the same clause, so the decision is not internally consistent about the
case. Uppercase is used because S1 to S5 is how every other document in this corpus names
a screen, and because the facade compares the id in the key against the screen's own id.
The four older `s5.*` keys below keep their existing names and are unchanged; the facade
treats that form as screen-scoped too, so they are reachable by S5 and by nothing else.

**These three shared keys were documented here and seeded by nothing until 4.5.**
`screens.slot_ceiling`, `screens.floor_percentile` and `screens.floor_lookback_days` have
carried values, decisions and a Consumer column since the first corpus, and
`ConfigSeeder.Keys` had no row for any of them. It was found by C13 failing to resolve the
lookback rather than by an audit, which is the direction this document's own rule does not
cover: an unverified entry is worse than an absent one, and an entry for a key with no row
at all is worse than both. `ConfigSeeder.Keys` moves 74 to 77.

**`screens.quota_large`, `quota_mid` and `quota_small` are retired** [D-116]. D-89's
proportion is the only quota arithmetic and nothing reads the three keys once it
exists. Config is append-only, so the rows already inserted stay where they are: the
retirement is a removal from the seeder and from this document rather than a delete.
Prior wording in `CHANGELOG.md`.

**`screens.<id>.state` is seeded `live` for S1 to S5 and no family member is
registered** [D-119]. The shadow mechanism is proven in phase 4 against a fixture
screen that is removed again, so `shadow` is a value the key accepts rather than one
any seeded row carries.

**`screens.<id>.min_inputs` is set per screen, and the values were chosen against
measured coverage rather than picked** [D-112]. Read on 2026-08-12 over the 2,865 active
members, counting non-null `_pctile` inputs per name per screen:

| Screen | Inputs | Floor | Members scorable at that floor |
|---|---|---|---|
| S1 quality | 7 | 5 | 2,356 of 2,865, 82.2% |
| S2 trend | 5 | 4 | 2,865 of 2,865, 100% |
| S3 sentiment | 3 | 3 | 1,763 of 2,865, 61.5% |
| S4 flow | 2 | 2 | 2,517 of 2,865, 87.9% |
| S5 quality composite | 7 | 5 | 2,356 of 2,865, 82.2% |
| S5 technical composite | 3 | 3 | 2,865 of 2,865, 100% |

**S1 is the only screen where this key is a real dial.** Its coverage is graded: 33.1% of
members carry all seven inputs, 61.2% carry six, 82.2% five and 93.4% four. A floor of
seven would draw S1's 98th percentile over 947 names, and S1 is the screen the design's
argument against a megacap tilt leans on hardest. S3 and S4 are near-binary instead, a
name either carrying the sentiment or flow row or not, so 3.4% and 0.3% of members
respectively hold a partial row and lowering either floor buys almost nothing.

**The same read on 2022-06-15 is the reason these are not tuned to one date.** S1 is
better there, 45.4% at all seven against 33.1%; S3 is worse at 39.6% and S4 at 68.3%. The
floors are unchanged across both, and the range run at 4.13 is what says whether the
scored population moves enough over the window to matter [`CLAUDE.md` §11].

## Mean reversion stabilisation

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `s5.stabilisation_z_max` | 1.0 | D-13 | ScreenEngine | unverified |
| `s5.sentiment_delta_min` | 0 | D-13 | ScreenEngine | unverified |
| `s5.news_gate_min_articles` | 3 | D-14 | ScreenEngine | unverified |
| `s5.no_digest_disqualifier_min_articles_90d` | 12 | D-60 | rubric prefix | unverified |

`s5.news_gate_min_articles` is the fail-open threshold. Below three articles in seven
days the two news conditions are treated as satisfied rather than failed.

**The seven days are calendar days and the count comes from `sentiment_daily`.** No store
carries the count as a column: `sentiment_derived_daily.article_count_z_own_90d` is a
z-score and cannot say how many articles there were. A day with no `sentiment_daily` row
is a day with no articles rather than a day nobody looked, so a name with no rows at all
is below the threshold and fails open, which is the thinly covered small cap the rule
exists for [`SCHEMA.md`]. Calendar days rather than sessions, because news arrives on days
the exchange is shut.

**The first three of these were documented here and seeded by nothing until 4.7**, the
same gap the three shared screen keys had at 4.5 and found the same way, by the gate
failing to resolve one. They are seeded at the values above, which are the ones this
document and `ARCHITECTURE.html` §05 already carried; nothing was chosen. The fourth is
the rubric's and is not a screen threshold, so it stays unseeded until phase 5.

`s5.no_digest_disqualifier_min_articles_90d` is the same asymmetry applied at the
rubric rather than the gate. The no-digest disqualifier only bites where the ticker
carried at least twelve articles in ninety days, roughly one a week, because below
that an absence of news is the ordinary state rather than a signal [D-60].

## Gates

C12 GateEngine's thresholds [D-117].

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `gates.gap_pct` | 8 | D-117 | GateEngine | unverified |
| `gates.earnings_blackout_days_before` | 5 | D-117 | GateEngine | unverified |
| `gates.earnings_blackout_days_after` | 2 | D-117 | GateEngine | unverified |
| `gates.cooldown_days` | 30 | D-117 | GateEngine | unverified |

**Halt and already-held carry no key.** They are conditions rather than thresholds: a
name is halted or it is not, and an open `position` row is the already-held test itself.
D-117 asks for one key per threshold and these two have none to state.

**All four values are unconstrained by anything in this corpus, and that is recorded
rather than glossed.** `ARCHITECTURE.html` §3 gives C12 thirteen words and no width,
percentage or day count; D-117 settles where the gate applies and what `gate_state` means
over history and sets no number. Nothing here was reasoned to from a measurement, and the
three seeded so the engine is whole are seeded for that reason and not because a value was
derived [`CLAUDE.md` §8, §11].

**`gates.gap_pct` is the only one 4.8's distribution can speak to.** Three of the five
reasons are structurally unevaluable across the whole backfill window: `position` and
`trade_outcome` hold no rows until phase 7, so already-held and cooldown can never fire,
and `events.earnings_backward_days` is 7 with earnings deliberately not backfilled, so the
blackout has no calendar to read on a historical date [D-117]. Gap is therefore the one
threshold whose value changes a row in this phase, and the firing distribution at 4.8 is
the only evidence any of these four has.

**8 rather than a looser figure, for what the gate is for.** It excludes a name that has
already made the move, and the ordinary "the news already happened" case is a 5 to 10
percent overnight gap. A 15 percent threshold is a rare event on a mid or large cap, so it
would pass almost everything and produce a firing distribution of nearly all zeroes, which
is a threshold that cannot be read at 4.8. 8 is arbitrary in the same sense as the other
three; the difference is that it produces a distribution worth reading.

**One interaction is recorded as seen and accepted rather than discovered in phase 7.**
`gates.cooldown_days` is 30 calendar days and `risk.time_stop_days` is 40 [D-34], so a
name can be re-surfaced as a candidate before a position that ran its full time stop would
have closed. The two keys are not in conflict today, `position` and `trade_outcome` being
empty and the cooldown gate inert for the whole of phase 4. It first matters in phase 7,
which is where the two are reconciled if they need to be.

## Risk

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `risk.starting_equity` | 100000 | D-30 | PortfolioRunner | unverified |
| `risk.per_trade_pct` | 0.005 | D-30 | RiskGate | unverified |
| `risk.max_open_positions` | 8 | D-32 | RiskGate | unverified |
| `risk.max_new_entries_per_day` | 2 | D-32 | RiskGate | unverified |
| `risk.cash_floor_pct` | 0.10 | D-32 | RiskGate | unverified |
| `risk.single_position_cap_pct` | 0.20 | D-32 | RiskGate | unverified |
| `risk.sector_cap_pct` | 0.30 | D-32 | RiskGate | unverified |
| `risk.large_bucket_cap` | 4 | D-32 | RiskGate | unverified |
| `risk.stop_atr_multiple` | 2 | D-31 | RiskGate | unverified |
| `risk.stop_cap_pct` | 0.12 | D-31 | RiskGate | unverified |
| `risk.time_stop_days` | 40 | D-34 | PositionManager | unverified |

**These are operator configuration and the tuner never writes them** [D-43,
INVARIANT 14]. If anything in the learning layer acquires a write path to a
`risk.*` key, that is a defect regardless of how the value moved.

## Broker

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `broker.participation_cap_pct` | 0.01 | D-35 | PaperBroker | unverified |
| `broker.slippage_base_bp_large` | 5 | D-35 | PaperBroker | unverified |
| `broker.slippage_base_bp_mid` | 10 | D-35 | PaperBroker | unverified |
| `broker.slippage_base_bp_small` | 20 | D-35 | PaperBroker | unverified |
| `broker.slippage_participation_mult` | 25 | D-35 | PaperBroker | unverified |

## Researcher

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `researcher.probability_margin_pp` | 8 | D-18 | rubric prefix | unverified |
| `researcher.thesis_max_words` | 60 | D-19 | rubric prefix | unverified |
| `researcher.counter_arg_max_words` | 20 | D-19 | rubric prefix | unverified |
| `researcher.max_tokens` | derived | D-21 | ResearcherClient | unverified |
| `researcher.batch_deadline_et` | 09:00 | D-22 | ResearcherClient | unverified |
| `researcher.cache_ttl` | 1h | D-22 | ResearcherClient | unverified |
| `researcher.concurrency` | 4 | — | ResearcherClient | unverified |

The word limits appear in the rubric and the token cap is enforced on the call. Both
exist: the rubric statement is for quality, the token cap is what protects the
budget.

## Digest chain

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `digest.chain` | local, haiku | D-25 | NewsDigester | unverified |
| `digest.rotation_count` | 2 | D-27 | NewsDigester | unverified |
| `digest.max_tokens` | 150 | D-24 | NewsDigester | unverified |
| `digest.health_timeout_ms` | 5000 | — | LocalModelClient | unverified |
| `digest.readiness_check_et` | 15:30 | — | LocalModelClient | unverified |

The chain is an ordered list, so adding a third link is an insert.

## Learning

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `tuner.shrinkage_old` | 0.8 | D-43 | ScreenTuner | unverified |
| `tuner.slot_floor` | 4 | D-43 | ScreenTuner | unverified |
| `tuner.slot_cap` | 12 | D-43 | ScreenTuner | unverified |
| `tuner.benchmark_column` | vs_peers | D-42 | ScreenTuner | unverified |
| `lessons.min_sample` | 30 | D-44 | LessonWriter | unverified |
| `lessons.expiry_months` | 6 | D-44 | LessonWriter | unverified |
| `lessons.max_active` | 10 | D-44 | LessonWriter | unverified |

`tuner.benchmark_column` is a key rather than a literal so that a test can assert its
value, but changing it to `vs_spy` is a defect and not a tuning option [INVARIANT 5].

## Alerts and budget

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `validator.rejection_rate_alert` | 0.05 | — | ProposalValidator | unverified |
| `monitor.megacap_share_max` | 0.333 | D-7 | ConcentrationMonitor | unverified |
| `monitor.distinct_tickers_60d_min` | 250 | — | ConcentrationMonitor | unverified |
| `monitor.cache_hit_rate_min` | 0.80 | — | CostLedger | unverified |
| `cost.annual_budget` | 100 | — | CostLedger | unverified |
| `freshness.row_count_abort_below` | 40000 | D-59 | FreshnessGuard | verified 2026-08-09 |
| `freshness.row_count_alert_below` | 45000 | D-59 | FreshnessGuard | verified 2026-08-09 |
| `freshness.settled_fraction` | 0.95 | D-70 | FreshnessGuard, PriceFrontier | verified 2026-08-09; second consumer verified 2026-08-22 |
| `freshness.settled_window_days` | 20 | D-70 | FreshnessGuard, PriceFrontier | verified 2026-08-09; second consumer verified 2026-08-22 |
| `price.reload_window_days` | 20 | A10 | PriceIngestor | verified 2026-08-09 |

Phase P measured the bulk end-of-day row count [D-59]: about 50,000 rows on a settled
day, one settled day 11 percent below its neighbours, and part-settled sessions an
order of magnitude lower. A single tolerance was replaced by a floor and an alert
because the two populations are far enough apart that a wide floor separates them
with no false positives, while a tight band would fire on the 11 percent day and
teach the operator to ignore it. There is no upper bound: no failure mode produces
too many rows.

**The guard has three checks and two of them carry keys** [D-70].
`freshness.row_count_*` are completeness. `freshness.settled_*` are settledness,
which became a threshold when the re-fetch was dropped: a date is settled when its
count is at or above `settled_fraction` of the median of the last
`settled_window_days` dates before it, computed from `price_daily` alone [D-70].
Recency still has no key, because it reads the exchange calendar for the most
recent completed session and compares dates rather than crossing a bound.

**`freshness.settled_*` has a second consumer and it is the same question asked of the
other path** [item 60]. `PriceFrontier` is what the backfill driver uses to decide the
newest date `price_daily` holds a real session for, and it refuses a range end past it.
It reads these two keys rather than keys of its own so that the nightly path and the
range path cannot drift on what a real bar count is. It deliberately does not read
`freshness.row_count_*`: those floors are sized for the bulk feed's whole-exchange row
count of about 50,000, and this store holds about 3,000 bars a session, so applying them
to a range would refuse every range ever issued. Read at `BackfillRun.cs`, where the
driver composes the check into the session source it hands the context.

The two values and their reasoning are in D-64's closure rather than here,
including why 0.95 sits six points above the measured part-settled ceiling rather
than midway between the populations, and the holiday-week watch item that would
move it.

`price.reload_window_days` belongs to C02 rather than to the guard. C02 re-loads a
trailing window of dates every night rather than tonight alone, so a day loaded
short tops up on a later run, which is what actually heals accretion; D-68's
idempotent upsert is what makes re-loading safe. Without it a part-settled day
stays part-settled in `price_daily` for ever, and settledness would keep failing it
with nothing able to fix it.

**`price.reload_window_days` and `freshness.settled_window_days` are coupled, and
lowering the first is not a local change** [A26]. The reload window decides how far
back C02 tops a short day up. The settled window decides how far back C07 takes its
median. A date that settles more slowly than the reload window ages out of C02's
reach while still short, stays short for ever, and then sits inside C07's median
dragging the reference down. The guard gets quieter rather than louder, which is the
wrong direction for a guard, and it degrades silently.

**The two count different things and both are 20** [A28].
`price.reload_window_days` counts **calendar days** back from the run date, with a
non-session returning an empty response that is tolerated rather than treated as a
fault, because C02 has no trading calendar to consult until C07 exists.
`freshness.settled_window_days` counts **dates present in `price_daily`**, which are
trading dates by construction, since a date with no session never lands a row.

**The requirement is not that the two windows match.** It is that a date finishes
settling before it ages out of reload reach. Twenty calendar days is about fourteen
trading dates, against an observed settling period of more than twenty-four hours,
so the margin is large and deliberate rather than incidental: 2026-08-04 was still
gaining rows more than a day after its session closed and finished well inside
either window.

This is also the second reason the settled window takes a median rather than a mean,
and the stronger of the two: a median over twenty is unmoved by one stuck day, so
the failure above is bounded even if it happens. A mean would absorb it in
proportion to its shortfall.

A later session lowering `price.reload_window_days` for call-volume reasons should
read this paragraph first. Twenty dates against one bulk call each is not the
expensive part of a night.
