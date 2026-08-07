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
| `universe.min_market_cap` | 300000000 | D-4 | UniverseBuilder | unverified |
| `universe.min_price` | 5 | D-4 | UniverseBuilder | unverified |
| `universe.min_adv_20d` | 2000000 | D-4 | UniverseBuilder | unverified |
| `universe.min_history_days` | 250 | D-4 | UniverseBuilder | unverified |
| `universe.bucket_large_floor` | 10000000000 | D-4 | UniverseBuilder | unverified |
| `universe.bucket_mid_floor` | 2000000000 | D-4 | UniverseBuilder | unverified |

## Fundamentals

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| ~~`fundamentals.filing_date_substitution_days`~~ | ~~65~~ | ~~D-57~~ | ~~FundamentalsIngestor~~ | [superseded, D-62] |
| `fundamentals.min_clean_gaps_for_substitution` | 4 | D-62 | UniverseBuilder, ~~FundamentalsIngestor~~ [corrected, L.2] | unverified |
| `fundamentals.substitution_rate_alert` | 0.25 | D-57 | FundamentalsIngestor | unverified |

The substitution window is ~~the widest gap the probe observed, not a mean, because
being late costs freshness while being early costs correctness~~ [superseded, D-62]
each ticker's own widest clean gap observed before the read date. There is no
universal constant left to configure: the per-ticker value is derived from that
ticker's filing history rather than set here, so it gets no key. What is
configurable is the floor beneath which no substitution is attempted at all. Below
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

## Percentiles

| Key | Default | Set by | Consumer | Verified |
|---|---|---|---|---|
| `percentile.cell_min_members` | 15 | D-10 | PercentileEngine | unverified |

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
| ~~`freshness.row_count_tolerance`~~ | ~~from probe~~ | — | FreshnessGuard | [removed, D-59] |
| `freshness.row_count_abort_below` | 40000 | D-59 | FreshnessGuard | unverified |
| `freshness.row_count_alert_below` | 45000 | D-59 | FreshnessGuard | unverified |
| `freshness.settled_fraction` | 0.95 | D-70 | FreshnessGuard | unverified |
| `freshness.settled_window_days` | 20 | D-70 | FreshnessGuard | unverified |
| `price.reload_window_days` | 20 | A10 | PriceIngestor | unverified |

~~The freshness tolerance has no default until phase P measures a real bulk end-of-day
row count.~~ [removed, D-59]

Phase P measured it: about 50,000 rows on a settled day, one settled day 11 percent
below its neighbours, and part-settled sessions an order of magnitude lower. A single
tolerance was replaced by a floor and an alert because the two populations are far
enough apart that a wide floor separates them with no false positives, while a tight
band would fire on the 11 percent day and teach the operator to ignore it. There is no
upper bound: no failure mode produces too many rows.

~~**The guard has three checks and only one of them has a key** [D-65]. The two keys
above are completeness. Recency reads the exchange calendar for the most recent
completed trading session, and settledness compares a re-fetch of a date against the
rows already stored for it. Neither is a threshold, so neither gets a key, and adding
one would invent a bound where the decision deliberately introduced none.~~
[superseded, D-70]

**The guard has three checks and two of them carry keys.**
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
