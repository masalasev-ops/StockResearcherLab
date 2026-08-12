# SCHEMA.md

Every table, its grain, and which component owns which write to it. The ownership
declaration is not documentation. It is the contract the conformance test in phase 0
asserts against the stage registry, which is how INVARIANT 10 is enforced mechanically
rather than by review.

**Ownership is per operation, not per table** [INVARIANT 10, L.3]. The registry
declares component, table, operation and column set, and the test asserts no two
components claim the same triple. Where a heading below reads **Writer: X**, X owns
every operation on that table. Where it names several, each declares the operation
and the columns it owns, and nothing else may write there at all.

**There is no count of how many tables may have several writers, and nothing replaces
it as a count** [D-77]. A rule that counts exceptions gets longer every time the design
is correct, which is what happened: the count stood at three splits over five tables
until the percentile engine made it seven over nine.

The brake a count provided is not lost. Adding a second writer means editing this
document, which a build session cannot do, where a count is prose a build session can
read past. The conformance test derives the permitted splits from the headings below
rather than from a list of its own, so what a heading says is checked on every push.

Column lists below are the load-bearing ones, not exhaustive. Types, indexes and
constraints are phase 0 work and are not fabricated here.

**Size after backfill is not here.** It is `ARCHITECTURE.html` §16's column, derivable
from the grain a heading below already states, and a third statement of it is the
duplication D-76 exists to remove. §16's store list is held against this document in
both directions by a test; its size column is not, and phase 3's sign-off is where
those figures stop being estimates [`BUILD_PLAN.md` carried obligations].

---

## Reference

### security
Grain: one row per ticker. **Writer: UniverseBuilder.**

`ticker`, `name`, `first_seen`, `last_seen`, `delisted_date` [D-92].

Identity and lifespan, and nothing that varies by date. Membership, sector, size
bucket and market capitalisation are per date and are in `security_daily`. A reader
meaning "this ticker" reads here; a reader meaning "the universe on a date" reads
there.

`delisted_date` is populated rather than the row deleted, because the historical
universe must be reconstructable per date including names that no longer exist
[D-48].

**The clean gap count is computed, never stored** [M.1]. UniverseBuilder counts rows
in `fundamental_snapshot` for that ticker whose `filing_date_unknown_reason` is
`none` and whose `filing_date_effective` is at or before the date being built, and
excludes below `fundamentals.min_clean_gaps_for_substitution` [D-62, D-4].

A stored scalar would have been wrong in the permissive direction. The count is
as-of: a ticker has more clean gaps now than it had three years ago, so a backfill
reading one value would admit names a live system on that date would have excluded,
and backfilled screen scores would sit on a different population than live ones. Made
a computation, it is point-in-time correct by construction and needs no column, no
second writer on this table, and nothing to keep in step [INVARIANT 13].

### security_daily
Grain: ticker by date. **Writer: UniverseBuilder.**

`ticker`, `date`, `sector`, `size_bucket`, `market_cap`, `is_active`.

Size buckets: large-and-above at $10B or more, mid $2B to $10B, small $300M to $2B.

**This table exists because C11's cell is `(size_bucket, sector)` on the date being
ranked** [D-10, D-92]. `security` carries one row per ticker, so a backfilled 2021
date ranked against it would put every name in its 2026 cell, and a percentile
computed over a slightly wrong cell is not inspectable afterwards: nothing
downstream can see the cell it was computed over.

**Written on C01's own weekly cadence, and read as the most recent row at or before
the date.** That is what makes a backfilled cell sit on the identical population
rule as a live one rather than on a rule the live system does not share [D-92,
D-58].

**Sector is the one column that is not point-in-time.** This provider carries no
sector history, so a ticker's sector is fetched once and carried across the window
and a reclassification inside the window is invisible. It is a bounded distortion of
cell membership, stated rather than proxied [D-92].

**`is_active` is `NOT NULL` and carries no default**, where `security`'s column
defaults to true and that default is what let C01 have no path that deactivates a
name [`PROGRESS.md`, 2026-08-09]. Membership on a date is the presence of the row,
so every row C01 writes reads true.

**`market_cap` is `numeric`** [INVARIANT 16].

---

## Market data

### price_daily
Grain: ticker by day. **Writer: PriceIngestor.**

`ticker`, `date`, `open`, `high`, `low`, `close`, `adj_close`, `volume`.

### fundamental_snapshot
Grain: ticker by fiscal period. **Writer: FundamentalsIngestor.**

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

**The column is nullable, where null means the provider's filing date was unusable
and no substitution was derivable either** [1.4], which is a ticker with zero clean
gaps to take a widest from. `NOT NULL` could only be satisfied there by writing a
date that is not true: `period_end` makes the row readable immediately, which is the
lookahead D-62 exists to prevent, and a universal constant is what D-62 explicitly
rejected.

A narrower constraint replaces it, `CHECK (filing_date_effective IS NOT NULL OR
filing_date_unknown_reason <> 'none')`. That still catches the case `NOT NULL` was
pointing at, a row losing its date to a bug, while permitting the one case it could
only handle by fabricating. Every read filters `filing_date_effective <= date`, so a
null row is unreadable by construction rather than by anyone remembering to exclude
it, and the zero-clean-gap population is
`filing_date_unknown_reason <> 'none' AND filing_date_effective IS NULL`.

`filing_date_unknown_reason` records which case fired, one of `null`, `equal`,
`negative` or `none`. Four distinguishable states rather than a boolean, because
which one fired is diagnostic: a rise in `null` is the provider dropping the field,
a rise in `equal` is its date handling changing, and `negative` is a date that
cannot exist and should never appear at all. The substitution rate stays measurable
rather than invisible.

### fundamental_fetch_attempt
Grain: one row per ticker. **Writer: FundamentalsIngestor.**

`ticker`, `last_attempted_date`, `last_yield_date`, `rows_last_attempt`.

**The record is of the attempt, not of the result, and that is the whole point of
it** [0006]. C03 ordered never-fetched first, where fetched meant any row in
`fundamental_snapshot`. Once the pool is covered that group is empty, so the same
alphabetically-first names are selected on every run afterwards and coverage never
advances: `capital_expenditures` reached 482 tickers running contiguously from
`A.US` to `CCBG.US` and stopped there, while two consecutive runs wrote an identical
44,365 rows over an identical 500 tickers.

A `fetched_at` column on `fundamental_snapshot` would not fix it. That column moves
only when rows are written, so a ticker whose fetch returns nothing never moves and
sits at the front of the rotation for ever. A column on `security` fails differently:
C03's pool is the candidate set, deliberately broader than the universe, so a pool
member with no `security` row would have nowhere to record an attempt.

**`last_yield_date` null means attempted and never yielded, which is a different fact
from an absent row, which means never attempted.** Those are the two states the old
ordering conflated [`CLAUDE.md` §6].

**No counter column, because a tally would not be idempotent.** Each column is a
function of the last attempt alone, so a second run over one date writes what the
first wrote [D-68].

**The rotation reads attempts strictly before the run date.** A re-run of one date
therefore sees the state the first run saw and selects the same names, so the stage
stays a pure function of its date and config version; the rotation advances between
dates rather than between runs. That is the discipline every fundamental read already
applies to `filing_date_effective` [INVARIANT 12, INVARIANT 13, `CLAUDE.md` §6].

### flow_fetch_attempt
Grain: one row per ticker. **Writer: FlowIngestor.**

`ticker`, `last_attempted_date`, `last_yield_date`, `rows_last_attempt`.

**The same record as `fundamental_fetch_attempt` for the component D-91 explicitly
left open** [D-95, 0008]. C05's ordering was copied from C03's by hand at 1.7 and kept
the defect after C03's was fixed: never-fetched first, where fetched meant any row in
`insider_transaction`, so once the pool is covered that group is empty and the same
alphabetically-first names are selected on every run for ever.

**The 14 of 250 that answer `404 Symbol not found` are what makes it expensive.** They
write no rows, so they stayed never-fetched and were re-asked every run, and a 404 is
billed at 10 units [3.1]. The frozen head was spending 140 units a night on calls that
cannot succeed.

**A shared table was rejected and the ordering function is shared instead.** Two
components writing one table is two claims on one component-table-operation triple
[INVARIANT 10]. `RotationSelection` is what both read, so the two rotations cannot
drift apart the way the code and the catalogue did.

The three properties carry over from `fundamental_fetch_attempt` unchanged: the record
is of the attempt rather than the result, attempts are read strictly before the run
date so a re-run of one date selects the same names, and `last_yield_date` null means
attempted and never yielded, which is a different fact from an absent row.

### sentiment_daily
Grain: ticker by day, whole universe. **Writer: SentimentIngestor.**

`ticker`, `date`, `article_count`, `sentiment_score`.

**A day with no row is a day with no articles, not a day nobody looked** [D-78]. C04
covers the whole universe every night with no pre-selection, and the probe found days
carrying a non-zero count identical to days carrying a row on all seven names, so no
empty rows are written. That is what lets `article_count` be read as zero across an
absent day and it is the reason the derived table below can exist at all.

### sentiment_derived_daily
Grain: ticker by day [D-78]. **Writers: SentimentEngine, a compute stage and not the
ingest, inserts; PercentileEngine updates the percentile columns** [D-77].

`ticker`, `date`, `article_count_z_own_90d`, `sentiment_delta_7v30`,
`sentiment_7d_level`.

Derived rather than ingested, exactly as `flow_daily` is derived from the two flow
source tables and `indicator_daily` from `price_daily`. Ingest grain follows the
source; consumption grain follows the screen [D-61].

S3 ranks on these three and on nothing else, S5's stabilisation gate reads the first
two, and the dossier's fixed core carries two. The three windows are named in the
columns, so they are constants in the stage rather than config keys.

**`article_count` zero-fills across an absent day and `sentiment_score` does not**, and
the two rules are the same rule applied to absences that mean different things. There is
no tone where there are no articles. Zero-filling the score would say the coverage was
exactly neutral, which is a different claim from the coverage not existing, and it would
pull every thinly covered name toward zero in proportion to how thinly covered it is.
That is a size proxy arriving where D-12 exists to keep one out.

### headline
Grain: candidate by day. **Writer: HeadlineIngestor.**

`ticker`, `date`, `published_at`, `title`, `source`, `url`.

Only for names that reached the candidate set, since headlines exist for the dossier
and only candidates reach the dossier [D-23].

### insider_transaction
Grain: ticker by filing by transaction, the source's own. **Writer: FlowIngestor.**

`ticker`, `accession_number`, `transaction_side`, `transaction_ordinal`,
`filed_at`, `transaction_date`, `reporting_owner_cik`, `reporting_owner_name`,
`transaction_code`, `security_title`, `shares_amount`, `price_per_share`,
`total_value`, `shares_owned_after`, `acquired_or_disposed`.

**The grain is the filing plus the line's position in it** [D-68 reopened at 1.7].
A7 proposed a key made of the transaction's own attributes and left 1.7's sweep to
confirm it against real rows. It does not hold: over 1,069 real transactions across
eight tickers that tuple collided 172 times, and adding security title, price and
shares-owned-after still left 5. Two line items in one filing can be identical on
every value the provider sends, the ordinary case being an option exercise reported
as common stock acquired and as restricted stock units disposed, same owner, same
date, same code, same share count.

So `accession_number` plus `transaction_side` plus `transaction_ordinal` is the key,
which makes "ticker by filing by transaction" literal rather than approximating it
with attributes. A key derived from values that are legitimately repeatable is not a
key, and an upsert on one collapses two real transactions into one silently.

`transaction_code` is not optional. The S4 rubric disqualifies option exercises and
scheduled plan activity, so a count that cannot separate an open-market purchase from
an award is not the count the screen needs [D-61].

### institutional_holding
Grain: ticker by holder by report date, the source's own. **Writer: FlowIngestor.**

`ticker`, `report_date`, `holder_name`, `shares`, `change`, `change_pct`.

Quarterly, because that is the grain the filings arrive at. **`report_date` does not
make this backfillable** [D-69], measured false at 1.9. `Holders::Institutions` is a
top-20 snapshot rather than a series: CCS.US and NVDA.US return 20 entries at a
single `report_date` and BXC.US 20 across two, `sec-filings/{t}/13f` is a 404, and
the filings index lists only `10k`, `10q`, `form4` and `8k`. The column is populated,
which is why the claim survived being written. One or two distinct values per ticker
is not a history, so `inst_ownership_change` has nothing to compute a change over and
accumulates forward only. The table still ingests, because a current top-20 holder
list is a usable static feature; it is the change metric that has no series.

### flow_daily
Grain: ticker by day [D-61]. **Writers: FlowEngine, a compute stage and not the ingest,
inserts; PercentileEngine updates the percentile columns** [D-77].

`ticker`, `date`, `insider_net_90d_usd` [A1.a], `distinct_buyer_count`,
`inst_ownership_change` [D-58, D-61].

Derived rather than ingested. The two source tables above land at their own grain and
this table is computed from them, exactly as `indicator_daily` is computed from
`price_daily`. Ingest grain follows the source; consumption grain follows the screen
[D-61].

**The column was named `insider_net_usd_90d` here and in `0001_snapshot.sql` and
`insider_net_90d_usd` in `ARCHITECTURE.html` sections 3 and 5 and in D-61** [A1.a].
The architecture constrains the code and names match the architecture, so this
document was the wrong one. Renamed in `0002` rather than dropped and recreated:
the table had never held a row, so either would have done, and a rename says what
happened where a drop would not.

Short interest is gone: this provider has no series and no as-of date for it, so it is
not backfillable and the screen ranks on the three fields above [D-58].
`publication_date` went with it, having been named for the field it keyed.

### events
Grain: ticker by event. **Writer: EventsIngestor.**

`ticker`, `event_type`, `event_date`, `announced_date`.

---

## Computed

### indicator_daily
Grain: ticker by day. **Writers: IndicatorEngine inserts, PercentileEngine updates the
percentile columns** [D-77].

**The technical columns are those with a named consumer, and the list is here rather
than a count of it** [D-83]. `SchemaParityTests` holds the type declarations in §Types
against the live database in both directions, so the columns below are checked where a
count could not be. Each arrives with its percentile.

`atr_pct`, `adx14`, `dist_200dma`, `dist_52w_high`, `rs_change_21d`, `rs_change_63d`,
`rs_change_vs_sector`, `volume_vs_50d_avg`, `ma50_200_slope`, `base_breakout_flag`
[O.2], and four added at 2.2 for readers that had none: `dist_20dma`, `rs_20d_slope`,
`rs_21d_63d_change`, `dist_52w_high_20d_change`.

`dist_20dma` is the signed fraction rather than the 20-day average itself, so S5's
stabilisation gate reads `dist_20dma > 0` rather than joining back to the close. It
matches `dist_200dma`'s shape and it keeps a price out of a `real` column.

Store as 32-bit floats. No technical indicator needs fifteen significant figures and
it halves the table.

`median_dollar_volume_20d` is `numeric`, not a 32-bit float, because a dollar volume
is money and INVARIANT 16 does not bend for storage size [O.2].

All computed locally from `price_daily`. Never bought from the provider.

### valuation_daily
Grain: ticker by day. **Writers: ValuationEngine inserts, PercentileEngine updates the
percentile columns** [D-77].

`fcf_yield`, `ev_ebit`, `ev_ebit_vs_own_5y`, `roic`, `roic_4q_change`,
`gross_margin_4q_change`, `net_debt_ebitda`, `accruals`, `share_count_change`,
`revenue_growth_4q_trend`, `cash_on_hand`, `quarterly_burn_rate`,
`last_two_earnings_surprises`.

Recomputed daily because price moves. Every fundamental input resolved as of
`filing_date_effective` [D-62].

### market_context_daily
Grain: one row per day. **Writer: MarketContextEngine.**

`date`, `breadth`, `vix`, `regime_label`, `sector_relative_strength`.

**`regime_label` is `NOT NULL` with a `CHECK` on exactly `risk_on`, `risk_off` and
`mixed`** [D-80, 0005]. Enforced by the database rather than by the writer, and for the
same reason `filing_date_unknown_reason` is: this column segments analysis, being
carried in the cached prefix, stored on every attribution row and shown on screen U1, so
a drifted or mistyped value lands in its own bucket in every segmentation without ever
erroring. A writer-side check protects one writer; a constraint protects the column.

`risk_on` when breadth is at or above `market.regime_breadth_high` and the benchmark is
above its own 200-day average, `risk_off` when breadth is at or below
`market.regime_breadth_low` and the benchmark is below it, `mixed` otherwise. The
benchmark contributes a sign test and carries no threshold, because a series is above or
below its own average and zero is already meaningful there.

**`vix` is null and contributes nothing to the label.** The bulk end-of-day feed carries
equities and the index is not among them. The absence is recorded rather than allowed to
null the label, since three components read the label and a null would degrade all three
over a column none of them reads [D-80].

`sector_relative_strength` is a jsonb object of sector to trailing relative return
against the universe composite, with keys in ordinal sort order. Dictionary enumeration
order is unspecified and this column reaches the cached prefix, where a byte difference
breaks the cache and roughly triples the input bill silently [INVARIANT 6].

### percentile columns
Named `<metric>_pctile` and stored alongside the metric they rank, on the metric's own
table rather than in a table of their own. `real`, scaled 0 to 100, and none of them is
money whatever its source column is.

Who writes them is stated in the four headings above and nowhere here [D-77]. Stating
it in a third place is the duplication D-73 and D-76 exist to remove, and the headings
are the copy a conformance test reads.

Computed within size bucket by sector cells, falling back to size bucket alone when a
cell has fewer than fifteen members [D-10]. Expressed as window functions partitioned
by bucket and sector, so this is set-based and date-partitioned rather than
application-threaded.

This section has no `Grain:` line and is deliberately not parsed as a table, which
`SchemaParityTests` asserts. The columns belong to the tables above and are declared
individually under "Columns that are not money".

---

## Selection

### gate_result
Grain: ticker by day. **Writer: GateEngine.**

`ticker`, `date`, `passed`, `reasons`.

Records every failing reason, not the first.

### screen_score_daily
Grain: ticker by screen by day. **Writer: ScreenEngine.**

`ticker`, `screen_id`, `date`, `score`, `rank_within_screen`, `config_version`.

Larger than the price data it derives from, because every ticker is scored by all
five screens every day. That is necessary rather than wasteful: the floor is the
98th percentile of the screen's own trailing distribution and you cannot know the
distribution without scoring everyone [D-9].

### screen_history
Grain: screen by day. **Writer: ScreenEngine.**

Trailing distribution summary per screen, from which the floor is computed.

### candidate_set
Grain: ticker by day. **Writer: CandidateAllocator.**

`ticker`, `date`, `screens_surfacing`, `size_bucket`, `slot_filled`.

### attribution
Grain: ticker by day surfaced. **Writers: CandidateAllocator inserts,
ForwardReturnFiller updates.**

`ticker`, `date`, `screens_surfacing`, `score_per_screen`, `size_bucket`,
`sector`, `regime`, `gate_state`, `config_version`, `digest_provider`,
`return_5d_raw`, `return_5d_vs_spy`, `return_5d_vs_peers`, and the same triple at
21 and 63 days.

Two components, one operation each [INVARIANT 10 as amended]. **CandidateAllocator
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
Grain: ticker by day. **Writer: NewsDigester.**

`ticker`, `date`, `digest_text`, `provider`, `model_name`, `was_rotation`.

`provider` and `model_name` are not optional [D-29]. `was_rotation` marks the two
candidates a night deliberately routed to the secondary [D-27], so the paired sample
is separable from genuine fallthroughs.

### dossier
Grain: one prefix per night plus one block per candidate. **Writer: DossierBuilder.**

`date`, `prefix_text`, `prefix_hash`, `ticker`, `block_text`.

Persisted before any call goes out, which is what makes citation verification
possible after the fact. `prefix_hash` is what the snapshot test asserts on.

### proposal
Grain: ticker by day by model. **Writers: ResearcherClient inserts, ProposalValidator
updates status.**

`ticker`, `date`, `model_id`, `verdict`, `p_target_before_stop`, `thesis`,
`counter_argument`, `primary_driver`, `stop_pct`, `target_pct`, `horizon_days`,
`status`, `rejection_reason`.

Two components, one operation each [INVARIANT 10 as amended]. **ResearcherClient owns
the insert.** **ProposalValidator owns the update**, and only of `status` and
`rejection_reason`. Nothing else writes here.

---

## Execute

### portfolio
Grain: one row per portfolio. **Writer: configuration, not a stage.**

`portfolio_id`, `name`, `selection_method`, `provider`, `model_id`, `use_batch`,
`is_primary`, `state`.

`state` is `active`, `winding_down` or `retired`. Winding down stops new entries
while open positions run to natural exit, so retirement never distorts the trade
record by force-closing anything [D-38].

Exactly one research portfolio carries `is_primary`. Screens and Random match their
entry count to whichever it is.

### portfolio_selection
Grain: portfolio by ticker by day. **Writer: PortfolioRunner.**

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
fills and inserts positions. PositionManager updates positions to closed.**

Three tables and three components, each owning a different transition. This is the
group the old two-exception rule could never have accommodated, and it is why the rule
was restated per operation rather than extended by one more exception. The registry
declares the operation and column set for each, and the conformance test asserts no
two components claim the same triple.

No operation on these tables is shared by two components [N.1]. The runner writes
`portfolio_selection` and the gate turns selections into orders for every portfolio,
so sizing, stops and caps exist in exactly one place. A split by portfolio class
would have put them in two, and INVARIANT 8 says that voids the comparison.

### trade_outcome
Grain: per closed trade. **Writer: PositionManager.**

`portfolio_id`, `ticker`, `entry_date`, `exit_date`, `pnl`, `alpha_vs_spy`,
`alpha_vs_peers`, `mfe`, `mae`, `exit_reason`.

**All monetary columns are decimal, never float or double** [INVARIANT 16].

---

## Learn and configure

### config_rows
Grain: key by version. **Writer: configuration and ScreenTuner.**

Append-only and versioned. Current is `MAX(version)` for a key. A change inserts
version + 1. Anything reading config for a simulated date resolves as of that date,
never as-now [D-43, INVARIANT 13].

Holds screen definitions, slot allocations, the digest provider chain, and every
value that could plausibly be tuned. No magic numbers at call sites.

### researcher_memory
Grain: per revision. **Writer: LessonWriter.**

`lesson_text`, `sample_size`, `written_at`, `expires_at`, `reconfirmed_at`.

Maximum ten active. Requires n of at least 30. Expires after six months unless
reconfirmed [D-44].

### calibration
Grain: per model per screen per report. **Writer: CalibrationReporter.**

Brier score, reliability buckets, sample sizes.

---

## Operations

### run_log
Grain: per stage per run. **Writer: RunLog.**

`run_date`, `stage`, `status`, `started_at`, `duration_ms`, `rows_written`,
`error`.

### cost_ledger
Grain: per call. **Writer: CostLedger.**

`date`, `model_id`, `portfolio_id`, `input_tokens`, `cache_write_tokens`,
`cache_read_tokens`, `output_tokens`, `cost`, `was_batch`.

Validator rejection counts are recorded here per model alongside spend, since that
is a hallucination measure worth watching independently of returns.

### alert
Grain: per alert. **Writer: ConcentrationMonitor.**

`date`, `alert_type`, `detail`, `acknowledged`.

### local_model_config
Grain: one row per provider in the chain. **Writer: the UI, via the single permitted
write endpoint** [D-51].

`provider_order`, `endpoint`, `enabled`, `last_health_check`, `last_loaded_model`.

The only table the interface can write. Nothing here touches run data.

---

## Types

### Columns that are not money

**INVARIANT 16 is asserted from this list rather than from an exclusion list in a
script** [1.8]. `guards.ps1` parses the tables below and makes two assertions:
every column whose name matches the monetary pattern is `numeric` unless it appears
here, and every `real` or `double precision` column in the migrations appears here.
Adding a `real` column therefore means declaring it in this document, which is where
a reader would look, rather than in a script, which is where nobody does.

The monetary pattern covers `_usd`, `price`, `value`, `cap`, `cost`, `equity`,
`pnl`, `dollar` and `amount`, matched against the column name and not the table's.
Extend the pattern as the schema grows. Do not extend a list of files to skip: that
was the previous mechanism and it had reached two entries with the whole of phase 2's
compute layer still to come [D-83], at which point the guard would have been suppressed
rather than satisfied.

**The count is stated so the check cannot pass over an empty match set.** Eighteen
columns match the monetary pattern and are `numeric` [D-79], and a check finding fewer
has stopped reading part of the schema rather than found a cleaner one. That is not
hypothetical: the parser written for this missed `"order"` and `"position"`, whose
identifiers are quoted because both are reserved words, and six monetary columns were
silently outside the set it reported on.

The eighteenth is `fundamental_snapshot.capital_expenditures`, which matches through
`cap` and is money [D-79]. The number moving is the mechanism working rather than an
inconvenience: it is exact rather than a floor precisely so that a monetary column
cannot arrive without someone thinking about its type.

Everything below is a measurement, a ratio, an index or a key. None of it is a sum
of money, and storing a technical measure as a 32-bit float halves the two largest
tables in the system [O.2].

| Column | Type | What it is |
|---|---|---|
| `indicator_daily.atr_pct` | `real` | a percentage of price, not a price |
| `indicator_daily.adx14` | `real` | an index between 0 and 100 |
| `indicator_daily.dist_200dma` | `real` | a distance as a fraction |
| `indicator_daily.dist_52w_high` | `real` | a distance as a fraction |
| `indicator_daily.rs_change_21d` | `real` | a relative change |
| `indicator_daily.rs_change_63d` | `real` | a relative change |
| `indicator_daily.rs_change_vs_sector` | `real` | a relative change |
| `indicator_daily.volume_vs_50d_avg` | `real` | a ratio of two volumes |
| `indicator_daily.ma50_200_slope` | `real` | a slope |
| `indicator_daily.dist_20dma` | `real` | a distance as a fraction |
| `indicator_daily.rs_20d_slope` | `real` | a slope of a relative strength ratio |
| `indicator_daily.rs_21d_63d_change` | `real` | a difference of two relative changes |
| `indicator_daily.dist_52w_high_20d_change` | `real` | a change in a distance |
| `valuation_daily.fcf_yield` | `real` | a yield |
| `valuation_daily.ev_ebit` | `real` | a multiple |
| `valuation_daily.ev_ebit_vs_own_5y` | `real` | a multiple against its own history |
| `valuation_daily.roic` | `real` | a return rate |
| `valuation_daily.roic_4q_change` | `real` | a change in a rate |
| `valuation_daily.gross_margin_4q_change` | `real` | a change in a margin |
| `valuation_daily.net_debt_ebitda` | `real` | a ratio of two monetary figures, itself unitless |
| `valuation_daily.accruals` | `real` | a ratio |
| `valuation_daily.share_count_change` | `real` | a proportional change in a count |
| `valuation_daily.revenue_growth_4q_trend` | `real` | a trend in a growth rate |
| `valuation_daily.last_two_earnings_surprises` | `real` | percentages, `real[]` |
| `sentiment_daily.sentiment_score` | `real` | a normalised score between -1 and 1 |
| `institutional_holding.change_pct` | `real` | a percentage change in a share count |
| `flow_daily.inst_ownership_change` | `real` | a proportional change in a share count |
| `market_context_daily.breadth` | `real` | a fraction of the market |
| `market_context_daily.vix` | `real` | an index level |
| `sentiment_derived_daily.article_count_z_own_90d` | `real` | a z-score against the ticker's own baseline |
| `sentiment_derived_daily.sentiment_delta_7v30` | `real` | a difference of two sentiment means |
| `sentiment_derived_daily.sentiment_7d_level` | `real` | a mean of a normalised score |
| `screen_score_daily.score` | `real` | a screen score |
| `screen_history.floor_score` | `real` | a screen score |
| `screen_history.p98_trailing` | `real` | a screen score percentile |

### The percentile columns are not money either

Thirty of them, one per ranked metric, added at 2.2. Every one is a rank between 0 and
100 within a size and sector cell, so none is a sum of money whatever its source column
is. They are listed rather than pattern-matched away, because the two mechanisms this
document rejects are a list of files to skip and a rule that guesses from a name.

| Column | Type | What it is |
|---|---|---|
| `indicator_daily.atr_pct_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.adx14_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.dist_20dma_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.dist_200dma_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.dist_52w_high_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.dist_52w_high_20d_change_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.rs_change_21d_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.rs_change_63d_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.rs_21d_63d_change_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.rs_20d_slope_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.rs_change_vs_sector_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.volume_vs_50d_avg_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.ma50_200_slope_pctile` | `real` | a rank between 0 and 100 |
| `indicator_daily.median_dollar_volume_20d_pctile` | `real` | a rank between 0 and 100. Its source column is money and this is not |
| `valuation_daily.fcf_yield_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.ev_ebit_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.ev_ebit_vs_own_5y_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.roic_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.roic_4q_change_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.gross_margin_4q_change_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.net_debt_ebitda_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.accruals_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.share_count_change_pctile` | `real` | a rank between 0 and 100 |
| `valuation_daily.revenue_growth_4q_trend_pctile` | `real` | a rank between 0 and 100 |
| `flow_daily.insider_net_90d_usd_pctile` | `real` | a rank between 0 and 100. Its source column is money and this is not |
| `flow_daily.distinct_buyer_count_pctile` | `real` | a rank between 0 and 100 |
| `flow_daily.inst_ownership_change_pctile` | `real` | a rank between 0 and 100 |
| `sentiment_derived_daily.article_count_z_own_90d_pctile` | `real` | a rank between 0 and 100 |
| `sentiment_derived_daily.sentiment_delta_7v30_pctile` | `real` | a rank between 0 and 100 |
| `sentiment_derived_daily.sentiment_7d_level_pctile` | `real` | a rank between 0 and 100 |

**Two of those names match the monetary pattern**, `insider_net_90d_usd_pctile` through
`_usd` and `median_dollar_volume_20d_pctile` through `dollar`. Both are declared here
for the reason above and not by an exception carved into the pattern, so the pattern
stays a statement about names and this list stays the single place a type is argued.

**Two columns whose names collide with the monetary pattern** and are not money.
They are declared here for the same reason and by the same mechanism, so there is
one list rather than one per kind of exception.

| Column | Type | What it is |
|---|---|---|
| `config_rows.value` | `jsonb` | the config payload. The word is generic and this one is not a sum of money |
| `cost_ledger.cost_ledger_id` | `bigint` | an identity key that happens to sit on a table about cost |

`median_dollar_volume_20d` is deliberately absent from both tables. It is a dollar
volume, so it is money, and it is `numeric` for that reason rather than `real`
despite living among the technical columns [O.2]. A future edit moving it here would
be the mistake this section exists to make visible.

---

## Totals

Roughly 5 GB after a five-year backfill, growing about 700 MB a year. Two tables
carry most of it: `screen_score_daily` and `indicator_daily`, both ticker-by-day
fanouts that scale linearly with universe size. Lowering the liquidity floor or
adding a sixth screen moves these numbers in a way nothing else in the design does.
