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

## Percentiles

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `percentile.cell_min_members` | 15 | D-10 | PercentileEngine | unverified |

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
| `indicator.wilder_warmup_bars` | 250 | 2.1 | IndicatorEngine | unverified |
| `indicator.base_lookback_days` | 60 | 2.1 | IndicatorEngine | unverified |
| `indicator.base_max_range_pct` | 0.25 | 2.1 | IndicatorEngine | unverified |
| `valuation.own_history_min_points` | 24 | 2.1 | ValuationEngine | unverified |
| `market.breadth_ma_days` | 200 | 2.1 | MarketContextEngine | unverified |
| `market.sector_composite_min_members` | 5 | 2.1 | IndicatorEngine, MarketContextEngine | unverified |
| `sentiment.min_baseline_days` | 20 | 2.1 | SentimentEngine | unverified |
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

**An entry above reading `unverified` is the accurate state rather than an oversight**
where the component that consumes it does not exist yet. Each checkpoint that wires one
up moves its own row, having read the line that consumes it [CLAUDE.md §8]. The two
regime keys are verified at `MarketContextEngine.cs:56-57`.

## Screens

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `screens.slot_ceiling` | 8 | D-7 | CandidateAllocator | unverified |
| `screens.quota_large` | 2 | D-7 | CandidateAllocator | unverified |
| `screens.quota_mid` | 3 | D-7 | CandidateAllocator | unverified |
| `screens.quota_small` | 3 | D-7 | CandidateAllocator | unverified |
| `screens.floor_percentile` | 98 | D-9 | ScreenEngine | unverified |
| `screens.floor_lookback_days` | 250 | D-9 | ScreenEngine | unverified |
| `screens.<id>.metrics` | per screen | D-6 | ScreenEngine | unverified |
| `screens.<id>.slots` | 8 each at start | D-43 | CandidateAllocator | unverified |

Screen definitions are rows rather than code, so a sixth screen is an insert and not
a deployment.

## Mean reversion stabilisation

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `s5.stabilisation_z_max` | 1.0 | D-13 | ScreenEngine | unverified |
| `s5.sentiment_delta_min` | 0 | D-13 | ScreenEngine | unverified |
| `s5.news_gate_min_articles` | 3 | D-14 | ScreenEngine | unverified |
| `s5.no_digest_disqualifier_min_articles_90d` | 12 | D-60 | rubric prefix | unverified |

`s5.news_gate_min_articles` is the fail-open threshold. Below three articles in seven
days the two news conditions are treated as satisfied rather than failed.

`s5.no_digest_disqualifier_min_articles_90d` is the same asymmetry applied at the
rubric rather than the gate. The no-digest disqualifier only bites where the ticker
carried at least twelve articles in ninety days, roughly one a week, because below
that an absence of news is the ordinary state rather than a signal [D-60].

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
| `freshness.settled_fraction` | 0.95 | D-70 | FreshnessGuard | verified 2026-08-09 |
| `freshness.settled_window_days` | 20 | D-70 | FreshnessGuard | verified 2026-08-09 |
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
