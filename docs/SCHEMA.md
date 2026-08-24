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

**`first_seen` and `last_seen` mean the earliest and latest bar the store holds for
that ticker, not the earliest and latest that existed** [item 51]. Both come from
`min` and `max` over `price_daily` bounded `date <= asOf`, and the upsert conflicts on
`ticker` alone, so both are recomputed from whatever `price_daily` holds each time C01
runs rather than accumulated. A prune of `price_daily` therefore moves them forward on
the next run, with no error and nothing to compare against, and that has now happened
rather than being a risk: the 2026-08-21 prune truncated that table at 2016-01-04, and
the C01 run of 2026-08-22 [`run_log` 1780] took the `security` rows carrying a
`first_seen` earlier than that **from 2,916 of 4,399 to 3**, the earliest moving from
1962-01-02 to 1986-06-06.

**The retained reading is the meaning, and it is settled rather than open** [item 51,
closed 2026-08-22]. The alternative, first listed, is no longer derivable from this
store: `price_daily` does not hold the bars it would be computed from, and C01 recomputes
both columns from that table on every run. A column cannot be defined as something the
system has no way to produce, so naming it "first listed" would describe something that
is not there and would read as a defect on every future comparison. **What this costs is
stated rather than implied**: D-48 makes these two and `delisted_date` the basis for
reconstructing membership per date, so a reconstruction written later reads a lifespan
that starts where the retained history starts, and a name that traded before the prune
floor looks younger than it was. Any analysis needing true listing dates needs a source
outside this store.

**Nothing reads either column.** Confirmed 2026-08-22 by reading the lines rather than
by the grep that found them: every occurrence of `first_seen` in `src/` outside
`0001_snapshot.sql` is inside `UniverseBuilder.cs`, which is the writer, at the column
list, the derivation, and the two records that carry it between them; and the only
statements selecting from `security` at all are two `count(*)` guards in the test
suite. So the truncation cost no behaviour, which is why it was allowed to happen with
the values recorded first rather than being prevented.

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

### universe_rejection
Grain: ticker by evaluation date. **Writer: UniverseBuilder.**

`ticker`, `date`, `criterion`.

One row for every name C01 evaluated on that date and did not admit. A member has no
row, so the two tables partition the evaluated population between them: presence in
`security_daily` with `is_active` is admission, presence here is rejection, and absence
from both means the name was not evaluated on that date at all [D-108].

**`criterion` is the criterion the evaluation stopped on, not the only one the name
failed.** D-4 is a conjunction and C01 tests it in a fixed order, so a name below the
market cap floor that is also thinly traded records the thin trading, that being the
test it reached first. Recording all nine would suggest an independent evaluation the
code does not perform, and reading this column as "the only failure" is the mistake the
sentence exists to prevent.

`criterion` is `NOT NULL` with a `CHECK` on exactly `below_min_price`,
`below_min_dollar_volume`, `insufficient_history`, `not_common_stock`,
`delisted_on_date`, `no_fundamentals`, `below_clean_gaps`, `no_share_count` and
`below_market_cap`, in C01's own test order. The constraint is in the database rather
than in the writer for the reason `regime_label`'s is: this column segments every count
taken off it, so a drifted value lands in its own bucket in every segmentation without
ever erroring.

The first three are applied together inside one statement, which classifies rather than
filters from D-108 onward. A name reaching the remaining six has already passed them.

**The population is bounded by `backfill.window_start`**, the same bound C01 already
applies to its symbol listing and for the same reason: a name that stopped trading
before the window can never be a member on any evaluated date. One population rule
rather than two, so this table and the listing cannot drift apart.

**No column records the threshold a name failed against.** That is configuration,
resolved as of the evaluation date by the rule every other reader uses, and a copy here
could disagree with `config_rows` [D-43, INVARIANT 13].

**The date is recomputed whole rather than merged.** C01 deletes the evaluation date and
inserts it, so a name that was rejected and is now admitted leaves no stale row, and a
re-run of one date produces identical rows [D-68].

---

## Market data

### price_daily
Grain: ticker by day. **Writer: PriceIngestor.**

`ticker`, `date`, `open`, `high`, `low`, `close`, `adj_close`, `volume`.

**What is in here is wider than the universe, and one part of it is deliberate**
[D-104]. The backfill sweep loads every admitted common stock, live and delisted, plus
every reference series: a price series a component reads as a comparison and that is
admitted to nothing, written to neither `security` nor `security_daily` and therefore a
member of no universe on any date. `SPY.US` is the only one. The nightly bulk feed also
lands whatever else the exchange returns, which is where the ETFs and funds in this table
come from and is not deliberate. **The distinction matters because a reader cannot see
it**: C08 and C10 both read `SPY.US`, the sweep's pool did not carry it, and its 265 bulk
rows looked like a loaded series while four of five and a half years of relative strength
and regime were empty.

### price_fetch_attempt
Grain: one row per ticker. **Writer: PriceIngestor.**

`ticker`, `last_attempted_date`, `last_yield_date`, `rows_last_attempt`.

**What a ticker-partitioned sweep resumes on, and the only thing it resumes on**
[D-99, 0010]. The price sweep resumed from a ticker parsed back out of the `run_log` line.
Three real failures in one day produced that position once: the first fault arrived in
the allowance gate's own call rather than inside the dispatch loop and recorded nothing,
a test fixture's cleanup deleted the row carrying the second, and the third recorded one
and resumed correctly. A killed process records nothing at all, the log write being the
last thing a run does. An attempt row is written as the sweep goes, so a clean halt, a
command timeout and a `kill -9` all resume identically.

**A sweep's attempts are stamped with the range start**, so the remaining set is the
pool minus the tickers carrying one at that date. The range end defaults to today and
moves under a sweep re-invoked the next morning; the start does not.

**Tickers already present in `price_daily` is the predicate this replaces, and it does
not work.** C02's nightly reload has been loading every admitted name since phase 2,
where the table held 13,091,293 rows over 274 dates, so presence says almost nothing
about whether a ticker was swept: a swept ticker has years of bars and a nightly-only
ticker has the last twenty dates. Resuming on it would skip most of the pool.

**`last_yield_date` null means attempted and yielded nothing, which is a different fact
from an absent row, which means never attempted** [`CLAUDE.md` §6]. That is also the
leak the presence predicate carries: a ticker the price endpoint answers `404` for
writes no bars and would be re-asked on every run for ever, which is the defect 0008
measured at C05 one table over.

**No counter column, because a tally would not be idempotent.** Each column is a
function of the last attempt alone, so a second run over one range writes what the first
wrote [D-68].

**The rotation shape is deliberately absent.** Its two neighbours order a rotation and
read attempts strictly before the run date; this is a set difference and reads them at
one date. The table is the same and the question asked of it is not.

### fundamental_snapshot
Grain: ticker by fiscal period. **Writer: FundamentalsIngestor.**

`ticker`, `period_end`, `filing_date`, `filing_date_effective`,
`filing_date_unknown_reason`, `period_type`, `sector`, plus the statement fields.

**`sector` is stored per filing and C01 reads it there** [D-97, 0009]. C03 writes it
from `General::Sector` on the call it already makes, and C01 reads the most recent
filing at or before the date being built rather than making a call per member. Sector as
of a filing is not sector as of a date: a company that reclassifies between filings
reads as its former sector until the next one lands, which is closer to point-in-time
than a single current value and is not the same thing. A ticker with no fundamental rows
has no sector and resolves to the existing bucket-only percentile fallback.

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

`ticker`, `last_attempted_date`, `swept_through_date`, `last_yield_date`,
`rows_last_attempt`.

**Two columns, two readers, and they were one column until 0013** [item 44]. `last_attempted_date` is the nightly rotation's freshness ordering and is **nullable**, null meaning the night has never attempted this ticker, which is a different fact from an absent row, meaning nothing has. `swept_through_date` is the range sweep's marker and is read as coverage, `>= the range end`, never as equality with it. One column served both and each reader undid the other's work: a sweep stamp sorted ahead of any later nightly date so the rotation preferred the names the sweep had just paid for, and a night's stamp knocked a swept ticker back into the sweep's remaining set to be bought again. Read as coverage rather than equality because D-105 refuses a range end past the ingest frontier, so every later run asks for an earlier end than the stamps carry, and under equality the whole pool re-dispatches; that recurs on any frontier correction that moves an end. The stamp is still the range end on both these tables, which is D-99's asymmetry and is unchanged.

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

### earnings_history
Grain: ticker by fiscal period. **Writer: FundamentalsIngestor.**

`ticker`, `period_end`, `report_date`, `before_after_market`, `eps_actual`,
`eps_estimate`, `surprise_fraction`.

**Captured on the sweep that pays for it** [D-96, 0009]. `Earnings::History` sits in the
`fundamentals/{t}` payload C03 already fetches, so capturing it during a sweep costs
nothing and capturing it afterwards costs the sweep again at 10 units a ticker. Capture
is not use: D-90 is open on whether post-earnings drift registers at all and this
prefers no fork.

**Every read is keyed on `report_date <= date`**, which is `filing_date_effective`'s
analogue one table over [INVARIANT 12]. A row whose `report_date` is null is stored and
is unreadable, exactly as an undated fundamental row is.

That is not lookahead, and the reason is stated because it looks like it might be. The
nightly run executes after the close, so a result released after the close of the date
being computed was public before the run began. A backfilled date inherits the property,
`report_date` being when the result actually landed.

**`before_after_market` is load-bearing rather than descriptive.** A result released
after the close of day D is reacted to on D+1 and one released before the open of D is
reacted to on D, so a drift screen computing its reaction window without this column
uses the wrong session for roughly half of all announcements.

**`surprise_fraction` is a fraction and the column is named for what it holds.** The
provider sends a percent and it is divided on the way in, which is the rule every ratio
in this system follows. It is stored rather than derived because the provider's figure
may rest on an estimate other than the one it reports; whether it agrees with
`(eps_actual - eps_estimate) / abs(eps_estimate)` is what 3.7's own output answers.

### flow_fetch_attempt
Grain: one row per ticker. **Writer: FlowIngestor.**

`ticker`, `last_attempted_date`, `swept_through_date`, `last_yield_date`,
`rows_last_attempt`.

**Two columns, two readers, and they were one column until 0013** [item 44]. `last_attempted_date` is the nightly rotation's freshness ordering and is **nullable**, null meaning the night has never attempted this ticker, which is a different fact from an absent row, meaning nothing has. `swept_through_date` is the range sweep's marker and is read as coverage, `>= the range end`, never as equality with it. One column served both and each reader undid the other's work: a sweep stamp sorted ahead of any later nightly date so the rotation preferred the names the sweep had just paid for, and a night's stamp knocked a swept ticker back into the sweep's remaining set to be bought again. Read as coverage rather than equality because D-105 refuses a range end past the ingest frontier, so every later run asks for an earlier end than the stamps carry, and under equality the whole pool re-dispatches; that recurs on any frontier correction that moves an end. The stamp is still the range end on both these tables, which is D-99's asymmetry and is unchanged.

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

### sentiment_fetch_attempt
Grain: one row per ticker. **Writer: SentimentIngestor.**

`ticker`, `last_attempted_date`, `last_yield_date`, `rows_last_attempt`.

**The fourth of these, and the one where `last_yield_date` null is the common case**
[D-99, 0011]. A sparse series is the ordinary state above rather than a fault, so a
ticker nobody wrote about across the whole window is fetched, yields nothing, and would
never gain a row in `sentiment_daily`. Resuming a sweep on presence in that table would
therefore re-fetch it every run for ever, and the names it would loop on are exactly the
thinly covered ones the sentiment screen exists to find.

**The stamp is the range start, which is C02's half of D-99's asymmetry.** The nightly
call asks from `context.Date - sentiment.lookback_days` and the sweep asks from
`backfill.window_start`, so the two differ in depth and the sweep's marker has to be one
no nightly run can produce. `fundamental_fetch_attempt` and `flow_fetch_attempt` stamp
the range end because for those two the nightly call and the sweep call are the same
call.

**One row per ticker though the call is batched.** The endpoint takes a comma-separated
symbol list, so the unit of work is a batch; the unit of billing is a ticker, flat at
five [1.6, 3.1]. Resumption keys on what was paid for.

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
Grain: ticker by holder by report date, the source's own.
**Writer: FundamentalsIngestor** [D-98].

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

The block rides the `fundamentals/{t}` call C03 already makes, so nothing fetches this
table separately and no sweep re-fetches it [D-98].

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

### event_fetch_attempt
Grain: one row per ticker. **Writer: EventsIngestor.**

`ticker`, `last_attempted_date`, `last_yield_date`, `rows_last_attempt`.

**What the distributions sweep resumes on** [D-99, 0012]. Presence in `events` cannot
serve: a name that has never split and never paid a dividend is ordinary rather than
missing, so `splits/{t}` and `div/{t}` both return empty and it gains no row ever. 3.1
measured the case on `SPY.US` itself, which is the benchmark rather than an obscure
name. The earnings rows in that table make the predicate worse rather than better, being
written by a different call, so a ticker with an earnings row and no distributions would
read as covered while carrying nothing the sweep is for.

**The stamp is the range start.** The nightly stage takes splits and dividends from the
bulk feed for one date and the sweep takes whole history per ticker, so the two differ
in depth as completely as two calls can.

**One row per ticker for two calls.** Neither `splits/{t}` nor `div/{t}` is dispatched
without the other, so a half-covered ticker is a state the sweep cannot produce and the
record does not make representable.

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

### percentile_cell_daily
Grain: date by size bucket by sector by metric. **Writer: PercentileEngine.**

`date`, `size_bucket`, `sector`, `metric`, `source_table`, `cell_members`,
`bucket_members`, `min_members`, `ranked_scope`.

**The population a percentile was ranked against, kept rather than discarded** [D-107].
C11 computes each metric's non-null count in its cell and in its bucket inside one
window function, uses both to pick a scope, and until 0014 kept neither. A percentile
without the size of the population behind it is unreadable: one over three members and
one over eighty are the same number, and the fifteen-member fallback is theoretical
rather than visible.

**The grain is the cell, not the row.** The population is a property of the cell, so one
row per member per metric would restate it thousands of times over on the two largest
tables in the store.

`cell_members` is the non-null count of that metric in `(size_bucket, sector)` and is
null on the row that carries no sector. `bucket_members` is the count in the bucket
alone and repeats across the sectors of a bucket, which is a redundancy accepted so a
reader takes one row per metric rather than two. `min_members` is
`percentile.cell_min_members` as it stood on that date.

**`ranked_scope` is stored rather than derived**, `cell`, `bucket` or `none` under a
`CHECK`. Comparing a count against a floor and labelling the answer is a computation,
and the reader that shows this does not compute; storing it leaves that decision with
the component that made it.

**A null sector forms no cell** and goes to the bucket fallback [`METRICS.md` §6.4], so
it has a row with no sector. The key is a unique index over
`(date, size_bucket, sector, metric)` declared `NULLS NOT DISTINCT`, Postgres not
allowing a null in a primary key [0016].

**An empty-string sector is a different cell from a null one and the key distinguishes
them.** `security_daily.sector` holds both, and C11's `CellQualifies` tests
`sector IS NOT NULL`, so an empty string is a real cell of its own while a null forms no
cell at all. The two reach the same bucket fallback by different routes and seeing which
route is what this store is for. A reader matches with `IS NOT DISTINCT FROM`, which is
the comparison the index makes.

`metric` is sufficient in the key without `source_table` beside it, the thirty metric
names being distinct across the four sources. `source_table` is carried so a reader knows
where to look, and a future collision is a failed key rather than a silent overwrite.

### percentile_cell_coverage
Grain: one row per source table. **Writer: PercentileEngine.**

`source_table`, `covered_from`, `covered_to`.

**What the populating pass reached, read as coverage and never as equality** [D-106,
D-107]. Without it a date the pass has not reached and a cell that does not exist are
the same empty result, and a reader would render "no cell" for a date whose cells simply
have not been written yet.

One row per source table rather than one for the store, because the pass writes per
source and a halt between the four statements of a date leaves three further on than the
fourth. The pass runs date-descending, so the covered set is a contiguous suffix and two
dates describe it.

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

`ticker`, `date`, `passed`, `reasons`, `gate_state`.

Records every failing reason, not the first.

**The reason vocabulary is closed by a CHECK and not only by an enum.** `reasons` may
hold `earnings_blackout`, `gap`, `halt`, `already_held` and `cooldown`, which are §03's
five in §03's order, and the constraint arrives with migration `0018` at checkpoint 4.8.
A vocabulary closed in code alone leaves the column able to hold a string no reader can
interpret, which would then fail at read time one night later and one component away from
whatever wrote it.

**`passed` is `cardinality(reasons) = 0` and is written in the same statement**, so the
flag and the array cannot disagree.

**`gate_state` is `passed` or `passed_partial` and never `gated`, and it is a property of
the date rather than of the name** [D-117]. `passed_partial` means at least one gate
reason was structurally unevaluable on that date: `position` and `trade_outcome` hold no
rows until phase 7 and earnings are deliberately not backfilled, so over the backfill
window three of the five reasons cannot fire at all. It is what stops a backfilled night
reading identically to a live one. C14 carries the value onto the `attribution` row, so
the component that reads the three stores is the component that records what it found;
the column arrives with migration `0019` at checkpoint 4.10 and is `text NOT NULL` with a
CHECK and no `DEFAULT`. `gated` is absent from the vocabulary because a gated name has no
attribution row to carry a state.

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

**The allocator deletes the date before it rebuilds it**, and both operations are its
own, so INVARIANT 10 read per operation is untouched. An insert with `ON CONFLICT` alone
updates the names a re-run surfaces and leaves behind the ones it no longer does, so a
night re-run after a name was gated would keep that name in the set. Two runs of a stage
over one date must produce identical output and with the stale row surviving they did
not [4.9].

**`slot_filled` has no stated meaning anywhere in this corpus and is written null.** The
grain is one row per candidate and a candidate is a name that took a slot, so a column
saying so would be true of every row; a column about the slots that stayed empty cannot
be carried at this grain, there being no row for them. Null is what the column means
until something states otherwise, which is the rule for an absent value rather than a
placeholder [`CLAUDE.md` §6]. Reported at 4.9.

### attribution
Grain: ticker by day surfaced. **Writers: CandidateAllocator inserts,
ForwardReturnFiller updates.**

`ticker`, `date`, `screens_surfacing`, `score_per_screen`, `surfaced_as`,
`size_bucket`, `sector`, `regime`, `gate_state`, `config_version`,
`digest_provider`, `return_5d_raw`, `return_5d_vs_spy`, `return_5d_vs_peers`, and
the same triple at 21 and 63 days.

**`surfaced_as` and `score_per_screen`'s object shape arrive with migration `0017` at
checkpoint 4.1 and are not in the database yet** [D-110]. They are stated here rather
than at the migration because the operator adopted phase 4's authored items ahead of
its first checkpoint; D-110 asks that this document gain the column in the same
checkpoint as the migration, and this note is the divergence made visible rather than
left to be found. Neither is a `real` column, so neither enters the not-money
declaration below and no check reads them before `0017` lands.

`surfaced_as` is `text NOT NULL` with `CHECK (surfaced_as IN ('candidate','shadow'))`
and no `DEFAULT`, so a writer that has not decided fails at the column rather than
taking a value nobody chose. `score_per_screen` is screen id to an object of score and
rank, held by a `jsonb` CHECK asserting every top-level value is an object, so the flat
shape cannot be written at all. The `candidate_attribution` view selects
`surfaced_as = 'candidate'` and every reader meaning candidate reads the view [D-110,
D-85].

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

**The type vocabulary is closed by a CHECK and not only by a constant** [D-126].
`alert_type` may hold `megacap_share` and `distinct_tickers_60d`, which are §18's two
conditions for this writer, and the constraint arrives with migration `0021` at
checkpoint Q.4. This is `gate_result.reasons` closed the same way for the same reason: a
vocabulary held in code alone leaves the column able to carry a string no reader can
interpret.

**§18 names four further conditions whose row reads "Alert" and whose owner is C07, C03,
C13 or C26.** None of those components declares a write to this table and no document
says how their alerts are recorded, so their type strings are deliberately not in the
vocabulary. A phase that gives one of them a writer adds the name and extends the
constraint in the same checkpoint, which is the rule §attribution's `surfaced_as` already
follows [D-110].

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
| `earnings_history.eps_actual` | `real` | a per-share figure, not a monetary total [D-96] |
| `earnings_history.eps_estimate` | `real` | a per-share figure, not a monetary total [D-96] |
| `earnings_history.surprise_fraction` | `real` | a fraction, the provider's percent divided on the way in [D-96] |
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
