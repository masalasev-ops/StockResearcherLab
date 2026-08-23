# Phase 4 — Screens and candidate selection

**Target** phase 4, the gate engine, the screen engine with its five screens as config rows,
the floors, the candidate allocator, the attribution write, the concentration monitor, and the
screen backfill.

**Authored** 2026-08-23, at phase 3.5's sign-off, as `BUILD_PLAN.md` requires of the next
phase's detail.

**Read against** `ARCHITECTURE.html` §03, §05, §06, §10, §16, §18, §19 and §20, `SCHEMA.md`,
`SCREEN_LIFECYCLE.md` §1 to §10, `METRICS.md` §6 and §8, `DECISIONS.md` D-5 to D-14, D-40,
D-42, D-43, D-58, D-69, D-84 to D-90 and D-107 to D-109, `CONFIG_REFERENCE.md`,
`WORKED_EXAMPLE.md`, `VALIDITY.md` §3 and §4, and `CLAUDE.md` §2, §3, §5, §6, §9, §11 and
§13, at HEAD `f233e88`. Nine claims below rest on the code rather than on a document and were
read there: `PercentileEngine.Sources` and its statement shape, `StageRegistry`,
`DeclaredAccess` with `UndeclaredTableAccessException`, `Universe.AsOf` and
`Universe.MembersAsOf`, `NightlyRun.EveningOrder`, `BackfillSequence.SourceOrder`,
`ArchitectureDocument`'s `code` element parse, `ConfigSeeder.Keys`, and the six table
definitions in `0001_snapshot.sql` [`CLAUDE.md` §7].

**Status** `DRAFT`, which is the only value this corpus uses for a plan. The numbered
checkpoints in §6 are phase scope and are authored into `BUILD_PLAN.md` by a human rather than
from here, as are the eleven decision clauses in §4 [`CLAUDE.md` §13].

**This plan is not archived to `prompts/spent/` yet, and archiving it now would be the wrong
thing.** Nothing has been issued from it, none of §4's eleven decisions is authored, and no
checkpoint has landed, so there is no record of what was asked to keep. It goes to
`prompts/spent/` **as implemented**, which is `CLAUDE.md` §3's wording and is what phase 3's
archive is: the same document carrying, in its header, that D-92 to D-95 were authored and
that the eighteen checkpoints reached `BUILD_PLAN.md`. Phase 2's archive is the other shape,
the prompt issued to the build session at `Status: SPENT`. §8 carries the drift that made this
worth stating.

---

## Context

**This is the phase where you find out whether the idea works, and it is the last one before
money is spent on models** [`BUILD_PLAN.md` phase 4]. It is also the phase with the largest
unauthored surface in this corpus, and that is the fact the rest of this document is organised
around.

**How a screen score is computed is nowhere.** `ARCHITECTURE.html` §05 gives each screen a
list of metrics it ranks on and says nothing about how those become the single `score` column
`screen_score_daily` carries. That is deliberate rather than missing: `METRICS.md` §8 assigns
it here, in terms, saying "Screen scores, floors, weights and the direction each screen reads a
metric in. Those are `screens.*` config rows and phase 4 authors them". The same document
assigns the boolean question here too, at §6.5: "How a boolean enters a screen score is phase
4's to author".

**The gate engine is thirteen words.** `ARCHITECTURE.html` §03 gives C12 "Earnings blackout,
gap, halt, already held, cooldown" and there is no threshold for any of them anywhere in the
corpus, no `gates.*` namespace in `CONFIG_REFERENCE.md`, and no statement of whether a gated
name is excluded or flagged.

**Two decisions owed to this phase are still `OPEN`.** D-69, whether S4 survives an
unbackfillable institutional ownership source, which the carried obligations say must be
decided before screen floors are drawn. And D-90, whether post-earnings drift is registered,
which §5 of this document places at the phase's sign-off rather than at its start.

**Nothing here is a new table.** `screen_score_daily`, `screen_history`, `candidate_set`,
`attribution`, `gate_result` and `alert` were all created by `0001_snapshot.sql` and all hold
zero rows. What this phase adds to the schema is one column, one view, one `jsonb` shape and
one partitioning change, and every one of them is free now and expensive later.

**And phase 3.5 was built for this phase's first bad night.** `C36 RecordInspector` shows,
for one ticker on one date, every ranked metric as raw value, percentile and the population of
the cell it was ranked in. When a screen surfaces a name nobody would buy, every input to that
name's score is already on one page, resolved as of that date, and the score is hand
recomputable from it without writing a query.

**The size of the work is one migration, four components, a config shape, and two range
passes.** §3 prices the range passes. They spend zero provider units.

---

## 1. What exists, and what phase 4 has to reach around

| Component | What is stored today | What is missing, and where it went |
|---|---|---|
| **C12 GateEngine** | `gate_result` at ticker by day with `passed` and `reasons text[]`, and `SCHEMA.md` saying it records every failing reason rather than the first | **Every threshold.** Five gates named in thirteen words with no width, no percentage and no day count anywhere in the corpus, and no `gates.*` section in `CONFIG_REFERENCE.md`. Also missing: whether a gated name is excluded or flagged, though C14 rather than C13 reading `gate_result` settles where exclusion happens |
| **C13 ScreenEngine** | Thirty ranked metrics carrying `_pctile` columns over four tables [`PercentileEngine.Sources`], `percentile_cell_daily` at 1,858,932 rows over 1,457 dates giving each percentile its cell population [3.5.2], and `screen_score_daily` and `screen_history` empty and waiting | **The composition.** No document says how a metric list becomes one score, how direction is carried, how a boolean enters, or what happens when an input is null. `screens.<id>.metrics` is a key with a stated consumer and no stated shape [`CONFIG_REFERENCE.md:326`] |
| **C14 CandidateAllocator** | `candidate_set` and `attribution` empty, `attribution` carrying `screens_surfacing`, `score_per_screen`, `size_bucket`, `sector`, `regime`, `gate_state`, `config_version` and nine return columns [`0001_snapshot.sql:270-290`] | **`surfaced_as`, which does not exist as a column**, and `score_per_screen`'s rank, the `jsonb` being flat today [D-85, `SCREEN_LIFECYCLE.md` §4.3, §4.4]. Also the quota arithmetic above eight slots, which D-89 restates as a proportion and `CONFIG_REFERENCE.md` still carries as three fixed keys |
| **C28 ConcentrationMonitor** | `alert` empty, with ConcentrationMonitor as its declared writer. `security_daily` at ticker by date carrying the point-in-time size bucket [D-92, 3.11] | **Nothing structural**, and one wrong table in its Reads cell. §2's third rule and §8's B3 have it |

**Four things phase 4 reuses rather than rebuilds, named because the alternative to each is a
second implementation.**

`PercentileEngine` is the shape a date-partitioned set-based stage takes here: one statement
per source table over the whole date, `IStage` and `IBackfillStage` together, and the metric
list as data rather than as code. C13 is the same shape with one statement per screen.

`Universe.AsOf` and `Universe.MembersAsOf` in `Core` are the one statement for the membership
in force on a date, written at 3.12 after five components asked it five ways and moved to
`Core` at 3.5.1. C13's population is `MembersAsOf` and not a join it writes itself.

`BackfillSequence` with its `SourceOrder`, and `BackfillRun` underneath it, are phase 3's range
harness. The screen backfill is a second constant in that file rather than a second driver.

`DeclaredAccess` throws `UndeclaredTableAccessException` before a connection opens when a stage
touches a table outside its declared set, and it is what §4's D-120 leans on to make INVARIANT
2 structural rather than remembered.

**Three facts about the store that a phase 4 reader will meet and should not read as broken.**

`ev_ebit_vs_own_5y` and `dist_52w_high_20d_change` compute for no name at the start of the
window and become computable as it deepens, the first needing 24 month-end samples over five
years and the second 272 trading dates. Both are ranking inputs, the first to S1 and the second
to S2 [`BUILD_PLAN.md` carried obligations, 2 to 3].

`position` and `trade_outcome` hold no rows and will not until phase 7, and two of C12's five
gates read them. "Already held" and "cooldown" are structurally inert for the whole of this
phase and for the whole of the backfill window.

The earnings blackout is a third inert gate over history rather than a working one.
`events.earnings_backward_days` is 7, so the events store reaches seven days into the past, and
`BackfillSequence.SourceOrder` records that earnings are deliberately not backfilled. A
blackout evaluated over a date whose earnings calendar was never ingested is not a gate that
passed. §4's D-117 is what stops that reading like one.

---

## 2. Three rules, before the first checkpoint

**Every screen scores every active member, and a gated name is scored like any other.** The
floor is the 98th percentile of the screen's own trailing distribution, and the distribution
cannot be known without scoring everyone [D-9, INVARIANT 1]. There is a second reason and it is
the sharper one: a floor drawn over the ungated subset moves when a position opens or a
cooldown expires, which makes a screen's floor a function of the portfolio. So C13 does not
read `gate_result` and must not, C14 does, and exclusion happens at allocation. §03's Reads
cells already say exactly this and the reason is worth stating beside them.

**S5's gate reads percentile columns and never another screen's score** [INVARIANT 2, D-6].
`ARCHITECTURE.html` §05 states the gate as "S1 top quintile AND technical bottom quintile AND
stabilising", which read literally is one screen reading another. `WORKED_EXAMPLE.md` §3
settles what it means: the two gates traced there are a **quality composite percentile** of 84
and a **technical composite percentile** of 12, both composed for S5 from the percentile store.
So S5 carries its own metric lists, they duplicate S1's content, and **the duplication is
deliberate**. Removing it is what the invariant forbids, in the invariant's own words: sharing
a computed value between screens looks like removing duplication and destroys the independence
the design rests on.

**A null metric is unknown and never scores as a zero or as a bottom rank** [`CLAUDE.md` §6].
This is the rule with the most consequence attached, because a great deal of this store is
legitimately absent: `fcf_yield` computed for 464 of 5,713 valuation rows before the rotation
was lifted, and the three derived sentiment metrics for 453 of 2,841 names at the phase 2
measure. A screen that reads absence as the worst value deletes precisely the thinly covered
small caps its small slots exist to find, and the megacap tilt this whole design is arranged
against returns through the arithmetic rather than through a ranker. §4's D-112 is where that
rule becomes a formula.

---

## 3. The scope question: how much of the range this phase runs

**This is not two documents disagreeing. It is a reading of the phase's own size, and it is
put before the decisions because it changes the shape of the phase rather than one of its
checkpoints.**

The scope prose says "backfill screen scores over the five years using the two-pass approach".
The done-when asks that megacap share sit under a third **including inside the 2022 drawdown**,
that distinct tickers over any 60-day window exceed 250, and that overlap between screens fall
near 10 to 20 percent. **Those are properties of `candidate_set`, not of `screen_score_daily`.**
On the plain reading of the done-when, C12 and C14 run over the range too and `attribution` is
populated over history, which is what `SCREEN_LIFECYCLE.md` §9.2 assumes when it prices the
table's row count after the backfill.

**The reading is priced rather than called roughly double.** The two measured range passes it
is estimated against, both at zero provider units: **C01 over 292 evaluation dates in 14.94
minutes** and **C11 over 1,462 trading dates in 48.54 minutes**, writing 26,834,277 rows
[`PROGRESS.md`, phase 3.5 sign-off]. The window is 2021-01-04 to 2026-08-12, **1,462 sessions**,
at 2,433 to 2,864 active members per evaluation date [`PROGRESS.md` measured figures].

**Every figure in the table is an estimate and is marked as one. Nothing in phase 4 has run.**

| Pass | Dates | Rows | Estimate | Derived from |
|---|---|---|---|---|
| C13 pass one, raw scores | 1,462 | ~19.7M at five screens | **45 to 70 min** | C11 over the same 1,462 dates writing 26.8M rows. Fewer rows, five statements a date against C11's four, and reading percentile columns already computed rather than computing them |
| C13 pass two, floors and ranks | 1 | in place | **5 to 15 min** | One window function over the complete table, so a sequential scan and a sort rather than a per-date loop [`ARCHITECTURE.html` §19] |
| C12 GateEngine | ~1,212 | ~3.3M | **15 to 30 min** | C01's per-date cost scaled from 292 dates to 1,212, on a simpler statement. Only dates carrying a floor need it |
| C14 CandidateAllocator | ~1,212 | ~34k and ~34k | **5 to 15 min** | Trivial row counts against 1,212 per-date statements, so statement overhead dominates |
| C28 ConcentrationMonitor | ~1,212 | tens | **1 to 5 min** | Rolling windows over a 34k-row table |
| **Total, wide reading** | | **~23M** | **70 to 135 min** | Against the measured whole-compute rebuild of **121.30 minutes** [`PROGRESS.md`, 3.16] |

**What doubles is what becomes immutable, not the wall clock, and that is the correction the
word "roughly" was hiding.** The incremental cost of the wide reading over the narrow one is
C12, C14 and C28, which is 21 to 50 minutes on top of C13's 50 to 85: plus 30 to 60 percent of
the range run rather than plus 100. What actually doubles is the phase's output, because
`attribution` is a second store and it cannot be rewritten afterwards [INVARIANT 4, D-40,
`RUNBOOK.md`]. Storage adds roughly 1.4 GB for `screen_score_daily` at five screens and about
18 MB for `candidate_set` and `attribution` together, both estimates [`SCHEMA.md` §16,
`SCREEN_LIFECYCLE.md` §9.2].

**What each reading gives, and what it hands forward.**

- **Wide.** 4.13 fills `screen_score_daily` over the range and 4.14 runs the gate and the
  allocator over it, producing candidate and attribution history from the first date a floor
  exists, around 2022-01-03. All six done-when criteria are measurable at sign-off. **The
  fourteen checkpoints in §6 assume this reading.**
- **Narrow.** 4.13 is the phase's last range run, 4.14 shrinks to a nightly wiring and one live
  night, and three of the six done-when criteria have no population to be measured against
  until enough live nights accumulate. What it hands forward is not only those three: **the
  point at which the attribution record starts leaves phase 4**, so a later phase inherits an
  authored decision this one was going to take, namely when the record starts and whether the
  historical dates ever receive attribution rows at all. That question gets harder rather than
  easier once live rows exist beside the gap. D-117's backfill half also goes unexercised,
  `gate_state` reading `passed_partial` being a rule with no row to test it against until a
  range run happens.

**The wide reading is recommended and not taken**, on the grounds that a done-when line the
phase cannot evaluate is not a definition of done, and that deferring the attribution write
moves a decision rather than removing one [`CLAUDE.md` §13].

---

## 4. Decisions that need authoring before code

Eleven. Each is authored content under `CLAUDE.md` §13 and none is the build session's to
write. Clauses are in `CLAUDE.md` §15's amendment format so they paste into a build prompt, and
each names the checkpoints it blocks. Numbering continues from D-109, the last decision in the
register.

**Two of the eleven close decisions that are already open.** D-118 closes D-69 and D-119
disposes of D-90 by moving it rather than answering it. The other nine are new.

### D-110, the attribution shape

Blocks 4.1 and 4.10.

```
D-110 attribution gains surfaced_as, and score_per_screen carries the rank
beside the score. ACTIVE

D-85 authored both and neither exists. surfaced_as has no column in any of the
sixteen migrations and score_per_screen is flat jsonb with no writer. This
entry is the migration detail rather than a new ruling, and it exists because
the shape has to be settled in the same checkpoint as the column.

surfaced_as is text NOT NULL with CHECK (surfaced_as IN ('candidate','shadow'))
and no DEFAULT. No default for security_daily.is_active's reason: a column that
defaults is a column a writer can decline to think about, and the value that
gets written is then the one nobody chose. A writer that has not decided must
fail at the column.

The constraint rather than a writer-side check, for D-80's reason exactly. This
column segments every analysis in SCREEN_LIFECYCLE.md section 4.5, so a drifted
value lands in its own bucket in every one of them without ever erroring.

score_per_screen becomes screen id to an object of score and rank, held by a
jsonb CHECK asserting every top-level value is an object. The flat shape then
cannot be written at all rather than being refused by whichever writer
remembers. It is a shape change to a column with no rows in it, so it costs
nothing now and cannot be done cheaply later.

The candidate_attribution view selects surfaced_as = 'candidate' and every
reader meaning candidate reads the view. Reading the table becomes a deliberate
act rather than the default, which inverts which mistake is easy.

DoD: migration 0017 adds the column, the two CHECKs and the view; SCHEMA.md
gains surfaced_as in that same checkpoint and not before, guards.ps1 and
SchemaParityTests holding the document against the migrations in both
directions; an insert omitting surfaced_as fails at the column rather than
defaulting; a flat score_per_screen value is refused by the constraint and the
refusal is exercised; the view returns exactly the candidate rows over a
fabricated pair; ExpectedMonetary is unchanged and asserted, no column here
being money [INVARIANT 16].
```

**The cost is one migration against an empty table and it is the cheapest this decision will
ever be.** `attribution` holds zero rows today. After 4.14 it holds roughly 34,000 that cannot
be rewritten.

### D-111, the score table's read pattern

Blocks 4.1, and through it every checkpoint after it.

```
D-111 screen_score_daily is range-partitioned by date with its key reordered,
and the ranked rows carry a partial index. ACTIVE

The primary key is (ticker, screen_id, date) and the table carries no other
index [0001_snapshot.sql]. Every read this system makes of it is one date: the
allocator's nightly read, and the backfill's read once per date across roughly
1,260 of them. The leading column of the only index is the one the query does
not constrain [SCREEN_LIFECYCLE.md section 9.3].

Three changes, and all three rather than one of them.

The key becomes (date, screen_id, ticker). That fixes every per-date read
rather than only the allocator's, and it is the change section 9.3's complaint
literally describes.

Declarative range partitioning by date, one partition a year, with no default
partition. A date outside the declared range then fails loudly rather than
landing in a partition nothing queries. Partitioning makes the per-date read a
single partition scan and makes pass one's writes cheaper because each date
lands in its own child.

A partial index on (date, screen_id, rank_within_screen) WHERE
rank_within_screen IS NOT NULL. The floor admits about two percent of the
population, so the index covers about two percent of the table and costs about
two percent of what section 9.3 priced a full one at.

Section 9.3's option 1 as literally written is rejected. A full index on the
largest table in the system, maintained across roughly 19 million inserts
during pass one, to serve a query wanting two percent of the rows, while
leaving the key's leading column still wrong.

The cost is named rather than glossed. Partitioning adds one child relation a
year, and SchemaParityTests and guards.ps1 both read information_schema.columns
with no partition filter, so every child's score column would be reported as an
undeclared real column. Two predicates in two places, and they are part of this
checkpoint rather than a surprise inside it.

DoD: migration 0017 drops and recreates the table partitioned, guarded by an
assertion that it is empty so a re-run after the backfill fails rather than
discarding it; SchemaParityTests and guards.ps1 pass with no child partition
reported as an undeclared column, the filter exercised against a fabricated
child; the default partition does not exist and a write outside the declared
range fails; the partial index exists and a plan over one date and one screen
uses it.
```

**This is the decision `SCREEN_LIFECYCLE.md` §9.3 says must be taken before the screen backfill
and not after**, and the reason is the whole argument: both fixes are cheap on an empty table
and the second is substantially not cheap on a 1.4 GB one.

### D-112, how a screen score is composed

Blocks 4.3, 4.4, 4.6 and 4.7. The largest item in the phase.

```
D-112 A screen score is the weighted mean of its direction-adjusted
percentiles, taken over the non-null members of its metric list, and null below
a minimum input count. ACTIVE

METRICS.md section 8 assigns this here and nothing else states it. What the
formula has to survive is the null rule: a great deal of this store is
legitimately absent, and a screen that scores absence as the worst value
deletes the thinly covered small caps its small slots exist to find.

On the 0 to 100 scale the percentiles already use, so a 98th-percentile floor
reads against the same units the inputs carry and no second scale exists to
disagree with the first.

Mean and not sum. A sum makes the score a function of how many inputs happened
to be non-null, so a name with three of seven is scored below one with seven by
construction rather than on its merits. That is CLAUDE.md section 6's null rule
broken inside the arithmetic instead of at a call site, where nothing greps for
it.

screens.<id>.min_inputs is what stops a name with one of seven being scored as
confidently as one with seven. Below it the score is null, which is the
established idiom rather than a new one: ARCHITECTURE.html section 18 already
says of a name with no sentiment that the sentiment screen simply cannot rank
it.

Weights are carried per metric and default to 1. METRICS.md section 8 names
weights as this phase's to author, so the shape has to carry them even where
every seeded value is one, because adding the field later is a config migration
across every screen row.

Rejected: a sum of percentiles, which punishes absence. A geometric mean or a
product, where one zero annihilates and there is no reason to want an AND
across seven quality measures. Z-scores, which need a second normalisation the
store does not hold and reintroduce the cross-sectional scale D-10 removed.

One consequence, stated because a reader will otherwise read it as a defect. A
mean compresses toward 50 and compresses further as the metric count grows, so
S1 at seven inputs and S3 at three will show visibly different dispersions and
therefore floors at visibly different levels. That is correct, each floor being
drawn from its own screen's distribution, and it is stated so that a floor of 71
on one screen beside 84 on another is not read as a fault.

DoD: a hand-computed reference over a fabricated screen of three metrics
reproduces exactly, the arithmetic written in the test rather than
reimplemented; a name with one null input scores over its present inputs and is
not scored as if at the bottom; a name below min_inputs carries null and not a
low score; the direction rule in D-113 and the bonus rule in D-114 are
exercised inside the same reference; two runs over one date are byte-identical.
```

**The scale is stated rather than inherited, and that is deliberate.** `METRICS.md` §6.3 puts
percentiles at 0 to 100 while `WORKED_EXAMPLE.md` §3 prints a screen score of 0.79 against a
floor of 0.74. That document declares its figures illustrative and internally consistent rather
than measured, so it does not bind, but a decision that did not say which scale it meant would
leave two authored documents disagreeing about the column.

### D-113, direction

Blocks 4.3 and 4.4.

```
D-113 The direction a screen reads a metric in lives in screens.<id>.metrics,
and no _inv column exists. ACTIVE

Percentiles are ascending always: higher raw value means higher percentile, for
every metric [METRICS.md section 6.3]. S1 ranks on net_debt_ebitda_inv,
accruals_inv and share_count_change_inv, and those names in ARCHITECTURE.html
section 05 name a direction rather than a column. No _inv column exists or is
meant to [BUILD_PLAN.md carried obligations, 2 to 4].

The metric list is an array of objects, each carrying the metric, the direction
and the weight:

  {"metric": "net_debt_ebitda", "direction": "low", "weight": 1}

direction "low" is applied as 100 minus the stored percentile. The words are
"high" and "low" rather than asc and desc or plus and minus one, because the
stored percentile is always ascending, so the word has to say what the screen
rewards rather than how anything sorts.

Rejected: a stored _inv column, which is a second copy of one fact and doubles
the percentile write. That is the shape D-76, D-77 and D-83 each removed from
this corpus.

Rejected: a parallel screens.<id>.directions key, being two lists that can fall
out of length with each other and produce a plausible score when they do.

DoD: net_debt_ebitda at the 95th percentile contributes 5 to S1 and not 95,
asserted; a metric list carrying an unrecognised direction fails the stage
closed rather than defaulting to high; the three _inv names in section 05 are
seeded as direction "low" and a test reads them back.
```

### D-114, the boolean

Blocks 4.6.

```
D-114 base_breakout_flag enters S2 as a fixed bonus in score points outside the
mean, and a null flag contributes what false contributes. ACTIVE

It is the one column with a named reader that is not percentiled, because a
percentile over a two-valued column collapses to two values and carries nothing
the boolean does not [METRICS.md section 6.5]. It is an S2 ranking input
[ARCHITECTURE.html section 05], so it has to enter somehow.

  {"metric": "base_breakout_flag", "kind": "bonus", "points": 10}

applied after the weighted mean.

Mapping true to 100 and false to 0 inside the mean is rejected, and the
arithmetic is the reason. At six S2 inputs one boolean is worth a sixth of the
score and opens a fifty-point gap between two names differing on one binary
condition. That is the magnitude falling out of the mean's arithmetic rather
than being chosen, which is exactly what section 6.5 says a percentile over a
two-valued column cannot represent. A bonus is the only form where the
magnitude is stated in configuration.

Rejected: the flag as a hard gate on S2, which narrows the screen to a handful
of names most nights and leaves its floor drawn over a population it no longer
ranks.

Null contributes zero, the same as false, and that is written down rather than
defaulted. "There is no base" and "the base is unknown" both mean no evidence
of a defined entry level, and the bonus is evidence-positive only. This is the
one place in this system where unknown and false legitimately coincide, which
is why it is stated rather than left to the null rule.

DoD: the bonus is exercised at true, false and null with the hand-computed
reference from D-112; a name whose flag is null scores identically to one whose
flag is false and both score below an otherwise identical name whose flag is
true; the points value resolves from config as of the date and is not a
literal.
```

### D-115, the floor's denominator

Blocks 4.5 and 4.13.

```
D-115 The floor is the 98th percentile of the non-null score population, and a
screen without a full lookback has no floor and ranks nothing. ACTIVE

D-112 makes a score null for a name below its screen's minimum input count, so
the score column is legitimately sparse. Postgres counts nulls toward
PERCENT_RANK's denominator, so a floor computed over 700,000 slots of which
200,000 are null is a different number from one computed over 500,000 real
scores, and the two are indistinguishable on the page. This is
PercentileEngine's own count(metric) argument applied one level up.

Below screens.floor_lookback_days of observations there is no floor.
floor_score is null, observation_days records the short window, and nothing is
ranked. That is SCREEN_LIFECYCLE.md section 5.1's rule for a newly registered
shadow applied identically to a live screen at the start of the backfill
window, because it is the same condition rather than an analogous one.

The consequence is that the first 250 sessions of the window carry scores and
no candidates, so candidate history begins around 2022-01-03 rather than at the
window start. That is the floor working and it is stated here so the gap is not
read as a failed pass.

DoD: the p98 over a synthetic distribution containing nulls reproduces the
hand-computed figure and differs from the figure a null-counting denominator
would give, both asserted; with fewer than the lookback of observations
floor_score is null, observation_days records the short window and no row
carries a rank; ranked rows per screen per date are close to two percent of the
scored population, recorded rather than asserted to a bound.
```

### D-116, the quota arithmetic

Blocks 4.9.

```
D-116 D-89's proportion is the only quota arithmetic, and
screens.quota_large, quota_mid and quota_small are retired. ACTIVE

  large = floor(slots / 4)
  rest  = slots - large
  mid   = floor(rest / 2)
  small = rest - mid

which is exactly 2 / 3 / 3 at eight slots and integral at every count from four
to twelve [D-89]. The three quota keys are that proportion's output at eight
and nothing reads them once the proportion exists.

Rejected: keeping them as the proportion's inputs. Three keys that must sum to
slots, can be set so they do not, and nothing would notice.

Rejected: keeping them for eight and computing only above it. Two code paths
that must agree, of which the one that runs today is the one nobody exercises.

SCREEN_LIFECYCLE.md section 10 already says the proportion introduces no value,
so a key for it would be a key with nothing behind it. Config is append-only,
so the existing rows stay where they are and the retirement is a removal from
the seeder and from CONFIG_REFERENCE.md rather than a delete.

DoD: the nine-row table in SCREEN_LIFECYCLE.md section 8.1 reproduces exactly
as a fixture at every count from four to twelve; at eight the split is 2 / 3 /
3; the three keys are absent from ConfigSeeder.Keys and from
CONFIG_REFERENCE.md and a test asserts nothing reads them; the allocator fails
the stage closed when screens.<id>.slots falls outside tuner.slot_floor to
tuner.slot_cap.
```

**One contradiction is reported here and not resolved.** `screens.slot_ceiling` is 8 [D-7,
`CONFIG_REFERENCE.md:320`] and `tuner.slot_cap` is 12 [D-43, `CONFIG_REFERENCE.md:413`]. This
plan reads the ceiling as the allocation each screen is seeded at and the cap as what binds,
which is consistent with D-43 having permitted four to twelve since it was designed. That
reading is a human's to confirm, and the fail-closed check in the DoD is what makes a wrong
reading loud rather than silent.

### D-117, where the gate applies, and what it means over history

Blocks 4.8, 4.9 and 4.14.

```
D-117 A gated name is scored, excluded at allocation, and its slot passes to
the next name of the same size. gate_state records whether every gate reason
was evaluable. ACTIVE

C13 does not read gate_result and must not. The floor is the 98th percentile of
the whole scored population, so a floor over the ungated subset moves when a
position opens or a cooldown expires, which makes a screen's floor a function
of the portfolio [INVARIANT 1, INVARIANT 2]. C14 reads gate_result and drops
gated names from the ranked list before slots are filled.

Filling then continues within the bucket, and the distinction from D-8 is the
whole of this clause. D-8 forbids backfilling from a larger bucket, which is
the case where nothing of that size cleared the screen's floor. A name that
cleared the floor and is unavailable tonight is a different case, and
conflating the two either leaves a hole D-8 does not ask for or leaks the
guarantee D-8 exists to hold.

The five gate reasons are named in ARCHITECTURE.html section 03 and their
thresholds are keys under a new gates.* namespace, one per threshold, with no
literal at a call site [CLAUDE.md section 8]. Every failing reason is recorded
rather than the first, which SCHEMA.md already requires of the column, so
passed is the empty reason array rather than a separately written flag.

Three of the five are structurally unevaluable over the backfill window rather
than passing over it. position and trade_outcome hold no rows until phase 7, so
already-held and cooldown can never fire. events.earnings_backward_days is 7
and earnings are deliberately not backfilled, so the blackout has no calendar
to read on a historical date.

So attribution.gate_state carries passed or passed_partial and never gated, a
gated name having no attribution row to carry it. passed_partial means at least
one gate reason was structurally unevaluable on that date, and it is what stops
a backfilled row reading identically to a live one when it is not. This is the
D-58 and D-69 pattern met a third time and the first time it is labelled on the
row rather than found afterwards.

DoD: every active member has exactly one gate_result row, the gate labelling
and never narrowing [INVARIANT 1]; a name failing three reasons carries three
entries in a fixed order and passed is false; a name with an open position row
is gated, exercised against a fabricated row rather than left untested because
the table is empty; the same for cooldown against a fabricated trade_outcome; a
gated name's slot passes to the next name of its own size and never to a larger
one, asserted beside D-8's own fixture so the two cases are visibly different;
every backfilled row carries passed_partial and every row from a date with a
readable earnings calendar carries passed, asserted over one date of each.
```

**The obligation this creates is named rather than left implicit.** Every backfilled row will
read `passed_partial` and every live row `passed`, so the two populations differ by
construction at the boundary. The `candidate_attribution` view protects one filter structurally;
nothing protects this one, and a later analysis pooling them would be comparing a gated
population against an ungated one. It belongs in the carried obligations table owed to phase 8,
in the same form as the view's reader test.

### D-118, closing D-69

Blocks 4.5, and it is owed before floors are drawn.

```
D-118 S4 drops inst_ownership_change and runs on its two insider inputs.
ACTIVE, closing D-69.

D-58 removed short_interest_change from S4 for this fact in a different form: a
screen whose backfill scores come from a different population than its live
scores has a floor drawn from a distribution the live screen does not share.
Deciding the same question the other way here, with no new evidence, is
result-shopping under CLAUDE.md section 11.

The independent argument does not rest on backfillability at all.
inst_ownership_change is a change in the top twenty holders' total, so when a
holder enters or leaves that set between report dates, composition change and
ownership change are added together and the metric cannot separate them
[BUILD_PLAN.md carried obligations, 1 to 4]. The input is defective whether or
not it has a history.

Rejected: keep all three and accept a two-input backfill distribution against a
three-input live screen. That is what D-58 rejected, and its failure is a floor
that is simply wrong and inspectable by nothing.

Rejected: source ownership from SEC EDGAR 13F. Free and complete, and a second
provider and a real ingest, which is a phase-1-shaped body of work added to a
selection phase's own scope [CLAUDE.md section 3].

Two costs are recorded rather than glossed. Both surviving inputs come from one
endpoint, so a single provider change now takes the whole screen rather than
one input; the screen was designed with four inputs from three sources and this
is the second removal. And SCREEN_LIFECYCLE.md section 7.6 notes the family
covers no axis but S1's, so a later S4 failure leaves the flow axis uncovered
and is a design decision rather than a swap.

One measured caveat is carried forward rather than discovered in phase 8.
sec-filings/{t}/form4 returns 404 Symbol not found for every delisted name
tested, against the same ticker strings eod/{t} answered for in the same run
[D-69, measured at 3.1]. So S4's backfilled distribution, and therefore its
floor, is measured over survivors. That is a known property of this screen's
floor and belongs in PROGRESS.md as one.

flow_daily.inst_ownership_change keeps computing and stays available to the
dossier as a static feature. It stops being a ranking input, not a column.

DoD: S4's seeded metric list carries two metrics and not three; a test asserts
inst_ownership_change appears in no screen's metric list; the column is still
written by C34 and still percentiled by C11, asserted so the removal is
visibly from the screen rather than from the store; S4's survivor caveat is
recorded in PROGRESS.md against its floor.
```

### D-119, D-90 moved rather than answered

Blocks nothing, and that is the point of it.

```
D-119 The shadow family is not registered in phase 4. The mechanism is built
and proven against a fixture screen, and the registration decision is taken at
this phase's sign-off. ACTIVE.

SCREEN_LIFECYCLE.md gives phase 4 the mechanism: C13 iterating on
screens.<id>.state and C14 recording a shadow to tuner.slot_cap under the same
proportional quota. It does not require that any family member be registered
while that mechanism is built, and the two are separable.

Registering the family in this phase costs its share of section 9.1, taking
screen_score_daily from roughly 1.4 GB to roughly 2.2, and requires D-90 to be
answered before the phase can start rather than when the fork actually bites.

So the mechanism is exercised against a fixture screen registered as shadow
and removed again, which is the same proof at none of the cost. The screen
that proves a shadow writes attribution and no candidate_set row does not have
to be a screen anyone intends to promote.

D-90 is therefore not owed until the family is registered, which is
SCREEN_LIFECYCLE.md section 7.5's own condition read literally: the fork is
about X-PEAD's registration, and nothing registers here. The same applies to
the three backfillable members, whose registration is a decision taken on the
measured live backfill rather than before it.

What this buys beyond cost: the registration decision is then taken by someone
who has seen five live screens scored over 1,462 dates, with the persistence
figures in section 5 in front of them, rather than by someone reasoning about
a mechanism that has never run.

DoD: a fixture screen registered as shadow is scored by C13, writes
screen_history, writes attribution rows labelled shadow, and writes no
candidate_set row, all asserted; removing it leaves the live five unchanged
row for row; no family member is seeded; screens.<id>.state is seeded live for
S1 to S5 and the key gains its CONFIG_REFERENCE.md row.
```

### D-120, S5's two composites

Blocks 4.7.

```
D-120 S5's quality and technical gates are metric lists of S5's own, and the
duplication against S1 is the price of INVARIANT 2. ACTIVE

ARCHITECTURE.html section 05 states S5's gate as "S1 top quintile AND technical
bottom quintile AND stabilising", which read literally is one screen reading
another. WORKED_EXAMPLE.md section 3 says what it means: the two gates traced
there are a quality composite percentile of 84 and a technical composite
percentile of 12. Neither composite is defined anywhere, and "technical bottom
quintile" names inputs no document lists.

screens.s5.quality_metrics holds the same content as S1's metric list, copied.
screens.s5.technical_metrics holds dist_200dma, dist_52w_high and
rs_change_63d, each direction high, so a low composite is a name that has
fallen against its cell on all three. Both are composed with D-112's arithmetic
from the percentile store and neither reads another screen's score or config.

screens.s5.quality_quintile_min is 80 and
screens.s5.technical_quintile_max is 20, top and bottom quintile on the 0 to
100 scale METRICS.md section 6.3 fixes.

The copy is deliberate and is stated rather than deduplicated. Removing it is
what INVARIANT 2 forbids, in the invariant's own words: sharing a computed
value between screens looks like removing duplication and destroys the
independence the design rests on. What holds it is a per-screen config facade
that throws when asked for another screen's key, so the shortcut is unavailable
rather than discouraged.

DoD: S5 scores with S1 absent from the registry entirely, asserted, which is
the test that the copy is real; asking the S5 facade for screens.S1.metrics
throws; a name outside the quality quintile carries a null score and not a low
one; below s5.news_gate_min_articles in seven days both news conditions are
treated as satisfied, and at or above it a name with
article_count_z_own_90d at or above s5.stabilisation_z_max fails the burnout
condition; the ranking metric is distance below the 200-day average and the
worked example's arithmetic reproduces.
```

---

## 5. The persistence measure, pre-registered

**The phase's done-when is a diversity test rather than a signal test, and it is nearly blind
to the composition being wrong.** That is stated here, before any code, because a phase whose
acceptance test cannot see the thing being tested does not have an acceptance test.

**The mechanism.** The floor is self-referential, so it admits about two percent of the
population whatever the score is [D-9]. Replace every screen's score with a date-seeded random
number and four of the five distribution lines pass outright. The ceiling cuts each screen to
eight. Dedup takes forty to roughly twenty-eight. **The 2/3/3 distribution holds by
construction**, because the allocator enforces the quota rather than the screen earning it.
Megacap share sits at the market's own bucket proportions, which are well under a third.
Distinct tickers over sixty days go through the roof precisely because a noise screen never
repeats. The fifth line, overlap near 10 to 20 percent, is the only one that moves, and it
moves toward what reads as good independence.

That matters because this phase's output is a record that cannot be rewritten, and ten of the
eleven decisions in §4 are arbitrary in the specific sense that no number in this corpus
constrains them.

**So the measure that would distinguish the cases is stated in full now, before 4.13 runs and
before any score exists to look at.** Written afterwards, each of the three readings below can
be narrated as the expected one.

**The measure.** For each registered screen, over the backfilled range: the mean Jaccard
overlap of the screen's ranked set, meaning the names carrying a non-null `rank_within_screen`,
between date D and date D-1, and the same statistic at D-5 and D-21. One aggregate over
`screen_score_daily`, per screen, reported as three numbers and their decay. It reads no
forward return and no `attribution` row, so it does not touch `CLAUDE.md` §11's prohibition on
tuning screens on forward returns before anything has judged them.

**The three readings, stated in advance.**

| Reading | Shape | What it means |
|---|---|---|
| **Near-chance persistence** | D-1 overlap close to what random draws of the same size from the same population give, and at that level already by D-5 | The screen ranks on noise. Its composition is wrong, or its inputs are too sparse to rank on, and its two percent is a lottery |
| **High persistence with a large-cap tail** | D-1 overlap very high, decay to D-21 shallow, and the ranked set's size distribution tilted large against the screen's own bucket quota | The screen ranks on something that is really a size or liquidity proxy. This is the convergence §20 says kept trying to reappear, arriving through the composition rather than through a ranker |
| **Slow decay from a high base** | D-1 overlap high, D-5 lower, D-21 materially lower, size distribution unremarkable | The screen ranks on a real and slow-moving property. This is what the design expects, and it is the only one of the three that is |

The chance baseline is computed per screen from its own ranked-set size against its own scored
population on those dates rather than assumed, because the five screens rank different numbers
of names and a single baseline would flatter the narrow ones.

**Where it is taken, and why the ordering is not incidental.** The measure reads
`screen_score_daily`, which 4.13 fills. `attribution` becomes immutable at 4.14. A measure that
would have changed a decision is worth nothing taken after the row it would have changed can no
longer be changed, so it is 4.13's and 4.14 does not begin until it is recorded.

**What stopping looks like, said now rather than improvised then.** If a screen reads
near-chance, or reads as a size proxy, **4.14 does not run**. The score table is a build
artefact and can be dropped and recomputed; the attribution table cannot. The phase stops at
4.13 with the figures in `PROGRESS.md`, and what happens next is a human's call on an authored
decision: a changed composition under a new `config_version`, a screen withdrawn, or the
reading accepted and 4.14 released. The build session reports and waits. It does not adjust a
weight until the number improves, which is result-shopping whatever the reasoning says
[`CLAUDE.md` §11].

**Whether this becomes a sixth done-when line is unauthored and this plan does not decide it.**
A build session never adds a measurement to its own scope [`CLAUDE.md` §3]. The phase signs off
either way, **with the figure recorded in `PROGRESS.md` for every registered screen**, which is
the part that is not optional. A measure taken and not written down is not a measure.

---

## 6. Checkpoints

Fourteen, one commit each, `Phase 4 / 4.n - what it did`. Run `ci.ps1` per checkpoint, which is
the per-checkpoint verification since 1.12 and the only thing that cannot reach the test step
off a stale binary.

**The order follows three facts, in decreasing force.**

**Attribution is written once and never rewritten** [INVARIANT 4, D-40, `RUNBOOK.md`]. That
makes this phase a funnel rather than four parallel component builds: every decision that could
move a row's contents closes upstream of the allocator's range pass, and the range pass is
therefore last. It also means this phase needs a moment nobody has named, **the point at which
the record starts**. During the build a range run is repeatable because it is a build; at 4.14
it stops being one, and putting that transition in a checkpoint with its sha and row count is
what makes the `RUNBOOK.md` line enforceable rather than remembered.

**Schema is free exactly once.** All of it lands in one migration at 4.1 against six tables
holding zero rows. A second migration later runs against a populated table and forces a second
conformance pass over `SCHEMA.md`, `guards.ps1` and `SchemaParityTests`. The carried obligation
also requires `SCHEMA.md` to gain `surfaced_as` in the same checkpoint as the column, the parity
tests running in both directions.

**Config is what a screen is** [`CLAUDE.md` §5]. Config precedes the engine, or the row's shape
becomes whatever the engine happened to need, and `screens.<id>.metrics` is the one key in this
system whose shape is a design decision rather than a value.

**The gate goes after the screens and before the allocator, and that is argued rather than
assumed.** There is no data dependency in either direction. C13 does not read the gate and must
not, for §2's first rule. The gate is the least checkable component in the phase, three of its
five reasons being structurally inert over the whole window, so opening the phase on five
unverifiable thresholds lands nothing observable, which is what a checkpoint is for. And the
gate's real question, what `gate_state` means on a backfilled row against a live one, is only
sharp once a score table and a per-date allocation exist to ask it against. It must nevertheless
precede the allocator, because a candidate set computed before the gate exists has to be
recomputed, and that is the rewrite the RUNBOOK forbids. So the build order is C13, then C12,
then C14, which matches §04's evening order in dependency even where it inverts it in clock
time.

### 4.1 All schema, before any component and before a single row

Blocked on D-110 and D-111. Migration `0017`, `SCHEMA.md` and `ARCHITECTURE.html` §16 in the
same commit, and the parity checks taught about partition children.

**This is the only checkpoint whose cost rises if it slips**, which is why it is first despite
being blocked on two decisions. Every other checkpoint is the same work whenever it happens.

*Done when:* `migrate.ps1` run twice applies nothing the second time; `attribution` carries
`surfaced_as` NOT NULL with its CHECK and no DEFAULT, and an insert omitting it fails at the
column; a flat `score_per_screen` value is refused by the constraint, exercised;
`screen_score_daily` is partitioned by date with the key reordered, the partial index present
and no default partition, and **a drop-and-recreate attempt against a non-empty table fails
loudly** rather than discarding it; `candidate_attribution` returns exactly the candidate rows
over a fabricated pair; `SchemaParityTests` and `guards.ps1` pass in both directions with **no
child partition reported as an undeclared column**, the filter exercised against a fabricated
child; `ExpectedMonetary` is unchanged and asserted; all six tables still hold zero rows.

### 4.2 The view's reader test, built against no readers

Blocked on D-110. The list of candidate-meaning readers from `SCREEN_LIFECYCLE.md` §4.5, and
the assertion that each registered one names the view rather than the table.

**A check over an empty list passes vacuously and would keep passing until phase 9**, which is
the whole reason the fabricated half is in this checkpoint rather than left to the phase that
first has a real reader. The eight readers §4.5 enumerates are all phase 8 and 9 components and
none exists. Writing the check now means each of them is written under it rather than by a
session that has not read §4.5.

*Done when:* the declared list is non-empty and cites §4.5 rather than restating its reasoning;
a fabricated reader on the list declaring `attribution` fails and the failure names the view; one
declaring `candidate_attribution` passes; **a fabricated reader not on the list declaring
`attribution` passes**, which is the everything-surfaced case and would otherwise be caught
wrongly; `DeclaredAccess` admits the view name through the same guard a table goes through.

### 4.3 Config, and the facade that makes INVARIANT 2 structural

Blocked on D-112, D-113, D-114, D-116, D-118, D-119 and D-120. The `screens.*` and `s5.*` keys
seeded, the metric-list shape, resolution as of the simulated date, and a per-screen config
facade.

**The set of screens is discovered from config and never from a list in code**, which is what
`CLAUDE.md` §5 means by a screen being a row.

**The facade is where INVARIANT 2 stops being a rule someone remembers.** It is constructed with
one screen's id and refuses any key not prefixed `screens.<thatId>.` or in the shared namespace,
so a screen cannot reach another screen's config at all. That is `DeclaredAccess`'s own idiom
applied to configuration, and it is why D-120's deliberate duplication is safe to write down.

*Done when:* `ConfigSeeder.Keys` moves from **44** to its new count and the count is asserted,
which is what catches a key with nothing behind it; every `screens.*` and `s5.*` key resolves at
2021-01-04 and at the frontier, and **a version 2 seeded with a later `set_at` does not change
what a 2021 date resolves to** [INVARIANT 13]; the live ids resolved as of a date are exactly S1
to S5 and no family member is registered; **asking the S5 facade for `screens.S1.metrics`
throws**; the three retired quota keys are absent from the seeder and from
`CONFIG_REFERENCE.md`; running the seeder twice inserts nothing the second time.

### 4.4 C13 ScreenEngine, one screen, scoring every member

Blocked on D-112 and D-113. One date-partitioned set-based statement per screen on
`PercentileEngine`'s shape, `score` written for every active member, `rank_within_screen` still
null because no floor exists yet.

**Its declared read set names five stores the catalogue does not**, which is §8's B1. The build
declares what it reads and reports the conformance failure rather than narrowing the declaration
to fit the markup.

*Done when:* the row count for one screen on the blessed date **equals `Universe.MembersAsOf`'s
count exactly**, which is INVARIANT 1 checked mechanically rather than argued; the hand-computed
reference over a fabricated three-metric screen reproduces exactly; a name with one null input
scores over its present inputs and is not scored as if at the bottom; a name below `min_inputs`
carries null; `config_version` is the version in force **as of the date** and not the newest,
exercised by seeding a later version and re-running a past date; two runs over one date are
byte-identical.

### 4.5 Floors, and the rank that carries them

Blocked on D-115 and D-118, the second because a floor drawn on the wrong metric list is a floor
that has to be redrawn. `screen_history`, the trailing p98, and `rank_within_screen` dense from
1 at or above the floor and null below it.

**Ranking here rather than in the allocator is what makes §06's "floors already applied"
literally true.** C14 then needs no floor knowledge at all and cannot apply one differently.

*Done when:* the p98 over a synthetic distribution containing nulls reproduces the hand-computed
figure **and differs from what a null-counting denominator would give**, both asserted; with
fewer than `screens.floor_lookback_days` of observations `floor_score` is null,
`observation_days` records the short window and **nothing is ranked**; ranked rows per screen per
date are recorded as a proportion of the scored population rather than asserted to a bound; a
screen where nothing clears its floor returns nothing and the run does not fail
[`ARCHITECTURE.html` §18].

### 4.6 S2, S3 and S4 as config rows, with no new code path

Blocked on D-114 and D-118.

**This is the checkpoint that proves a screen is a row**, and the proof is the test rather than
the three screens. A throwaway sixth screen id seeded into config produces scores with no code
change and is then removed. If that needs a class, the phase has already gone wrong.

*Done when:* a fabricated sixth screen seeded into config is scored, writes rows, and is removed
leaving the five unchanged row for row; each of S2, S3 and S4 scores a count equal to the member
count; **the boolean's contribution is asserted at true, false and null** and null scores
identically to false; S4's metric list carries two metrics and `inst_ownership_change` appears in
no screen's list, while the column is still written and still percentiled.

### 4.7 S5, the gated screen

Blocked on D-120 and D-112. The two composites, the three stabilisation conditions, the two news
conditions failing open, and the ranking on distance below the 200-day average.

**The fail-open rule is the one thing here that must not be tidied into a fail-closed one.**
Below `s5.news_gate_min_articles` articles in seven days the two news conditions are treated as
satisfied, because thinly covered names are exactly what this screen's small slots exist to find
and failing closed deletes them [`ARCHITECTURE.html` §05].

*Done when:* **S5 scores with S1 absent from the registry entirely**, which is the assertion that
the metric-list copy is real rather than a reference; a source-level check asserts S5's statement
names no other screen's id, beside 4.3's facade as the structural half; below the article
threshold both news conditions pass and at or above it a name at or above `s5.stabilisation_z_max`
fails the burnout condition; a name outside the quality quintile carries **null and not a low
score**; the worked example's arithmetic for distance below the 200-day average reproduces; S5's
floor is drawn over its non-null population only.

### 4.8 C12 GateEngine

Blocked on D-117. Five reasons, a new `gates.*` namespace, every failing reason recorded.

**The gate labels and never narrows.** Every active member gets a row, which is what makes
INVARIANT 1 checkable here as well as at C13.

*Done when:* every active member has exactly one `gate_result` row; a name failing three reasons
carries three entries in a fixed order and `passed` is false; `passed` is the empty reason array
rather than a separately maintained flag; the reason vocabulary is closed and a value outside it
fails the stage; **the two phase-7 gates are exercised against a fabricated `position` row and a
fabricated `trade_outcome` row** rather than left untested because the tables are empty; C12 is
the sole declared writer of `gate_result`.

### 4.9 C14, the live half

Blocked on D-116 and D-117. The proportion, the ceiling, the empty slot, dedup across screens,
`candidate_set`.

*Done when:* **the nine-row table in `SCREEN_LIFECYCLE.md` §8.1 reproduces exactly as a fixture
at every count from four to twelve**, and at eight the split is 2/3/3; **a screen with no small
name clearing its floor sends fewer names and no larger name is promoted into the small slot**,
which is INVARIANT 3's fixture; **a gated name's slot passes to the next name of its own size**,
asserted beside that fixture so the two cases are visibly different rather than conflated; a name
surfaced by two screens is one row carrying both ids; `screens.<id>.slots` outside the tuner's
floor and cap fails the stage closed.

### 4.10 The attribution write

Blocked on D-110 and D-119. One row per name any registered screen surfaced, scores and ranks
frozen as they stand, `surfaced_as`, `config_version`, and the return columns empty.

**Its Reads cell does not carry the tables it needs**, which is §8's B2 and the one contradiction
in this phase that markup cannot fix.

*Done when:* one row exists per name any registered screen surfaced and none for a name none did;
`score_per_screen` parses to the object shape and the flat shape is refused **by the constraint
rather than by the writer**; the nine return columns are null; **the allocator attempting an
Update throws through `DeclaredAccess` before a connection opens** [INVARIANT 10];
`candidate_attribution` returns exactly the candidate rows; **a candidate row carrying a shadow id
in `screens_surfacing` is the ordinary case and is asserted**, which is `SCREEN_LIFECYCLE.md`
§4.6's trap; a fixture screen registered as shadow writes attribution rows labelled shadow and no
`candidate_set` row, and removing it leaves the live five unchanged.

### 4.11 C28 ConcentrationMonitor

Reads `security_daily` rather than `security`, which is §8's B3.

*Done when:* a fabricated 20-day window at 40 percent megacap raises one alert; a 60-day window at
249 distinct tickers raises the other; a clean window raises neither; C28 is `alert`'s only
declared writer; **the same window classified from `security` and from `security_daily` gives
different answers on a 2022 date**, which is B3 stated as a number rather than as an argument.

### 4.12 The evening order, and one real night

`NightlyRun.EveningOrder` gains C12, C13, C14 and C28 at their §04 times, and `BackfillSequence`
gains a second constant for the selection order.

**It produces zero candidates and that is correct.** `screen_history` is empty, so no floor
exists, so nothing is ranked. Asserting that as the warm-up case rather than treating it as a
halt is the point of running the night before the range.

*Done when:* a night runs end to end on the current store and produces zero candidates, asserted
as the warm-up case; one `run_log` row per stage with its duration and row count; the zero-row
halt fires where `ARCHITECTURE.html` §18 says it should and not where it says it should not; the
selection order names only components that exist.

### 4.13 The two-pass range run, and the persistence measure

Blocked on D-115. Pass one loops dates writing scores with no dependency on any other date. Pass
two is one window function over the complete table writing floors and ranks.

**Pass two refuses to run unless pass one's distinct date count equals the calendar's session
count for the range.** One query, no new table. A floor computed over a short score table is a
floor drawn from a population that does not exist, and it would look entirely normal.

**§5's persistence measure is computed and recorded here**, per screen, against the three
readings stated in advance. 4.14 does not begin until it is in `PROGRESS.md`.

*Done when:* 1,462 dates times the registered screen count, one row per member per screen; wall
clock recorded per pass and compared against C11's measured 48.54 minutes over the same dates;
`rank_within_screen` is null everywhere after pass one; **an interrupted pass one blocks pass two
rather than producing floors over a short table**, exercised; re-running one date reproduces
byte-identically; the measured `screen_score_daily` size recorded against `SCHEMA.md` §16's
estimate; **the persistence figures recorded for every registered screen with its own chance
baseline beside them**, and the reading named against §5's table.

### 4.14 The gate and the allocator over the range, and the point the record starts

Blocked on everything above and on §5's reading. Range mode for C12 and C14, then C28 over the
range.

**This checkpoint contains one explicit truncate-and-run of `candidate_set` and `attribution`,
and after it `RUNBOOK.md`'s prohibition on re-running the attribution write is operative.** The
sha and the frozen row count go in `PROGRESS.md`, because a rule about a moment nobody wrote down
is a rule that gets broken by someone who did not know the moment had passed.

*Done when:* candidate history exists from the first date carrying a floor to the frontier; **the
six done-when criteria measured**, being median candidates a night, the size distribution, megacap
share over every 20-day window **including inside 2022**, distinct tickers over every 60-day
window, overlap between screens, and an attribution row with `config_version` for every candidate;
every backfilled row carries `passed_partial` and the count is stated; every `alert` row raised
over the range is enumerated and explained rather than counted; the sha and frozen row count are
in `PROGRESS.md`; **`CONFIG_REFERENCE.md`'s Consumer column is filled for every `screens.*`,
`s5.*` and `gates.*` key from the composition code read line by line** [`CLAUDE.md` §8].

---

## 7. Definition of done

1. **A night yields roughly 26 to 30 candidates**, measured over the backfilled range rather than
   over one night, with the median and the spread recorded.
2. **The size distribution holds**, being D-89's proportion at each screen's slot count, and the
   unfilled slot is left empty rather than backfilled, held by INVARIANT 3's fixture and by the
   separate fixture for a gated name's slot.
3. **Megacap share sits under a third over every 20-day window including inside the 2022
   drawdown**, classified from `security_daily` and not from `security`.
4. **Distinct tickers over any 60-day window exceed 250.**
5. **Overlap between screens falls near 10 to 20 percent**, recorded per pair as well as overall,
   since a single figure hides which two screens are converging.
6. **Attribution rows exist for every candidate with the config version stamped**, written at
   shortlist time with scores and ranks frozen, never reconstructed.
7. **A screen is a row**, held by a fabricated sixth screen scoring with no code change.
8. **The persistence figures are recorded for every registered screen** with its own chance
   baseline beside them and the reading named. Whether this is a criterion or a record is
   unauthored, and §5 says so.

**Invariants at risk:** 1, 2, 3, 4, 10 and 13. **1** because a screen that scored a shortlist
rather than the universe would produce a floor over a population ranking had already narrowed,
and the row-count assertion at 4.4 is what catches it. **2** because S5's gate is one line of SQL
away from reading S1's score, and D-120's copy plus 4.3's facade are what stand between. **3**
because a gated name's slot and an unfillable slot look alike and D-117 separates them. **4**
because 4.14 freezes a record that no later pass may rewrite. **10** because `attribution` is
insert by one component and update by another, and the allocator attempting an update must throw
before a connection opens. **13** because a screen definition resolved as of today against a 2022
date scores that date under definitions it was not scored under, and looks right doing it.
**16 is asserted unchanged**, no column this phase adds being money.

---

## 8. Findings to report, not to resolve

**B1. C13's Reads cell cannot be satisfied as written.** `ARCHITECTURE.html` §3 gives ScreenEngine
"Percentiles, `config_rows`, `screen_history`". `ArchitectureDocument` takes `code` elements only
and intersects them with `SCHEMA.md`'s tables, so "Percentiles" is bare prose and the catalogue
yields two tables where the component needs seven. This is the same case the parser already
records against C15 DossierBuilder, whose comment says in terms that the fix is the markup rather
than the assertion. The build declares what it reads and reports the failure.

**B2. C14 cannot write the columns `SCHEMA.md` gives it from the tables §3 gives it.** Its Reads
cell is `screen_score_daily` and `gate_result`. It must write `candidate_set.size_bucket` and
`attribution.size_bucket`, `sector` and `regime`, which come from `security_daily` [D-92] and
`market_context_daily`. Neither is in the cell and `screen_score_daily` carries neither. This is
not markup: §3 and `SCHEMA.md` disagree about what the component does, and closing it is an
authored amendment to a Reads cell.

**B3. C28's Reads cell names `security`, which is the wrong table now.** `security` holds one row
per ticker carrying today's bucket, and D-92 moved point-in-time bucket and market cap to
`security_daily` for exactly this reason. A megacap share over a 2022 window computed from
`security` classifies 2022 with 2026 buckets, which is the defect D-92 exists to prevent arriving
in the one component whose entire job is to notice a wrong number that looks right.

**The `screen_evaluation` ownership contradiction.** `ARCHITECTURE.html` §16's note says of the
`NOT YET IN SCHEMA` marker that one row carries it, "`screen_evaluation`, whose migration is
phase 4's". `BUILD_PLAN.md`'s carried obligations owe it to phase 8, with C22 as its declared
writer in the same checkpoint as its migration. The recommended reading is that it stays phase
8's, because INVARIANT 10 requires the writer to be declared with the migration and C22 does not
exist, so a phase 4 migration would create a table with no declarable writer. That makes §16's
note the thing to correct, and correcting it is a human's.

**The score's scale is stated differently by two authored documents.** `METRICS.md` §6.3 puts
percentiles at 0 to 100 and says top quintile is at or above 80. `WORKED_EXAMPLE.md` §3 prints a
screen score of 0.79 against a floor of 0.74. That document declares its figures illustrative
rather than measured so it does not bind, but D-112 has to state the scale rather than inherit
one, and this is why.

**Two S1 and S2 inputs are null at the start of the window and fill as it deepens.**
`ev_ebit_vs_own_5y` needs 24 month-end samples over five years and `dist_52w_high_20d_change`
needs 272 trading dates. A reader meeting an all-null ranking input early in the range is looking
at the window rather than at a fault.

**S4's floor is measured over survivors, and it is a property rather than a defect to chase.**
`sec-filings/{t}/form4` returns 404 for every delisted name tested against ticker strings the
price endpoints answered for in the same run [D-69, measured at 3.1]. Both of S4's surviving
inputs come from that endpoint, so its backfilled distribution excludes names that stopped
trading. Recorded against the screen's floor in `PROGRESS.md`.

**Phase 3.5 archived a `DRAFT` to `prompts/spent/` before any code, and that is a drift rather
than a precedent.** `CLAUDE.md` §3 says a prompt is archived after writing code and goes to
`prompts/spent/` as implemented, and `prompts/README.md` says `spent/` holds "every prompt that
has been issued", with a status of `ISSUED`, `SPENT` or `SUPERSEDED BY`. Phase 2's archive is
the prompt issued to the build session at `Status: SPENT`. Phase 3's is the plan as implemented,
its header recording that its four decisions were authored and its eighteen checkpoints landed.
Phase 3.5's is a byte-identical copy of the `DRAFT`, carrying a header that cites phase 3 as its
precedent for archiving early, which phase 3's own archive does not support. Three files in
`spent/` now read `Status: DRAFT`, a value the README does not list. This plan followed the same
reasoning at first and it was wrong; correcting it is one line in a header here and an authored
call about the three existing archives, which is not this phase's to make.

**The persistence measure is deliberately not in this section.** A finding is something reported
after the fact and that is a bar set before one, so it is §5 and is pre-registered rather than
filed here.

---

## 9. What phase 4 does not do

**No phase 8 or 9 reader.** The `candidate_attribution` view and its test are built at 4.1 and
4.2; C21, C22, C23, C24, C31, U2, U4, U7 and the abstention analysis are their own phases'. The
test exists now so that each of them is written under a check rather than by a session that has
not read `SCREEN_LIFECYCLE.md` §4.5.

**No forward returns read, filled or tuned on.** The nine return columns are written null and
stay null. `ForwardReturnFiller` is phase 8. Tuning screens on forward returns before the
researcher has judged anything optimises toward whatever captured market beta in the backfill
window, which is the convergence this design exists to prevent [`CLAUDE.md` §11, D-42,
`BUILD_PLAN.md` phase 4's own "What this phase does not test"].

**No `screen_evaluation`, no `ScreenTuner`, and no `lifecycle.*` key seeded.** Those are the
phase 8 obligations. Seeding six keys now would put six unverified Consumer rows in
`CONFIG_REFERENCE.md`, and that document's own rule is that an unverified entry is worse than an
absent one.

**No promotion, no retirement, and no acting on a shadow's backfilled distribution** [D-86,
D-88].

**No family member registered**, per D-119, and therefore no answer owed to D-90 here.

**No second provider and no EDGAR 13F** [D-118], **and no widening of
`events.earnings_backward_days`**, which is an ingest change and which phase 5 already owns the
point-in-time question for.

**No widening of `C36 RecordInspector`.** Its read set grows against §3's Reads cell one
checkpoint at a time, so widening it is an `ARCHITECTURE.html` edit and a scope widening at once.
It already shows every input to every screen score, which is the better debugging surface: a panel
showing the stored score shows you the bug, where the inputs let you catch it.

**No narrowing of C09's pool and no prune of `valuation_daily`.** Phase 3's record says that needs
an authored amendment to a Reads cell, narrowing downstream of the universe being what INVARIANT 1
forbids.

**No screen-specific dossier blocks.** Breakout level and distance from the base for S2,
peak-to-trough decline and days since peak for S5, all owed to phase 6.

**No authored document edited.** B1, B2, B3, the `screen_evaluation` note and the eleven decisions
are reported and drafted; none is closed from here [`CLAUDE.md` §13].

**No measurement added to this phase's own scope** beyond the eight §7 names, and §5 states
explicitly that the persistence figure is pre-registered and recorded rather than adopted as a
criterion by the build [`CLAUDE.md` §3].

---

## 10. Authored items owed, and files

**Authored by a human, blocking.** None of these is the build session's to write, and the
checkpoint each blocks is named beside it.

| Item | Document | Blocks |
|---|---|---|
| D-110, the attribution shape | `DECISIONS.md` | 4.1, 4.10 |
| D-111, the score table's read pattern | `DECISIONS.md` | 4.1 |
| D-112, how a screen score is composed | `DECISIONS.md` | 4.3, 4.4, 4.6, 4.7 |
| D-113, direction in the metric list | `DECISIONS.md` | 4.3, 4.4 |
| D-114, the boolean as a bonus | `DECISIONS.md` | 4.6 |
| D-115, the floor's denominator | `DECISIONS.md` | 4.5, 4.13 |
| D-116, the proportion and the retired quota keys | `DECISIONS.md` | 4.9 |
| D-117, gate placement and `gate_state` | `DECISIONS.md` | 4.8, 4.9, 4.14 |
| D-118, S4's inputs, closing D-69 and giving it a status line | `DECISIONS.md` | 4.5, 4.6 |
| D-119, the family unregistered, and D-90 restated as not owed here | `DECISIONS.md` | 4.3, 4.10 |
| D-120, S5's two composites | `DECISIONS.md` | 4.7 |
| The scope reading in §3, wide or narrow | `BUILD_PLAN.md` phase 4 | 4.13, 4.14 |
| C13's Reads cell, the five percentile stores in `code` rather than "Percentiles" in prose | `ARCHITECTURE.html` §3 | 4.4 |
| C14's Reads cell gains `security_daily` and `market_context_daily` | `ARCHITECTURE.html` §3 | 4.10 |
| C28's Reads cell, `security` becoming `security_daily` | `ARCHITECTURE.html` §3 | 4.11 |
| The `screen_score_daily` store row restated as partitioned, in the same checkpoint as the migration, `StoreMatrixConformanceTests` holding the list both ways | `ARCHITECTURE.html` §16 | 4.1 |
| The `screen_evaluation` note, phase 4's migration or phase 8's | `ARCHITECTURE.html` §16 | nothing, reported |
| `attribution` gains `surfaced_as` and the `score_per_screen` shape; `screen_score_daily`'s partitioning stated | `SCHEMA.md` | 4.1 |
| The `gates.*` section, one row per threshold, with the reason vocabulary enumerated | `CONFIG_REFERENCE.md` | 4.8 |
| `screens.<id>.state` and `screens.<id>.min_inputs` rows; the three quota keys removed | `CONFIG_REFERENCE.md` | 4.3, 4.9 |
| The phase 4 checkpoint table below, and the done-when restated to eight lines if §5's measure is adopted | `BUILD_PLAN.md` | all |
| The `gate_state` pooling obligation, owed to phase 8 in the same form as the view's reader test | `BUILD_PLAN.md` carried obligations | nothing, reported |

**The checkpoint table, to paste into `BUILD_PLAN.md` under the phase 4 section.**

| # | Scope |
|---|---|
| 4.1 | All schema, before any component and before a single row. Migration `0017`: `attribution.surfaced_as` NOT NULL with its CHECK and no DEFAULT, the `jsonb` CHECK holding `score_per_screen`'s object shape, `screen_score_daily` recreated partitioned by date with the key reordered and a partial index on the ranked rows, and the `candidate_attribution` view. `SCHEMA.md` and `ARCHITECTURE.html` §16 in this same checkpoint, and the parity checks taught about partition children. Blocked on D-110 and D-111 |
| 4.2 | The view's reader test, built against no registered readers plus a fabricated one, because a check over an empty list passes vacuously until phase 9. Blocked on D-110 |
| 4.3 | Config: `screens.*` and `s5.*` seeded, the metric-list shape carrying metric, direction and weight, resolution as of the simulated date, and **a per-screen config facade that refuses another screen's key**, which is INVARIANT 2 made impossible rather than documented. Blocked on D-112, D-113, D-114, D-116, D-118, D-119, D-120 |
| 4.4 | C13 ScreenEngine, one screen, scoring every active member. **The row count equals the membership count exactly**, which is INVARIANT 1 checked mechanically. Its declared reads name five stores the catalogue does not, reported. Blocked on D-112 and D-113 |
| 4.5 | Floors: `screen_history` and `rank_within_screen` null below the floor, so §06's "floors already applied" is literally true and the allocator needs no floor knowledge. Blocked on D-115 and D-118 |
| 4.6 | S2, S3 and S4 as config rows with no new code path. **A fabricated sixth screen produces scores with no code change**, which is what proves a screen is a row. Blocked on D-114 and D-118 |
| 4.7 | S5, the gated screen. The two composites of its own, the stabilisation conditions, the two news conditions failing open below the article threshold, and **S5 scoring with S1 absent from the registry entirely**. Blocked on D-120 |
| 4.8 | C12 GateEngine. Every failing reason recorded rather than the first, a new `gates.*` namespace, and the three gates that are structurally inert over the window exercised against fabricated rows rather than left untested. Blocked on D-117 |
| 4.9 | C14 CandidateAllocator, the live half: the proportion, the ceiling, dedup, `candidate_set`. **An unfillable slot and a gated name's slot are separate fixtures**, being separate cases. Blocked on D-116 and D-117 |
| 4.10 | The attribution write, scores and ranks frozen, `surfaced_as` and `config_version` stamped, return columns empty, and the shadow path proven against a fixture screen. Its Reads cell does not carry the tables it needs, reported. Blocked on D-110 and D-119 |
| 4.11 | C28 ConcentrationMonitor, reading `security_daily` rather than `security`, with the difference between the two on a 2022 date asserted as a number |
| 4.12 | The evening order wired and one real night run. **It produces zero candidates and that is correct**, no floor existing yet, asserted as the warm-up case rather than treated as a halt |
| 4.13 | The two-pass range run. Pass two refuses to run unless pass one's distinct dates equal the calendar's session count. **The pre-registered persistence measure computed and recorded here, per screen, with its own chance baseline**, before anything freezes. Blocked on D-115 |
| 4.14 | The gate and the allocator over the range, and the point the record starts: one explicit truncate-and-run, after which `RUNBOOK.md`'s prohibition is operative, with the sha and the frozen row count in `PROGRESS.md`. The done-when distributions measured here, and `CONFIG_REFERENCE.md`'s Consumer column filled from the composition code |

**New.** `src/StockResearcherLab.Data/Migrations/0017_selection_shape.sql`; the four components
under `src/StockResearcherLab.Pipeline/Select/`, being `GateEngine`, `ScreenEngine`,
`CandidateAllocator` and `ConcentrationMonitor`; a `ScreenDefinition` type and the per-screen
config facade in `Core`; the screen composition SQL builder beside them; test files per component
under `src/StockResearcherLab.Tests/`.

**Changed.** `ConfigStore.cs` for the seeded keys, gaining `screens.*`, `s5.*` and `gates.*` and
losing the three quota keys; `PipelineComposition.cs` for the four registrations;
`NightlyRun.EveningOrder` and `BackfillSequence` for the two orders; `SchemaParityTests` and
`guards.ps1` for the partition-child predicate; `ReadDeclarationConformanceTests` for the view.

**Written by the build, not authored.** `PROGRESS.md` gains the phase 4 row, the test count, the
HEAD sha, both range passes' wall clocks against C11's measured 48.54 minutes, the measured
`screen_score_daily` size against §16's estimate, the eight done-when figures, the persistence
figures per screen, S4's survivor caveat, and 4.14's sha and frozen row count. `FIXTURES.md`
gains this phase's fixtures. `CONFIG_REFERENCE.md` gains the Consumer column for every key this
phase wires, filled from the composition code read line by line and not inferred from the name
[`CLAUDE.md` §8].

---

## 11. Verification

**Per checkpoint:** `ci.ps1`, run against a worktree at HEAD after the checkpoint is committed and
before it is pushed.

**Per checkpoint, beyond CI**, each against the real store rather than a fixture. 4.1: `migrate.ps1`
twice, and the drop guard tried against a populated copy. 4.3: every seeded key resolved at
2021-01-04 and at the frontier. 4.4: one named ticker's S1 score hand-recomputed from `C36
RecordInspector`'s metrics panel for that date and compared to the stored value, which is what
phase 3.5 was built for. 4.5: one screen's floor read against its own trailing distribution.
4.8: one date's `gate_result` reason counts. 4.9: one date's slot fill against the proportion.
4.10: one candidate's attribution row read whole. 4.13: both passes timed, and the persistence
query run per screen. 4.14: the eight done-when figures.

**Structural, and asserted rather than reviewed:** write ownership over the registry;
`DeclaredAccess` throwing on an undeclared table and on the allocator's forbidden operation; the
read-declaration conformance in both directions; the per-screen facade throwing on another
screen's key; `SchemaParityTests` and `guards.ps1` in both directions including the partition
children; `ExpectedMonetary` unchanged.

**At sign-off:** the review runs in a session that has not committed here and asks
`BUILD_PLAN.md`'s three questions [D-67]. Two things are read at sign-off rather than measured
extra, per `CLAUDE.md` §3: the persistence figures against §5's three readings, and the
registration decision for the shadow family with D-90 alongside it, both answerable from what
this phase produced.
