# Phase 3.5 — The record for one name on one date

**Target** phase 3.5, one screen that answers what the store holds for a ticker on a date and
where each number came from.

**Authored** 2026-08-22, at phase 3's sign-off, as `BUILD_PLAN.md` requires of the next
phase's detail.

**Read against** `ARCHITECTURE.html` §03, §15 and §16, `SCHEMA.md`, `METRICS.md` §6,
`CONFIG_REFERENCE.md`, and `CLAUDE.md` §2, §5 and §13, at HEAD `256f3eb`. Four claims below
rest on the code rather than on a document and were read there: `UniverseBuilder.MembershipAsync`
and `LiquidAsync`, `PercentileEngine.Windowed` and `FallbackReportSql`, `Universe.AsOf`, and
`DeclaredAccess` with `StageData` and `ReadDeclarationConformanceTests` [`CLAUDE.md` §7].

**Status** `DRAFT`, which is the only value this corpus uses for a plan.
`phase-2-compute.md` is still `DRAFT` and phase 2 is signed off and merged. The numbered
checkpoints below are phase scope and are authored into `BUILD_PLAN.md` by a human rather than
from here [`CLAUDE.md` §13].

**This plan is archived to `prompts/spent/` before any code, not after**, on phase 3's
precedent and for its reason: phase 1 and phase 2 both archived at the end and phase 2's
archive is missing half of what was issued [`PROGRESS.md`, 2.13].

---

## Context

**The question this phase answers is "what does this system know about this name on that date,
and where did each number come from".** Not observability. Not monitoring. One page, one
ticker, one date.

Three weeks of phase 3 produced complete records and nothing anyone could open. Every number
reached a reader as prose in `PROGRESS.md`, and the two figures the sign-off review caught were
both found by writing a query to answer one question. There is no way to look at what the store
holds for a name on a date, and the store now holds 109.6 million price bars, 771,145
`security_daily` rows and five compute tables over a five-year window.

**Phase 4 builds the screens, and the first time a screen surfaces an implausible name the
question is which cell it was ranked in, how many members that cell had, and what raw value
produced the percentile.** Building the answer before the screens exist makes phase 4
debuggable from its first checkpoint rather than after its third report. Building it afterwards
means phase 4's early findings are each preceded by an ad hoc query, which is what phase 3 did
and what this phase exists to stop repeating.

**It sits between 3 and 4 rather than inside 9 because of what it costs later.** Two of the
four panels need a figure no store carries, both of them produced inside a statement and
discarded, and both cheap to start recording now and expensive to backfill after phase 4 has
written `screen_score_daily` at roughly 280 MB per registered screen. That is the same argument
`SCREEN_LIFECYCLE.md` §9.3 makes about an index and D-92 makes about `security_daily`, arriving
for the third time.

**It is a day of work plus two range passes.** The two passes spend zero provider units.

---

## 1. What exists, and what the page has to reach around

| Panel | What is stored today | What is missing, and where it went |
|---|---|---|
| **Membership** | `security_daily` at ticker by date: sector, size bucket, market cap, `is_active`, including the departure row C01 writes when a member leaves. `security` for identity and lifespan | **The criterion that rejected a name.** C01 counts six rejections into one run log line and writes no per-ticker row [`UniverseBuilder.MembershipAsync`]. Three further criteria are applied inside `LiquidAsync` as one SQL filter, so a name failing price, dollar volume or history never reaches the counted loop at all |
| **Metrics** | The raw value and the `_pctile` beside it on all four metric tables, thirty ranked metrics [`PercentileEngine.Sources`] | **The size of the cell the percentile was ranked in.** `Windowed` computes `<metric>_cell_n` and `<metric>_bucket_n` per row inside the statement and the UPDATE keeps neither. `FallbackReportSql` aggregates them to one run log line per date and per metric, which is a total rather than a cell |
| **Inputs** | `price_daily`, `fundamental_snapshot` with `period_end`, `filing_date`, `filing_date_effective` and `filing_date_unknown_reason`, `sentiment_daily` at its sparse grain, `insider_transaction` with `transaction_code` retained | **Which filing fed which valuation column.** `valuation_daily` records the value and not its source row, and every fundamental input is resolved as of `filing_date_effective` at compute time. This is a finding, not a decision: §2's first rule says what the panel does instead |
| **Market context** | `market_context_daily`: `breadth`, `regime_label`, `sector_relative_strength` as a jsonb object keyed by sector | Nothing. `vix` is null by D-80 and is shown as absent |

**Two things the page needs already exist and are worth naming, because the alternative to each
is a second implementation.**

`Universe.AsOf` is the one statement for "the membership in force on a date", written at 3.12
after five components asked the same question five ways. It is a pure static string builder with
no dependencies and it lives in `Pipeline`, which the Api may never reference [`CLAUDE.md` §4,
`ApiIsolationTests`]. Moving it to `Core` is the whole change and it keeps one copy.

`StageData` and `DeclaredAccess` are the enforced data route, and `DeclaredAccess` already has a
name-based constructor that takes a read set and a write set without an `IStage`. That is what
makes §2's second rule mechanical rather than aspirational, and it is in `Data`, which the Api
does reference.

---

## 2. Two rules, before the first checkpoint

**Nothing on the page computes anything.** Every figure is read from a store. Where a number
would have to be derived, the page shows the inputs and says so rather than deriving it.

A second implementation of a metric is the defect phase 2 and phase 3 kept finding, and a viewer
is exactly where one creeps in unnoticed, because a viewer's wrong number looks like a display
bug rather than like a wrong number. The specific thing this forbids is the page counting a
cell's members, which would be a second implementation of C11's cell rule: the non-null count
semantics, the null-sector rule that sends a name straight to the bucket fallback, the
`LEFT JOIN` that is left on purpose, and the `is_active` filter that is deliberately not inside
the as-of pick. Four rules, each written down once, each with a reason, and every one of them
would have to be re-derived correctly by a page whose failure is silent.

**The line between a read and a computation is drawn here rather than argued per panel.**
Selecting, ordering and filtering rows is reading, including taking the most recent row at or
before a date, which is the shape every stage in this system already uses. Arithmetic over
stored values is computing, and so is a comparison whose answer the page then labels, which is
why D-107 stores which scope a percentile came from rather than leaving the page to compare a
count against a floor.

**It declares its reads like any other reader.** It touches eleven tables, so the same
conformance that holds the catalogue against the code holds this. The first thing built to
inspect the system should not be the one thing outside its own rules.

This is not a promise to be reviewed for. It reads through `IStageData` behind a
`DeclaredAccess` built from its declared read set and an **empty write set**, so reaching an
undeclared table throws before a connection opens and any write at all throws as undeclared.
That is the guard `StageRunnerTests` already proves with the trespassing-stage fixture, reused
rather than rebuilt. "No writes of any kind" becomes a property of the construction instead of
a rule someone has to keep.

**One consequence of the second rule, stated because it is easy to miss.** A reader in this
system resolves configuration as of the date it is reading for, never as of now [D-43,
INVARIANT 13]. Every threshold this page draws, the fifteen-member floor, the market cap floor,
the clean gap floor, is the value in force on the date being viewed. A page showing today's
floors beside a 2022 percentile would be answering a different question than the one asked, and
it would look right.

---

## 3. Component or view, answered

**It is a component. `C36 RecordInspector`, with a catalogue row in §3 and its reads declared
there, hosted in the Api project.**

The case for a view is real and is not the deciding one. It writes nothing, owns nothing, runs
never, and lives entirely inside the read-only surface the Api already is. Nothing about it
needs the stage contract: it takes no date-and-config-version, it is not scheduled, and it
cannot be replayed because it produces nothing to replay.

**What decides it is that §3's Reads column is the conformance surface, and a reader outside the
catalogue is a reader nothing checks.** `ReadDeclarationConformanceTests` asserts in both
directions that a component's declared `ReadSet` and its §3 Reads cell name the same tables, and
D-74's record says what the missing declaration cost the last time: a per-ticker endpoint needed
a ticker list, the catalogue named none, nothing contradicted drawing C03's pool from `security`,
the universe closed over itself and coverage froze at 679 names with no error and entirely
plausible output.

**Folding it into C30 QueryApi does not give it a checked declaration, and would degrade the
check for everything else.** C30's Reads cell is the prose "Read models and stores"; the parser
finds no table in it, C30 is not in the registry, and nothing checks it today. To check this page
through C30, that cell would have to become the union of every table any interface ever reads,
growing with each of phase 9's six screens. A declaration that is a union is one no single
reader's correctness depends on: it would be satisfied by some endpoint somewhere reading the
table, which is not the property the test exists to hold.

**The objection that it is not a stage is answered by precedent inside this system rather than
around it.** `IWriteOwner` exists precisely so the registry sees writers that are not stages,
because "a writer the registry cannot see is a writer INVARIANT 10 is not enforced against"
[`StageContracts.cs`]. RunLog, CostLedger and ConcentrationMonitor sit outside the layers and are
catalogued anyway. The read side has no equivalent and this page is the first reader that needs
one, so the phase adds it: a declared read set on a component that is not a stage, asserted by
the existing conformance test extended to see it. `StockResearcherLab.Tests` already references
the Api project, so this costs no new reference and the Api still never references Pipeline.

**0.6's bare run viewer is the precedent for not cataloguing, and the plan says where that
precedent stops.** It reads one table, through the component that owns that table's write, and
it is catalogued nowhere and checked by nothing. Eleven tables is where that stops.

**Consequences, all of them authored and none of them this phase's to write.** A §3 row for C36,
placed in the presentation block beside C30 and C31 and saying in the row that it is a reader
rather than a stage. A §15 row, because the screens table is the list of what a human can open. A
§16 consideration, which is two new stores from D-107 and D-108 and nothing from C36 itself,
which persists nothing. All are in §9.

---

## 4. Decisions that need authoring before code

Three. Each is authored content under `CLAUDE.md` §13 and none is the build session's to write.
Clauses are in `CLAUDE.md` §15's amendment format so they paste into a build prompt, and each
names the checkpoints it blocks. Numbering continues from D-106, the last decision in the
register.

### 4.1 The cell a percentile was ranked in

Blocks 3.5.2. The largest item in the phase.

```
D-107 The population a percentile was ranked against is stored per cell, not
recomputed and not discarded. ACTIVE

C11 computes each metric's non-null population in its (size_bucket, sector) cell
and in its bucket inside one window function, uses both to pick a scope, writes
the percentile and keeps neither count. What survives is one run log line per
date aggregating every cell into a total.

A percentile without the size of the population behind it is unreadable. One
over three members and one over eighty are the same number on the page, and the
fifteen-member fallback is theoretical rather than visible. This is D-92's
argument arriving one table over and in its own words: a percentile computed
over a cell nothing downstream can see is not inspectable afterwards.

Recomputing it in a reader was weighed and is rejected. The count is not
count(*) over the cell's members; it is the non-null count of that metric in
that cell, under C11's null-sector rule, its LEFT JOIN and its is_active
handling. A reader reproducing those four rules is a second implementation of
the cell rule whose failure is a plausible number.

percentile_cell_daily is date by size_bucket by sector by metric, written by
PercentileEngine. The grain is the cell, not the row: the population is a
property of the cell and one row per member per metric would restate it
thousands of times over on the two largest tables in the store.

Each row carries cell_members, bucket_members, min_members as the floor in force
on that date, and ranked_scope over cell, bucket and none. bucket_members
repeats across the sectors of a bucket, which is a redundancy accepted so that a
reader takes one row per metric rather than two. ranked_scope is stored rather
than derived so no reader compares a count against a floor and labels the
result.

A name whose sector is null forms no cell and goes straight to the bucket
fallback [METRICS.md section 6.4], so it needs a row with no sector. How that is
keyed is the migration's to settle and the DoD names it.

Metric names are distinct across the four source tables today, which is what
makes metric sufficient in the key without the table beside it. source_table is
carried as a column so a reader knows where to look, and a future collision is a
failed key rather than a silent overwrite.

The pass that populates history records the range it has covered, so a reader
distinguishes a date nothing has reached yet from a cell that does not exist.
That is D-106's rule applied to a date-partitioned pass rather than a
ticker-partitioned one: the marker records coverage, not the invocation. Running
date-descending makes the covered set a contiguous suffix, so one covered-from
date carries it.

DoD: migration 0014 creates percentile_cell_daily; SCHEMA.md gains its section
with PercentileEngine as declared writer and ARCHITECTURE.html section 16 gains
its store row in the same checkpoint, since StoreMatrixConformanceTests holds
that list in both directions; the counts written for one date reproduce what
FallbackReportSql reports for that date, asserted as a test rather than
inspected; a metric whose cell fell back carries ranked_scope 'bucket' and a
metric nothing ranked carries 'none'; a name with a null sector reads its bucket
row; the coverage marker advances with the pass and a date beyond it is
distinguishable from a date with no cell; guards.ps1 check 4 passes with
ExpectedMonetary unchanged, asserted rather than assumed, no column here being
money.
```

**The range pass is the cost and it is stated rather than discovered.** History has to be
populated or the panel answers only for dates after this phase. That is one C11 range pass over
the window, which is four statements per date over 1,260 dates against the date-leading indexes
migration 0007 added, on a store where the whole compute rebuild measured 121.30 minutes
[`PROGRESS.md`, 2026-08-22]. It spends no provider units, it is a re-run of a compute stage and
therefore replayable by construction, and it is cheaper now than after phase 4 writes
`screen_score_daily`. The figure is measured at 3.5.2 and recorded, not estimated here.

### 4.2 The criterion a name was rejected on

Blocks 3.5.1's rejected half.

```
D-108 C01 records the criterion that rejected a name, per ticker per evaluation
date. ACTIVE

"Why is this obvious company not in my universe" is the question the membership
panel exists for, and it is the half that currently takes a query. The admitted
case is the one whose answer is already known.

C01 counts six rejections into a run log line and writes no per-ticker row, so
the answer for one name is not recoverable from the record at all. Three further
criteria, minimum price, minimum median dollar volume and minimum history, are
applied together inside one SQL filter in LiquidAsync, so a name failing any of
them is absent from the counted loop and absent from the counts.

Reconstructing it later does not work and the reason is D-92's. The clean gap
count is computed as of the date and never stored [M.1], market capitalisation
is computed from a share count readable on that date and is stored only for
members, and instrument type comes from a provider symbol list that is stored
nowhere. Three of the nine criteria have no persisted input at all.

universe_rejection is ticker by evaluation date, written by UniverseBuilder, one
column: criterion, NOT NULL with a CHECK over the enumerated values. The
constraint is in the database for the same reason regime_label's is: this column
segments every count taken off it, and a drifted value would land in its own
bucket in every segmentation without ever erroring.

One criterion per row, being the one that rejected, not every criterion the name
would have failed. D-4 is a conjunction and a name fails on the first criterion
it fails; recording all nine would suggest an independent evaluation the code
does not perform. What the column therefore means is the criterion the
evaluation stopped on, which is a property of C01's order rather than of D-4,
and that is stated in SCHEMA.md so a later reader does not read it as the only
criterion the name failed.

The three pre-pass criteria are projected rather than filtered, so the statement
classifies instead of dropping. That widens what LiquidAsync returns, and
MembershipAsync consumes its result as already filtered: the loop applies six
further criteria and admits whatever survives them, never re-testing price,
dollar volume or history. Projected without a corresponding first test, the loop
would therefore admit names D-4 excludes, which is a change to C01's membership
rather than an addition beside it. So the pre-pass verdict travels with each row
and the loop rejects on it before every other criterion, and the admitted set is
unchanged by construction rather than by inspection.

The containment is exact and is stated rather than assumed. LiquidAsync is
private with one caller, MembershipAsync, which has two of its own, the nightly
path and the range path, and both take the same Day. Nothing else in the
codebase reads the statement. universe.min_price, universe.min_adv_20d and
universe.min_history_days each carry FundamentalsIngestor as a second consumer,
and that is C03's own pool statement rather than a call into this one, which is
what D-102 means by the two pool statements: C03 is untouched here.

The population is bounded by backfill.window_start, the key C01 already resolves
and already bounds ListingAsync by, for the reason stated there: a name that
stopped trading before the window can never be a member on any evaluated date.
One population rule rather than two, so the store and the listing cannot drift
apart. What that inherits is stated: if the window start moves, the store's
population moves with it, which is correct, a date outside the window not being
one C01 evaluates. The unbounded alternative is every ticker with a bar at or
before the date, roughly 50,785 names across 260 weekly evaluation dates and
about 13 million rows to record that a ticker has not traded since 2019. A
tighter alternative is a new trailing-recency key, which is smaller again and
introduces a second population rule and a key whose only consumer is this write;
it is rejected on that rather than on size.

DoD: migration 0015 creates universe_rejection; SCHEMA.md gains its section with
UniverseBuilder as declared writer, its enumerated criterion values and the
sentence about what the column means; ARCHITECTURE.html section 16 gains its
store row and section 3's C01 Writes cell gains the table, both in the same
checkpoint as the migration; C01's write set declares it and
WriteOwnershipConformanceTests passes unchanged; the six loop criteria and the
three pre-pass criteria are each exercised by a test against a name constructed
to fail exactly that one; security_daily for a spot-checked evaluation date is
identical row for row before and after the projection change, asserted as a test
rather than compared by hand, because that is the assertion standing between an
addition and a membership change; a name that is a member on a date has no row
and a name that is rejected has exactly one; a re-run of one evaluation date
produces the identical rows.
```

**A C01 pass over the window is the cost here and it is smaller than D-107's.** C01 runs weekly,
so the window is about 260 evaluation dates rather than 1,260 trading ones, and 3.11 already ran
exactly this shape to fill `security_daily`. Zero provider units, since the symbol list is one
call per date and the rest is local.

### 4.3 A reader that is not a stage still declares its reads

Blocks 3.5.1.

```
D-109 A component that reads stores and writes nothing declares its read set as
data, and the catalogue conformance holds it in both directions. ACTIVE

Write ownership has been enforced through the registry since 0.4 and read
declarations since D-74, and both mechanisms see only components the pipeline
registry holds. Every reader in this system has so far also been a writer, so
nothing has needed the distinction. RecordInspector is the first reader that is
not, and the read-only query surface is where more of them will appear.

IWriteOwner exists because a writer the registry cannot see is a writer
INVARIANT 10 is not enforced against. The same sentence holds with reader
substituted, and this decision states the reader half rather than leaving the
first one outside the net.

So a read owner declares Name and ReadSet, is named in ARCHITECTURE.html section
3 like every other component, and reaches data through IStageData behind a
DeclaredAccess built from that read set and an empty write set. The empty write
set is the read-only guarantee made structural: any write at all throws as
undeclared before a connection opens, which is the same guard rather than a
second one.

It is hosted in the Api project and the Api still never references Pipeline
[CLAUDE.md section 4], so no page can invoke a stage. The test project already
references the Api, so the conformance test sees the declaration without any new
project reference and without the Api gaining one.

DoD: ReadDeclarationConformanceTests covers C36 in both directions and its
CataloguedComponents constant moves from 35 to 36, asserted rather than
adjusted; a fabricated reader disagreeing with its cell fails each direction and
leaves the other silent, registered in FIXTURES.md; a write attempted through
the inspector's data route throws UndeclaredTableAccessException before a
connection opens; ApiIsolationTests passes unchanged in all three of its
assertions.
```

---

## 5. Checkpoints

Four, one commit each, `Phase 3.5 / 3.5.n - what it did`. Run `ci.ps1` per checkpoint, which is
the per-checkpoint verification since 1.12 and the only thing that cannot reach the test step off
a stale binary.

They are ordered so the first is openable on its own: the page exists, takes a ticker and a date,
and answers something at the end of 3.5.1. Each later checkpoint adds one panel to a page that
already opens, so a checkpoint that slips leaves a working page rather than a half-built one.

**That holds for 3.5.3 and 3.5.4 and does not hold for 3.5.2, which is stated here rather than
covered by the sentence above.** Those two read stores that are already complete, so their panels
answer the moment they are built. 3.5.2's panel answers nothing for a historical date until its
range pass has reached that date, and the pass is the largest single piece of work in the phase.
What the panel shows in between is in the checkpoint.

### 3.5.1 Membership, and the page that opens

Blocked on D-108 and D-109. The route, the ticker and date input, the C36 read declaration and
its conformance, `Universe.AsOf` moved to `Core` so one statement answers "in force on this date"
for both the pipeline and the page, and the membership panel.

Rejected names are the half that matters, and D-108 is what makes them answerable. Without it the
panel can say a name has no row and nothing about why, which is the state the phase exists to
leave behind.

*Done when:* the page opens at a route taking a ticker and a date and renders without either;
a member on that date shows its size bucket, sector, market cap and the evaluation date the row in
force was written on; a name that departed shows the `is_active = false` row and its date rather
than an absence; a rejected name shows the criterion from `universe_rejection` and the threshold
in force on that date beside it; every threshold on the panel resolves as of the viewed date,
asserted by a test over two config versions either side of the date [INVARIANT 13]; **the
membership C01 writes is unchanged by the projection, asserted as `security_daily` identical row
for row for a spot-checked evaluation date either side of the change**; the widened statement's
wall clock is recorded against `universe.pool_statement_timeout_seconds`; the read declaration
fails the conformance test in both directions against a fabricated reader; a write through the
inspector's data route throws before a connection opens.

### 3.5.2 Metrics, with the cell beside the percentile

Blocked on D-107. Migration `0014`, C11's write, the panel, and then the range pass over the
window.

**The third figure is the point of the panel and the done-when names its column.** A percentile
over three members and one over eighty are the same number without it, and the fifteen-member
fallback is theoretical until a reader can see the cell it fired on.

**The checkpoint is built, then populated, and the page is open across the gap between them.**
For every date the pass has not reached, the panel shows the raw value and the percentile, which
are already stored, and shows the three cell figures as absent with the reason: not populated for
this date. **That is a different state from a cell that does not exist**, which is what a name
with a null sector reads, and the two must not render alike. The distinction comes off the
coverage marker D-107 requires rather than off an empty result, because an empty result is what
both of them look like.

**The pass runs date-descending, so recent dates answer first.** Each date's cell rows are a
closed question over that date's rows and depend on no other date, so the order changes nothing
about the output; the ranking `UPDATE` at 3.15 runs ascending and this is a separate pass writing
a separate table. A panel that answers for last week while the backfill walks is worth more than
one that answers for nothing until it finishes, and descending also makes coverage a contiguous
suffix that a single date records.

*Done when:* for a named ticker and date the pass has reached, each of the thirty ranked metrics
shows its raw value, its `_pctile`, and `percentile_cell_daily.cell_members` for the cell it was
ranked in, with `bucket_members` and `min_members` beside them; `ranked_scope` says which
population the percentile came from and the page compares nothing to establish it; a metric whose
value is null reads absent and not zero [`CLAUDE.md` §6]; a name whose sector is null shows its
bucket row and says it formed no cell; **a date beyond the coverage marker reads not populated
rather than blank, and a test asserts it renders differently from a cell that does not exist**;
the counts written for a spot-checked date reproduce what `FallbackReportSql` reports for that
date, asserted as a test; the pass covers the window and its wall clock is recorded in
`PROGRESS.md` against the 121.30 minutes the whole compute rebuild took; `ExpectedMonetary` is
unchanged and asserted.

### 3.5.3 Inputs

The panel opened when a number looks wrong. Recent price bars, every filing readable on the date,
the sentiment days in the window, the insider filings inside the window.

**The windows are the components' own windows, read from the same config keys and resolved as of
the viewed date**, not new keys and not literals. A panel showing a 90-day insider window while
C34 reads a different one would be a second definition of the window, which is the first rule in
its other form.

*Done when:* the recent bars render with the window stated; every `fundamental_snapshot` row with
`filing_date_effective` at or before the date shows `period_end`, `filing_date`,
`filing_date_effective` and `filing_date_unknown_reason`, ordered by effective date, with the most
recent readable row marked and a line stating that which valuation columns it fed is not recorded
[§7]; no row is keyed or ordered on `period_end` anywhere on the panel [INVARIANT 12]; the
sentiment panel shows only the days that carry a row and does not zero-fill the rest; insider
filings show `transaction_code` as stored; a name with no filings, no sentiment or no insider
rows shows an empty panel that says absent rather than an error or a zero.

### 3.5.4 Market context

One line, and the cheapest panel in the phase, which is why it is last.

*Done when:* breadth, `regime_label` and the sector composite for the name's sector on that date
render on one line; the composite is read out of `market_context_daily.sector_relative_strength`
by key rather than recomputed from any price series; `vix` shows as absent rather than blank or
zero [D-80]; a date with no `market_context_daily` row says so instead of rendering an empty
line.

---

## 6. Definition of done

1. **One route answers the question.** A ticker and a date in, four panels out, on a real store
   for a real name on a historical date and on the most recent one.
2. **Every figure on the page is read from a store.** Held structurally rather than by review:
   the read set is declared, `DeclaredAccess` throws on anything outside it, the write set is
   empty so a write throws too, and the Api carries no Pipeline dependency in its compiled
   closure.
3. **The metrics panel shows the cell population beside every percentile**, over a window that is
   populated, with the fallback visible where it fired.
4. **The membership panel names the criterion for a rejected name**, for each of the nine
   criteria, exercised by a test per criterion.
5. **The catalogue and the code agree about what this reader reads**, in both directions, with
   the failure exercised against a fabricated reader.

**Invariants at risk:** 10, 12 and 13. INVARIANT 10 because D-107 and D-108 each add a table
whose writer is declared in `SCHEMA.md` in the same checkpoint as its migration. INVARIANT 12
because a viewer keying filings on `period_end` would teach every reader the wrong key while
looking correct. INVARIANT 13 because a page resolving today's thresholds against a 2022 date
answers a different question and looks right doing it. **16 is asserted unchanged**, neither new
table carrying a monetary column.

---

## 7. Findings to report, not to resolve

**Which filing fed which valuation column is not recorded, and this phase does not close it.**
`valuation_daily` stores the value and not its source row, and every fundamental input is resolved
as of `filing_date_effective` at compute time. A per-column provenance store is one row per
ticker per date per column on a 540 MB table, which is a large cost for a question the Inputs
panel answers adequately by showing every readable filing with its effective date and marking the
most recent. Recorded so that a later reader does not read the absence as an oversight.

**The bare run viewer from 0.6 is in no document.** `BUILD_PLAN.md` asked for it and it exists at
`Ui/Components/Pages/Runs.razor` behind `/api/runs`, and `ARCHITECTURE.html` §15's screens table
does not have it. Reported rather than edited, §15 being authored prose. It is also the reason
§3's precedent question in §3 above had two answers available.

**`ARCHITECTURE.html` §15 says "Six screens" over a table of seven.** U7 was added and the
sentence was not. Reported.

**The pre-pass classification has two costs and only the second is a finding.** The first is a
behaviour change to C01's membership, because `MembershipAsync` consumes the statement's output as
already filtered, and it is answered inside D-108 rather than reported here: the verdict travels
with the row, the loop rejects on it first, and a test asserts `security_daily` is identical
either side of the change. The second is the statement's wall clock, which is not knowable from
here. `LiquidAsync` today filters three criteria away inside one statement over every ticker with
a bar at or before the date, and `universe.pool_statement_timeout_seconds` exists at 1800 because
this statement has been slow before [D-102]. 3.5.1 measures it and records the figure rather than
predicting it.

**Two panels are worth the phase and two are nearly free, and that asymmetry is deliberate.**
Inputs and market context read stores that are already complete. Membership and metrics are the
two that need a store to exist, and they are the two the phase is for.

---

## 8. What phase 3.5 does not do

**No run health, no pipeline status, no dashboard of what executed.** `run_log` answers that, U6
is where it belongs in phase 9, and nobody opens a dashboard twice. The reason this page is worth
a day is that it answers a question asked repeatedly, and a page that also does monitoring is a
page that does neither.

**No writes of any kind from the page.** Structural, not a rule: the empty write set makes any
write throw as undeclared.

**No screens, no scores, no candidates.** Phase 4 owns those, and nothing here reads
`screen_score_daily`, `candidate_set` or `attribution`, none of which has a row yet.

**No changes to any metric definition.** The two writes this phase adds record what C11 and C01
already compute. Neither changes a number that already exists, which is what keeps history
poolable across the boundary [`CLAUDE.md` §12].

**No measurement added to its own scope** [`CLAUDE.md` §3]. Every figure in §6 is a query against
a table this phase populates or a wall clock on a pass it runs.

---

## 9. Authored items owed, and files

**Authored by a human, blocking.** None of these is the build session's to write, and the
checkpoint each blocks is named beside it.

| Item | Document | Blocks |
|---|---|---|
| D-107, the cell population store | `DECISIONS.md`, a new section or the Backfill section extended | 3.5.2 |
| D-108, the rejection criterion store | `DECISIONS.md` | 3.5.1 |
| D-109, a reader that is not a stage declares its reads | `DECISIONS.md` | 3.5.1 |
| The `C36 RecordInspector` catalogue row, in the presentation block beside C30 and C31, its Reads cell naming the eleven tables and the row saying it is a reader rather than a stage | `ARCHITECTURE.html` §3 | 3.5.1 |
| C01's Writes cell gains `universe_rejection` | `ARCHITECTURE.html` §3 | 3.5.1 |
| C11's Writes cell gains `percentile_cell_daily` | `ARCHITECTURE.html` §3 | 3.5.2 |
| Two store rows, `percentile_cell_daily` and `universe_rejection`, each in the same checkpoint as its migration because `StoreMatrixConformanceTests` holds the list in both directions | `ARCHITECTURE.html` §16 | 3.5.2, 3.5.1 |
| A screens-table row for the inspector, and the "Six screens" sentence reconciled with the table | `ARCHITECTURE.html` §15 | 3.5.1 |
| Two table sections, with the declared writer, the enumerated `criterion` values, and the sentence saying `criterion` means the one the evaluation stopped on | `SCHEMA.md` | 3.5.1, 3.5.2 |
| The phase 3.5 section: scope, done-when, invariants at risk, the checkpoint table below, and "Eleven phases" becoming twelve | `BUILD_PLAN.md` | all |
| The commit convention for a fractional phase, `Phase 3.5 / 3.5.n - what it did` | `BUILD_PLAN.md`, checkpoints and commits | all |

**The checkpoint table, to paste into `BUILD_PLAN.md` under the new phase.**

| # | Scope |
|---|---|
| 3.5.1 | Membership, and the page that opens. The route, the ticker and date input, `C36 RecordInspector`'s declared read set and its conformance, `Universe.AsOf` moved to `Core` so one statement answers "in force on this date" for both the pipeline and the page, migration `0015` for `universe_rejection`, and C01 recording the criterion that rejected rather than counting six into a run log line. **The three pre-pass criteria are classified rather than filtered away, and the admitted set is unchanged by construction and asserted**, `MembershipAsync` consuming the statement as already filtered. Blocked on D-108 and D-109 |
| 3.5.2 | Metrics, with the cell beside the percentile. Migration `0014` for `percentile_cell_daily` at date by size bucket by sector by metric, C11 writing the population it already computes and currently discards, the panel showing raw value, percentile and cell population together, and then the pass that populates the window. **The pass runs date-descending and records its coverage**, so a date it has not reached reads not populated rather than blank, which is a different state from a cell that does not exist. Blocked on D-107 |
| 3.5.3 | Inputs. Recent bars, every filing readable on the date with its effective date and unknown reason, the sentiment days that carry a row, and the insider filings inside the window. The windows are the components' own keys resolved as of the viewed date, not new keys and not literals |
| 3.5.4 | Market context. Breadth, regime label and the name's sector composite on one line, read out of `market_context_daily` by key |

**New.** `src/StockResearcherLab.Data/Migrations/0014_percentile_cell_daily.sql`;
`src/StockResearcherLab.Data/Migrations/0015_universe_rejection.sql`;
`src/StockResearcherLab.Core/Stages/IReadOwner.cs`;
`src/StockResearcherLab.Core/Universe.cs`, moved rather than copied;
`src/StockResearcherLab.Api/RecordInspector.cs` and its contracts;
`src/StockResearcherLab.Ui/Components/Pages/Record.razor`.

**Changed.** `PercentileEngine` and `UniverseBuilder`, each gaining one write;
`ReadDeclarationConformanceTests`, to see a reader that is not a stage and to move
`CataloguedComponents` from 35 to 36; `Api/Program.cs`; and the record, `PROGRESS.md`,
`FIXTURES.md`, `CONFIG_REFERENCE.md`, `CHANGELOG.md` for any prior wording a clean edit removes
[D-73].

**Written by the build, not authored.** `PROGRESS.md` gains a phase 3.5 row in the status table
and the two range passes' measured wall clocks; `FIXTURES.md` gains the fabricated-reader
fixture and the per-criterion rejection fixtures; `CONFIG_REFERENCE.md` gains a Consumer entry
naming `RecordInspector` for every key the page resolves, filled from the composition code rather
than from the key name.

---

## 10. Verification

**Per checkpoint:** `ci.ps1`, which runs guards, restore, build, the secrets check, migrate twice
and the test suite, and exits non-zero on the first failure.

**Per checkpoint, beyond CI:** the page opened on a real store for a real name and a real date,
which is the only verification a viewer has that a test does not give it. 3.5.1 on a member, a
departed name and a rejected name. 3.5.2 on a metric that ranked in its cell and one that fell
back to its bucket. 3.5.3 on a name with a substituted filing date and one with none. 3.5.4 on a
date inside the 2022 drawdown, where the regime label should not read `risk_on`.

**Structural, and asserted rather than reviewed:** the read declaration in both directions;
`ApiIsolationTests` unchanged in all three assertions; a write through the inspector's data route
throwing before a connection opens; `WriteOwnershipConformanceTests` and
`StoreMatrixConformanceTests` passing against the two new tables and their catalogue rows.

**At sign-off:** the five done-when lines run and recorded in `PROGRESS.md`, then a review in a
session that did not build the phase, asking `BUILD_PLAN.md`'s three questions and correcting
nothing.
