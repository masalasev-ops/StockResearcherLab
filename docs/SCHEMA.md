# SCHEMA.md

Every table, its grain, and which component owns which write to it. The ownership
declaration is not documentation. It is the contract the conformance test in phase 0
asserts against the stage registry, which is how INVARIANT 10 is enforced mechanically
rather than by review.

**Ownership is per operation, not per table** ~~and two tables are documented
exceptions~~ [amended, INVARIANT 10 and L.3]. The registry declares component, table,
operation and column set, and the test asserts no two components claim the same
triple. Where a heading below reads **Writer: X**, X owns every operation on that
table. Where it names several, each declares the operation and the columns it owns,
and nothing else may write there at all.

The exception list was the wrong shape rather than too short. `attribution`,
`proposal`, and `order` with `fill` and `position` each had more than one writer from
the first draft, which is already past the two the old rule allowed. Three splits over
five tables, and no fourth: the one candidate for it, a stored clean gap count on
`security`, turned out to want computing rather than storing [M.1]. Every attempt to
enumerate exceptions ran out before the list was complete, because a rule that counts
exceptions gets longer every time the design is correct.

Column lists below are the load-bearing ones, not exhaustive. Types, indexes and
constraints are phase 0 work and are not fabricated here.

**Sizes are estimates after a five-year backfill, not measurements.**

---

## Reference

### security
Grain: one row per ticker. **Writer: UniverseBuilder.**

`ticker`, `name`, `sector`, `size_bucket`, `market_cap`, `first_seen`, `last_seen`,
`delisted_date`, `is_active`.

Size buckets: large-and-above at $10B or more, mid $2B to $10B, small $300M to $2B.
`delisted_date` is populated rather than the row deleted, because the historical
universe must be reconstructable per date including names that no longer exist
[D-48].

**The clean gap count is computed, never stored** ~~as `clean_gap_count`, maintained
by FundamentalsIngestor~~ [reversed, M.1]. UniverseBuilder counts rows in
`fundamental_snapshot` for that ticker whose `filing_date_unknown_reason` is `none`
and whose `filing_date_effective` is at or before the date being built, and excludes
below `fundamentals.min_clean_gaps_for_substitution` [D-62, D-4].

A stored scalar would have been wrong in the permissive direction. The count is
as-of: a ticker has more clean gaps now than it had three years ago, so a backfill
reading one value would admit names a live system on that date would have excluded,
and backfilled screen scores would sit on a different population than live ones. Made
a computation, it is point-in-time correct by construction and needs no column, no
second writer on this table, and nothing to keep in step [INVARIANT 13].

---

## Market data

### price_daily
Grain: ticker by day. **Writer: PriceIngestor.** ~400 MB.

`ticker`, `date`, `open`, `high`, `low`, `close`, `adj_close`, `volume`.

### fundamental_snapshot
Grain: ticker by fiscal period. **Writer: FundamentalsIngestor.** ~60 MB.

`ticker`, `period_end`, `filing_date`, `filing_date_effective`,
`filing_date_unknown_reason`, `period_type`, plus the statement fields.

**`filing_date_effective` is the key every read filters on, never `period_end` and
never the raw `filing_date`** [D-46, D-62, INVARIANT 12]. `period_end` and the raw
`filing_date` are both stored so the gap is inspectable, but no query joins on
either.

`filing_date_effective` equals `filing_date` where the provider supplied a real one,
meaning a date at least one day after `period_end`. Where it is null, equal to
`period_end`, or earlier than it, the provider has supplied no usable filing date,
and the row is instead readable from `period_end` plus that ticker's own widest
clean gap observed before the read date [D-62]. A ticker with fewer than
`fundamentals.min_clean_gaps_for_substitution` clean gaps observed is excluded from
the universe rather than given a substituted date.

`filing_date_unknown_reason` records which case fired, one of `null`, `equal`,
`negative` or `none`. Four distinguishable states rather than a boolean, because
which one fired is diagnostic: a rise in `null` is the provider dropping the field,
a rise in `equal` is its date handling changing, and `negative` is a date that
cannot exist and should never appear at all. The substitution rate stays measurable
rather than invisible.

### sentiment_daily
Grain: ticker by day, whole universe. **Writer: SentimentIngestor.** ~380 MB.

`ticker`, `date`, `article_count`, `sentiment_score`.

### headline
Grain: candidate by day. **Writer: HeadlineIngestor.** Small.

`ticker`, `date`, `published_at`, `title`, `source`, `url`.

Only for names that reached the candidate set, since headlines exist for the dossier
and only candidates reach the dossier [D-23].

### insider_transaction
Grain: ticker by filing by transaction, the source's own. **Writer: FlowIngestor.**
Small.

`ticker`, `filed_at`, `transaction_date`, `reporting_owner_name`,
`transaction_code`, `shares_amount`, `price_per_share`, `total_value`,
`acquired_or_disposed`.

`transaction_code` is not optional. The S4 rubric disqualifies option exercises and
scheduled plan activity, so a count that cannot separate an open-market purchase from
an award is not the count the screen needs [D-61].

### institutional_holding
Grain: ticker by holder by report date, the source's own. **Writer: FlowIngestor.**
Small.

`ticker`, `report_date`, `holder_name`, `shares`, `change`, `change_pct`.

Quarterly, because that is the grain the filings arrive at. ~~`report_date` is what
makes this backfillable, and it is the field short interest turned out not to have.~~
[struck, D-69] Measured false at 1.9. `Holders::Institutions` is a top-20 snapshot
rather than a series: CCS.US and NVDA.US return 20 entries at a single `report_date`
and BXC.US 20 across two, `sec-filings/{t}/13f` is a 404, and the filings index
lists only `10k`, `10q`, `form4` and `8k`. The column is populated, which is why
the claim survived being written. One or two distinct values per ticker is not a
history, so `inst_ownership_change` has nothing to compute a change over and
accumulates forward only. The table still ingests, because a current top-20 holder
list is a usable static feature; it is the change metric that has no series.

### flow_daily
Grain: ticker by day, ~~ticker by week~~ [superseded, D-61]. **Writer: FlowEngine, a
compute stage, not the ingest.** ~52 MB.

`ticker`, `date`, `insider_net_usd_90d`, `distinct_buyer_count`,
`inst_ownership_change`,
~~`week_end`, `publication_date`, `short_interest_pct_float`,
`short_interest_change`~~ [removed, D-58 and D-61].

Derived rather than ingested. The two source tables above land at their own grain and
this table is computed from them, exactly as `indicator_daily` is computed from
`price_daily`. Ingest grain follows the source; consumption grain follows the screen
[D-61].

Short interest is gone: this provider has no series and no as-of date for it, so it is
not backfillable and the screen ranks on the three fields above [D-58].
`publication_date` went with it, having been named for the field it keyed.

### events
Grain: ticker by event. **Writer: EventsIngestor.** Small.

`ticker`, `event_type`, `event_date`, `announced_date`.

---

## Computed

### indicator_daily
Grain: ticker by day. **Writer: IndicatorEngine.** ~1.2 GB, the second largest table.

Roughly forty technical columns plus their percentiles. `atr_pct`, `adx14`,
`dist_200dma`, `dist_52w_high`, `rs_change_21d`, `rs_change_63d`,
`rs_change_vs_sector`, `volume_vs_50d_avg`, `ma50_200_slope`,
`base_breakout_flag`~~, `median_dollar_volume_20d`~~ [moved out of this list, O.2].

Store as 32-bit floats. No technical indicator needs fifteen significant figures and
it halves the table.

`median_dollar_volume_20d` is `numeric`, not a 32-bit float, because a dollar volume
is money and INVARIANT 16 does not bend for storage size [O.2].

All computed locally from `price_daily`. Never bought from the provider.

### valuation_daily
Grain: ticker by day. **Writer: ValuationEngine.** ~540 MB.

`fcf_yield`, `ev_ebit`, `ev_ebit_vs_own_5y`, `roic`, `roic_4q_change`,
`gross_margin_4q_change`, `net_debt_ebitda`, `accruals`, `share_count_change`,
`revenue_growth_4q_trend`, `cash_on_hand`, `quarterly_burn_rate`,
`last_two_earnings_surprises`.

Recomputed daily because price moves. Every fundamental input resolved as of
`filing_date_effective` [D-62].

### market_context_daily
Grain: one row per day. **Writer: MarketContextEngine.** Small.

`date`, `breadth`, `vix`, `regime_label`, plus sector relative strength.

### percentile columns
Written alongside their source tables by **PercentileEngine**.

Computed within size bucket by sector cells, falling back to size bucket alone when a
cell has fewer than fifteen members [D-10]. Expressed as window functions partitioned
by bucket and sector, so this is set-based and date-partitioned rather than
application-threaded.

---

## Selection

### gate_result
Grain: ticker by day. **Writer: GateEngine.** ~120 MB.

`ticker`, `date`, `passed`, `reasons`.

Records every failing reason, not the first.

### screen_score_daily
Grain: ticker by screen by day. **Writer: ScreenEngine.** ~1.4 GB, the largest table.

`ticker`, `screen_id`, `date`, `score`, `rank_within_screen`, `config_version`.

Larger than the price data it derives from, because every ticker is scored by all
five screens every day. That is necessary rather than wasteful: the floor is the
98th percentile of the screen's own trailing distribution and you cannot know the
distribution without scoring everyone [D-9].

### screen_history
Grain: screen by day. **Writer: ScreenEngine.** Small.

Trailing distribution summary per screen, from which the floor is computed.

### candidate_set
Grain: ticker by day. **Writer: CandidateAllocator.** ~9 MB.

`ticker`, `date`, `screens_surfacing`, `size_bucket`, `slot_filled`.

### attribution
Grain: ticker by day surfaced. **Writers: CandidateAllocator inserts,
ForwardReturnFiller updates.** ~9 MB.

`ticker`, `date`, `screens_surfacing`, `score_per_screen`, `size_bucket`,
`sector`, `regime`, `gate_state`, `config_version`, `digest_provider`,
`return_5d_raw`, `return_5d_vs_spy`, `return_5d_vs_peers`, and the same triple at
21 and 63 days.

~~**This is the only table with two writers, and it is deliberate.**~~ [superseded,
INVARIANT 10 as amended] Two components, one operation each. **CandidateAllocator
owns the insert**, writing the row with scores frozen and return columns empty.
**ForwardReturnFiller owns the update**, and only of the nine return columns. Neither
may perform the other's operation, and no third component writes here at all, which
is a stronger claim than the exception it replaces.

Written at shortlist time for every candidate, never reconstructed [D-40, INVARIANT
4]. `config_version` is what lets history be segmented rather than pooled after a
screen definition changes.

Acquisitions, delistings and bankruptcies need explicit handling rather than row
deletion, or survivorship bias enters the attribution table itself.

---

## Decide

### news_digest
Grain: ticker by day. **Writer: NewsDigester.** Grows ~10 MB/yr.

`ticker`, `date`, `digest_text`, `provider`, `model_name`, `was_rotation`.

`provider` and `model_name` are not optional [D-29]. `was_rotation` marks the two
candidates a night deliberately routed to the secondary [D-27], so the paired sample
is separable from genuine fallthroughs.

### dossier
Grain: one prefix per night plus one block per candidate. **Writer: DossierBuilder.**
Grows ~40 MB/yr.

`date`, `prefix_text`, `prefix_hash`, `ticker`, `block_text`.

Persisted before any call goes out, which is what makes citation verification
possible after the fact. `prefix_hash` is what the snapshot test asserts on.

### proposal
Grain: ticker by day by model. **Writers: ResearcherClient inserts, ProposalValidator
updates status.** Grows ~30 MB/yr.

`ticker`, `date`, `model_id`, `verdict`, `p_target_before_stop`, `thesis`,
`counter_argument`, `primary_driver`, `stop_pct`, `target_pct`, `horizon_days`,
`status`, `rejection_reason`.

~~Second deliberate two-writer pair~~ [superseded, INVARIANT 10 as amended]. Two
components, one operation each. **ResearcherClient owns the insert.**
**ProposalValidator owns the update**, and only of `status` and `rejection_reason`.
Nothing else writes here.

---

## Execute

### portfolio
Grain: one row per portfolio. **Writer: configuration, not a stage.** Tiny.

`portfolio_id`, `name`, `selection_method`, `provider`, `model_id`, `use_batch`,
`is_primary`, `state`.

`state` is `active`, `winding_down` or `retired`. Winding down stops new entries
while open positions run to natural exit, so retirement never distorts the trade
record by force-closing anything [D-38].

Exactly one research portfolio carries `is_primary`. Screens and Random match their
entry count to whichever it is.

### portfolio_selection
Grain: portfolio by ticker by day. **Writer: PortfolioRunner.** Small.

`portfolio_id`, `date`, `ticker`, `source`, `source_ref`.

`source` is one of `proposal`, `screen_rotation` or `random_draw`, and
`source_ref` points at the proposal, the screen and rank, or the seed. The two
research portfolios select from `proposal`; the two controls have no equivalent
store and this is it, which is what lets one component apply risk to all four
rather than two components applying it to two each [INVARIANT 8].

A selection that the risk caps then block leaves a row here and no order. That
is the only place the difference between what a control portfolio wanted and
what it got is visible, and without it an underperforming control cannot be
read as a weak selection rule rather than a blocked one.

### order / fill / position
Grain: per event, tagged by portfolio. **RiskGate inserts orders. PaperBroker inserts
fills and inserts positions. PositionManager updates positions to closed.** Small.

Three tables and three components, each owning a different transition. This is the
group the old two-exception rule could never have accommodated, and it is why the rule
was restated per operation rather than extended by one more exception. The registry
declares the operation and column set for each, and the conformance test asserts no
two components claim the same triple.

~~RiskGate and PortfolioRunner both insert orders and are the one pair that shares an
operation on a table. They are separated by portfolio: the runner writes for every
non-research portfolio off the shared candidate set, the gate writes for the research
portfolios after arbitration.~~ [reversed, N.1] No operation on these tables is shared
by two components. The runner writes `portfolio_selection` and the gate turns
selections into orders for every portfolio, so sizing, stops and caps exist in exactly
one place. A split by portfolio class would have put them in two, and INVARIANT 8 says
that voids the comparison.

### trade_outcome
Grain: per closed trade. **Writer: PositionManager.** Small.

`portfolio_id`, `ticker`, `entry_date`, `exit_date`, `pnl`, `alpha_vs_spy`,
`alpha_vs_peers`, `mfe`, `mae`, `exit_reason`.

**All monetary columns are decimal, never float or double** [INVARIANT 16].

---

## Learn and configure

### config_rows
Grain: key by version. **Writer: configuration and ScreenTuner.** Tiny.

Append-only and versioned. Current is `MAX(version)` for a key. A change inserts
version + 1. Anything reading config for a simulated date resolves as of that date,
never as-now [D-43, INVARIANT 13].

Holds screen definitions, slot allocations, the digest provider chain, and every
value that could plausibly be tuned. No magic numbers at call sites.

### researcher_memory
Grain: per revision. **Writer: LessonWriter.** Small.

`lesson_text`, `sample_size`, `written_at`, `expires_at`, `reconfirmed_at`.

Maximum ten active. Requires n of at least 30. Expires after six months unless
reconfirmed [D-44].

### calibration
Grain: per model per screen per report. **Writer: CalibrationReporter.** Small.

Brier score, reliability buckets, sample sizes.

---

## Operations

### run_log
Grain: per stage per run. **Writer: RunLog.** Small.

`run_date`, `stage`, `status`, `started_at`, `duration_ms`, `rows_written`,
`error`.

### cost_ledger
Grain: per call. **Writer: CostLedger.** Small.

`date`, `model_id`, `portfolio_id`, `input_tokens`, `cache_write_tokens`,
`cache_read_tokens`, `output_tokens`, `cost`, `was_batch`.

Validator rejection counts are recorded here per model alongside spend, since that
is a hallucination measure worth watching independently of returns.

### alert
Grain: per alert. **Writer: ConcentrationMonitor.** Small.

`date`, `alert_type`, `detail`, `acknowledged`.

### local_model_config
Grain: one row per provider in the chain. **Writer: the UI, via the single permitted
write endpoint** [D-51].

`provider_order`, `endpoint`, `enabled`, `last_health_check`, `last_loaded_model`.

The only table the interface can write. Nothing here touches run data.

---

## Totals

Roughly 5 GB after a five-year backfill, growing about 700 MB a year. Two tables
carry most of it: `screen_score_daily` and `indicator_daily`, both ticker-by-day
fanouts that scale linearly with universe size. Lowering the liquidity floor or
adding a sixth screen moves these numbers in a way nothing else in the design does.
