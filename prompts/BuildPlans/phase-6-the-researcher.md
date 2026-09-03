# Phase 6 — The researcher

**Target** phase 6, the dossier builder producing the cached prefix and the per-candidate
blocks, the researcher client running twice with Opus 5 batched and V4 Pro synchronous,
the proposal validator with all three checks, and the cost ledger's researcher half.

**Authored** 2026-08-28, at phase 5's sign-off, as `BUILD_PLAN.md` requires of the next
phase's detail.

**Read against** `ARCHITECTURE.html` §03, §04, §07, §08, §09, §12, §16, §17, §18 and §19,
`SCHEMA.md`, `RUNBOOK.md`, `CONFIG_REFERENCE.md`, `VALIDITY.md` §3, §4 and §5,
`DECISIONS.md` D-15 to D-29, D-36, D-38, D-40, D-42, D-51, D-54, D-55, D-60, D-96, D-109,
D-110, D-124, D-126, D-130, D-132 to D-145, `FIXTURES.md`, `METRICS.md` §3,
`WORKED_EXAMPLE.md`, `GLOSSARY.md`, `prompts/prefix-template.md`,
`prompts/candidate-block.md`, `prompts/rubrics.md`, `prompts/README.md` and `CLAUDE.md`
§2, §3, §5, §6, §7, §9, §11, §13 and §14, at HEAD `7e1ff1a`.

**Sixteen claims below rest on the code, on the store or on a probe rather than on a
document, and were read or taken there** [`CLAUDE.md` §7]: the `dossier` and `proposal` definitions in
`0001_snapshot.sql` including both partial unique indexes and the absence of any CHECK or
NOT NULL beyond `date` and the three key columns; the empty `Pipeline/Decide/` folder and
the absence of any type named `DossierBuilder`, `ResearcherClient` or `ProposalValidator`
anywhere under `src/`; `ConfigResolutionTests`' `Assert.Equal(106, ConfigSeeder.Keys.Count)`
and the absence of any `researcher.` string under `src/`; `WriteOwnershipConformanceTests`'
`ExpectedOwners = 21`; `guards.ps1`'s `ExpectedMonetary = 19`; `CostLedger`'s `WriteSet`
naming `cost_ledger` alone, its `PriceKey`, and `PriceAsync` throwing on an unpriced model;
`AlertType`'s two values, `AlertTypes.Name`'s digit clause, and `0021`'s CHECK holding the
same two strings; `HaikuDigestLink`'s constructor, its `HttpClient` seam and its four-count
usage mapping; `LocalModelClient` as the OpenAI-compatible shape; `ValuationEngine.Columns`
carrying `last_two_earnings_surprises` beside a `ReadSet` that already declares
`earnings_history`; `indicator_daily`'s column list in `SCHEMA.md` §indicator_daily read
against `IndicatorEngine`; `NightlyRun.EveningOrder` ending at `NewsDigester` and its
`before` hook; `Directory.Packages.props` at `Anthropic` 12.42.0; that package's own
assembly, carrying `BatchService` over `/v1/messages/batches` and
`CacheControlEphemeral.Ttl` with `Ephemeral1hInputTokens`; the Claude Code CLI's
non-interactive surface and its reported usage, probed 2026-08-28 and recorded at D-157;
and `git rev-parse HEAD` at `7e1ff1a` with `git log --oneline -20`.

**Status** `DRAFT`, which is the only value this corpus uses for a plan. **The numbered
checkpoints in §7 are phase scope and the decision clauses in §4 are decisions; both are
authored into `BUILD_PLAN.md` and `DECISIONS.md` by a human rather than from here**
[`CLAUDE.md` §13].

**This document is archived to `prompts/spent/phase-6-the-researcher.md` before any code
is written, and that archive is checkpoint 6.1's first act.** It is stated in the header
rather than left to the end because the end is where it has failed three times: phase 2's
archive was missing half of what was issued and said so at 2.14, phase 4 archived no
prompt at all, and phase 5 archived the plan only after fourteen checkpoints had landed.
Three phases, three gaps, which makes it a pattern rather than an instance. The archive is
a verbatim copy and this document stays `DRAFT` and stays here, which is phase 5's
arrangement with the order of operations corrected.

---

## Context

**This is the phase the whole system exists to reach.** Everything built so far decides
what the model sees. Phase 6 is the first time anything decides what to do with it, and
`VALIDITY.md` §1 says in terms that the trading is the measuring instrument and this is
the instrument's first reading.

That has one consequence which governs the ordering below. **The phase produces a number
that can be narrated after the fact in a way no earlier phase's could.** A screen score is
what the definition says it is. A digest is what the model wrote. A verdict, a stated
probability and a rejection rate are all quantities where a reader who has seen them can
construct a story in which any value was expected. `CLAUDE.md` §11 and `VALIDITY.md` §4
exist for exactly this, so §6 below states the readings before §7 builds anything that
produces them.

**The phase spends real money, and the operator has authorised one live night on both
models.** Phase 5's secondary link was built and left inert on the operator's direction of
2026-08-25, and phase 5 signed off with three lines deferred because of it. That direction
does not carry forward: the fork was put and answered on 2026-08-28, and phase 6 runs its
night. **What follows from that is where C26 sits**, and it sits before the first paid
researcher call rather than after it. Phase 5's own plan named its 5.14 as the checkpoint
most likely to be struck and said that striking it would mean the phase spent money with
no ledger row against it. Here the ledger is 6.7 and the first researcher call is 6.8.

**The phase's inputs are thinner than §07 describes, and the gap was found by reading the
store rather than the document.** `prompts/candidate-block.md` names 26 core keys and five
screen blocks. Six of those fields have no column behind them: `surp1` and `surp2`, which
C09 has written null since `0001`; S2's breakout level and distance from the base; S5's
peak-to-trough decline and days since peak; and S4's largest transaction. A seventh, the
quarterly company one-liner, has no table, no writer and no phase. §4's D-153 and D-154
split those into the ones this phase closes and the ones it reports.

**And C15's declared read set, as `ARCHITECTURE.html` §03 states it, cannot build the
block it is specified to build.** The cell names five stores. The fixed core needs nine
more. This is the fourth phase running in which a component's Reads cell has been short of
what the component needs, after 4.4, 4.10 and D-125's finding about the rate, and it is
reported the same way rather than resolved in code [`CLAUDE.md` §13].

**Nothing here is a new table.** `dossier` and `proposal` were both created by
`0001_snapshot.sql` and both hold zero rows. What phase 6 adds to the schema is
constraints on two tables that have none, four columns on a third, and one extension to a
vocabulary. All of them are free now and expensive later.

---

## 1. What exists, and what phase 6 has to reach around

| Thing | State at `7e1ff1a` | What phase 6 does with it |
|---|---|---|
| `dossier` | Table, zero rows, both partial unique indexes, no NOT NULL beyond `date` | Constrains it at 6.2, writes it at 6.5 and 6.6 |
| `proposal` | Table, zero rows, three-column key, no CHECK anywhere | Constrains it at 6.2, inserts at 6.8 and 6.9, updates status at 6.10 |
| `cost_ledger` | Table, populated by C26's digest half since 5.14 | Gains the researcher half at 6.7 |
| `alert` | Table, one writer, two-value vocabulary closed by `0021` | Gains a second writer and two values at 6.7 |
| `CostLedger` | Built, `IWriteOwner`, prices from `cost.price_per_mtok_usd`, throws on an unpriced model | Extended, not replaced. The price key gains two entries and not four keys |
| `HaikuDigestLink` | Built, Anthropic SDK 12.42.0, `HttpClient` seam, maps four usage counts | The shape 6.9's batch path is built beside. It is not reused directly: it is a digest link |
| `Anthropic` 12.42.0 | Pinned, referenced by the Pipeline. **Carries `BatchService` over `/v1/messages/batches` and `CacheControlEphemeral.Ttl` with the extended-TTL beta** | 6.9's two hard requirements, D-22's batch and its one-hour cache, are already in the package [D-157] |
| `LocalModelClient` | Built, OpenAI-compatible, health and loaded-model name | The shape 6.8's V4 Pro client is built from |
| `researcher.*` | Seven rows in `CONFIG_REFERENCE.md`, all `unverified`, **none seeded** | Seeded at 6.3, Consumer column filled from the composition code |
| `cost.annual_budget` | Documented at 100, **unseeded**, Consumer `CostLedger`, `unverified` | Seeded at 6.3, wired at 6.7 |
| `prompts/rubrics.md` | 213 lines of authored product, never read by code | Read by C15 at 6.5, at the config version in force |
| `prompts/prefix-template.md` | The assembly specification, seven parts, never executed | Implemented at 6.5 |
| `prompts/candidate-block.md` | The block specification, 26 core keys, five screen blocks | Implemented at 6.6 |
| `researcher_memory`, `calibration` | Tables, zero rows, writers are C23 and C24 in phase 8 | Read empty at 6.5. D-149 settles what empty renders as |
| `NightlyRun.EveningOrder` | Ends at `NewsDigester` | Gains C15 and C16 at 6.12. The morning job is separate and is not in this array |

**Three things it must not reach around.**

`attribution` is frozen. 4.14 wrote 74,767 rows and `RUNBOOK.md`'s prohibition is
operative [INVARIANT 4, D-40]. C15 reads it for `score_within_each` and writes nothing
there, and no checkpoint in this phase touches it.

`prompts/rubrics.md`, `prompts/prefix-template.md` and `prompts/candidate-block.md` are
product and are authored [`prompts/README.md`]. A build that finds one of them wrong
reports it. **A change to any of the three splits accumulated history**, and on this phase
the accumulated history is one night, which makes a change cheap now and is not a reason
to make one casually: the point of getting it right before the first night is that the
first night is when it stops being free.

`ARCHITECTURE.html` and `CLAUDE.md` are human-edited only [`CLAUDE.md` §13]. Four
checkpoints below name a cell in §03 that a human amends in that same checkpoint. That is
the arrangement 5.9 used and it is stated per checkpoint rather than assumed.

---

## 2. Four rules, before the first checkpoint

**One. The prefix is byte-identical within a night, and the ways to break it are known in
advance.** INVARIANT 6, D-20, and `prompts/prefix-template.md`'s own list. No clock read,
no `Dictionary` or `HashSet` enumeration order reaching output, `CultureInfo.InvariantCulture`
on every number and date, no trailing whitespace, a fixed line ending, and the string built
once at the start of the run and reused rather than rebuilt per call. **A break here
produces no error**: the run completes, the proposals look normal, and the input bill
roughly triples. `prefix_hash` on the dossier row and the cache hit rate in `cost_ledger`
are the two detectors and 6.5 and 6.7 build them in that order.

**Two. The researcher never computes.** INVARIANT 9 and D-16. Everything arrives
precomputed, which is a constraint on C15 as much as on the model: where §07 names a field
the store does not hold, the answer is a column on the component that owns that kind of
number, never a calculation inside the dossier builder. §4's D-153 puts four technical
figures in C08 for this reason and not for tidiness. **The one place this rule is easy to
break without noticing is S5's screen block**, which §07 describes as "all three
stabilisation test results" and which no table holds. 6.6 renders the gate's inputs and
not its verdicts, so the gate keeps one definition [INVARIANT 2].

**Three. The validator exercises no judgment.** §09 states it and it is the reason the
dossier is a stored artefact rather than a string assembled in memory: verifying a citation
needs the exact text the model was given, byte for byte, after the fact. Every check is
arithmetic or string matching against something already on disk. **D-150's tolerance is the
one place a judgment could enter**, and it is a stated number rather than a disposition.

**Four. Two components write `proposal` and each owns one operation.** C16 inserts, C17
updates `status` and `rejection_reason` and nothing else [INVARIANT 10 as amended,
`SCHEMA.md` §proposal]. The stage registry declares the operation and the column set and
`WriteOwnershipConformanceTests` asserts no two components claim the same triple. This is
the pair the amended invariant was restated for, alongside `attribution` and the
order-fill-position group, so it is the case the rule was written against rather than a
new one.

---

## 3. What a night costs, and what phase 6 runs

**§17's table is an estimate resting on two other estimates.** It prices 1.16M cache-write
tokens, 5.85M fresh input tokens, 32.5M cache-read tokens and 1.10M output tokens a year,
and every one of those four lines is a token count from §07 multiplied by a call count
from §08. The two token counts are 4,600 for the prefix and 800 for the block. **Neither
has ever been measured**, and phase 5 measured the one comparable estimate in this corpus
and found it out by a factor of two: §07 put an article body at five to eight hundred
tokens and 5.1 measured a median of 1,248, which invalidated the article cap the design
had derived from the estimate [D-143].

So 6.1 measures both before 6.5 and 6.6 build against them, with bands and stopping rules
in §5. What follows is what the phase spends **if the estimates hold**, and it is stated
as conditional rather than as a figure.

| Line | On the night | Note |
|---|---|---|
| One prefix cache write, two models | ~4,600 tokens each | Paid once, read 28 times |
| 28 blocks, two models | ~800 tokens each | Fresh input at full rate both times |
| 28 cache reads, two models | ~4,600 tokens each | The line the cache exists for |
| Output, two models | Bounded by `researcher.max_tokens` | The line that cannot be compressed [D-21] |
| One batch submission | Halves every Opus 5 line [D-22] | Needs a real round trip to prove |

**One night at §17's rates is single-digit cents and that is not the reason to be careful
about it.** The reason is that a night which silently misses the cache costs roughly three
times what it should and looks identical in every output, so the first night is worth
instrumenting completely rather than running cheaply. 6.7 is what makes it visible and it
lands before 6.8.

**Phase 6 runs one night, not a series.** Twenty consecutive clean nights is phase 10 and
is not brought forward. `VALIDITY.md` §3 puts the validator rejection rate at four to six
weeks and the calibration report at six to nine months, so nothing this phase measures is
concluded from, and §6 says so before §7 measures anything.

**No backfill of dossiers or proposals, on the grounds phase 5 used for digests and one
more.** A dossier built today over a 2023 date is assembled from stores that hold today's
config resolution unless every read is as-of, which is INVARIANT 13 applied to nine stores
rather than one. A proposal produced today is model output produced today and is not
point-in-time. Nothing downstream reads either over history. And `VALIDITY.md` §6 names
model training contamination as a threat with the mitigation "backfill is never used to
evaluate the researcher", so a backfilled proposal would be a row that the document
mitigating this threat says must not exist.

---

## 4. Decisions that need authoring before code

Twelve, D-146 to D-157. Each is drafted here in the corpus's clause form for the operator
to adopt into `DECISIONS.md`. **Nothing below is authored by this document** [`CLAUDE.md`
§13]. The blocked-on column in §7 names them.

### D-146, C15's read set, and the amendment §03 needs

**C15 DossierBuilder's Reads cell in `ARCHITECTURE.html` §03 names `candidate_set`,
`headline`, `researcher_memory`, calibration and `market_context_daily`. The block it is
specified to build needs nine stores more.**

`prompts/candidate-block.md` gives the fixed core 26 keys in eight groups. Read against
`SCHEMA.md`, the fixed core alone needs `valuation_daily` for the seven quality and two
valuation keys, `indicator_daily` for the eight technical keys, `flow_daily` for
`insnet90`, `sentiment_derived_daily` for `artz90` and `dsent730`,
`percentile_cell_daily` for the paired percentile on every one of those, `security_daily`
for sector, size bucket and market cap at the point-in-time grain D-92 created, `events`
for `dte`, `news_digest` for the digest text, and `attribution` for `score_within_each`.
`price_daily` is deliberately absent: `price` and `median_dollar_volume_20d` are both on
`indicator_daily`, and a raw series must never reach this component [INVARIANT 9].

**`ReadDeclarationConformanceTests` holds the cell against the code in both directions**,
so this fails on the first commit of 6.6 whichever way the disagreement is resolved. The
resolution is an amendment to the cell by a human, in the checkpoint that needs it, which
is the arrangement 5.9 used for C36's two tables.

**This is the fourth instance of one pattern and it is counted rather than reported
fresh.** 4.4 reported five stores the catalogue did not name, 4.10 reported a Reads cell
short of the tables the write needed, and D-125 recorded the rate at which this column
drifts. What is new here is only the size: nine stores, on the component with the widest
read set in the system after C36.

### D-147, `dossier`'s grain rule and its constraints

**`dossier` holds two row shapes in one table and nothing in the database says so.** The
prefix row carries `date`, `prefix_text` and `prefix_hash` with `ticker` and `block_text`
null; the block row carries `date`, `ticker` and `block_text` with the two prefix columns
null. `0001` created both partial unique indexes, `dossier_prefix_ux` on `date` where
`ticker IS NULL` and `dossier_block_ux` on `(date, ticker)` where `ticker IS NOT NULL`, so
the grain is enforced and **the shape is not**: nothing stops a prefix row with a null
`prefix_text`, which is a night whose cache entry cannot be reconstructed and whose
citation check has nothing to check against.

The decision states one CHECK expressing both shapes as a disjunction, so a row is either
a well-formed prefix or a well-formed block and cannot be neither.

**And it states the idempotence rule, which is a run scope rather than a row identity**
[D-132's shape one table over]. C15 deletes the date it is about to rebuild and reinserts,
both operations its own, which is C14's shape and C33's. `ON CONFLICT` alone would leave
behind a block for a name the night no longer surfaces, and two runs of a stage over one
date must produce identical output.

**The prefix row is written before the blocks and both are written before any call goes
out.** §09 states the reason: check 2 needs the exact text the model was handed, and a
dossier assembled in memory would save a table and cost the only defence this system has
against a confidently invented number.

### D-148, `proposal`'s closed vocabularies and bounds

**`proposal` has a three-column key and no constraint on anything else.** `verdict` can
hold any string, `status` can hold any string, and `p_target_before_stop` is an unbounded
`numeric` on a column §08 defines as a percentage between 0 and 100.

The decision closes `verdict` to `BUY` and `PASS`, closes `status` to a stated set, bounds
the probability, bounds `stop_pct` and `target_pct` positive, and requires `model_id`
non-blank. **The three precedents are `gate_result.reasons` at `0018`, `alert.alert_type`
at `0021` and `news_digest.provider` at `0022`**, all closed the same way for the same
reason: a vocabulary held in code alone leaves the column able to carry a string no reader
can interpret. Here the column that would carry it is the one every downstream comparison
groups by.

**`status`'s vocabulary is the validator's outcome set and is stated in this decision
rather than in D-150**, because the column exists whether or not check 2 works the way
D-150 says. It carries at minimum a pending state, an accepted state, and one rejection
state per check, so a rejection rate can be read per check rather than pooled. §09's
failure table gives three distinct rejection conditions and pooling them would make the
per-model measurement `VALIDITY.md` §3 calls the earliest readable evidence unable to say
what was rejected.

**`p_target_before_stop` is not money and does not enter `guards.ps1`'s monetary
declaration.** `stop_pct` and `target_pct` are percentages and neither is money.
`ExpectedMonetary` stays at 19 and 6.2 asserts it.

### D-149, what the prefix renders when a store is empty

**`researcher_memory` and `calibration` hold zero rows and their writers are C23 and C24
in phase 8.** `prompts/prefix-template.md` gives them sections 4 and 5 of seven. So on
every night phase 6 runs, and on every night until phase 8's first monthly run, two of the
prefix's seven sections have nothing to render.

**This is a byte-identity question rather than a formatting one.** Two answers are
available and they differ in what happens on one specific night. If an empty section is
omitted, the prefix gains two sections on the night phase 8 first writes a lesson, which
is a prefix change with a cause that is invisible from the prefix itself. If an empty
section is rendered with its heading and an explicit statement that it is empty, the
prefix's shape is constant from the first night and what changes later is its content,
which is what §prefix-template already says about market context being the only part that
differs every night.

**The decision takes the second and states why**: a section that appears is a change to the
document's structure and a section that fills is a change to its content, and the
calibration table the model is being shown is a thing whose absence is worth stating to it
rather than hiding from it. A model told the calibration table is empty knows it has no
feedback yet; a model shown no calibration section cannot distinguish that from a system
that does not calibrate.

**It also states what an empty section must not do**, which is vary. The rendering is
fixed text, so two nights with both stores empty produce identical bytes in those two
sections, and the snapshot fixture at 6.5 covers it.

### D-150, check 2, the citation rule

**§09 says every number cited in the thesis is matched back against the dossier and gives
one worked example: a thesis resting on a free cash flow yield of 8.2 percent when the
dossier said 6.2. That is a rule at the level of an illustration and a build needs it at
the level of a grammar.**

The decision states four things.

**What is extracted.** Numeric tokens from `thesis` and `counter_argument`, with a stated
grammar covering the decimal separator, a leading sign, a percent sign, a basis-point
suffix, and multipliers written as words. `WORKED_EXAMPLE.md` §8's own example is "ROIC
still improving" mapping to `droic4q +180bp`, which contains no numeral in the thesis at
all, so the grammar has to handle a thesis whose only figures are qualitative and produce
zero citations rather than a failure.

**What it is matched against.** The block row's own values, as stored, for that ticker on
that date. Not the store, which may have been recomputed. The block, which is why D-147
persists it before any call goes out.

**The tolerance, as a stated number.** A model that reads `fcfy 6.2` and writes "a free
cash flow yield above 6 percent" has cited the figure correctly and rounded it, and a rule
with no tolerance rejects that. The decision states the tolerance and states it as a
relative band rather than an absolute one, because the block's values span basis points
and market capitalisations.

**What is excluded by name.** `stop_pct`, `target_pct`, `p_target_before_stop`,
`horizon_days` and the breakeven the first two imply are the model's own output, are not
in the dossier, and would every one of them be an unmatched numeral. They are excluded by
name rather than by a rule about position, so a model that mentions its own stop in its
thesis is not rejected for it.

**What an unmatched numeral does.** It rejects that proposal, and the row records the
cited value alongside the block's value, which is §09's failure table as written.

**The false-rejection risk is real and is measured rather than argued.** §18 alerts above
5 percent, and §6 below pre-registers what a rate at or near zero and a rate above the
alert each mean, so the first night's number is read against something written before it.

### D-151, the batch path's two halves

**`RUNBOOK.md` already states this: the decide stage is asynchronous, the evening run
submits and exits, and a separate job completes the pipeline when the batch returns.** The
decision states what that means for the three things that make a stage a stage.

**A submission is a completed stage and not a partial run.** C16's evening half runs, does
its work, writes what it has and returns. That is not the partial run `CLAUDE.md` §6
forbids: nothing downstream reads a proposal that does not exist yet, and the zero-row
halt keys on a stage that writes having written nothing, which is why the submission
records what it submitted.

**The run log carries a waiting state rather than a stall.** `run_log`'s `status` column
gains the value and C27 keeps its single writer. Without it a night that submitted
successfully and a night that died after the digest step are the same row.

**The morning job is not in `NightlyRun.EveningOrder`.** It is a separate command with its
own sequence, C16's collect half and then C17, and it runs against the date the evening
blessed rather than against a clock read [INVARIANT 11]. Phase 7 extends that sequence
with the risk gate and the broker; phase 6 ends it at the validator.

**The synchronous model does not wait for the batched one.** V4 Pro runs inline in the
evening and its proposals are complete before the night ends. That is a difference in when
two portfolios' evidence lands and is not a difference in what they were given, the dossier
being byte-identical to both, and it is stated because a reader meeting two proposals with
different write times will otherwise wonder whether they saw the same night.

### D-152, what the 09:00 deadline halts, and what phase 6 can prove of it

**§18 says: batch results not returned by 09:00, skip the night entirely, no orders for any
portfolio, since exposure matching requires the primary research portfolio to have decided.
Alert.**

**Two of those four clauses name things that do not exist until phase 7.** There is no
order, no portfolio runner and no exposure matching. This is D-139's situation one phase
on and it takes D-139's treatment: **the line is narrowed to what is observable now and
re-asked at phase 7 rather than marked met here.**

What phase 6 proves is that on a deadline miss the collect job records the miss, raises the
alert, writes no `proposal` row for the batched model, and returns not completed. What it
does not prove is that no order is produced, because nothing produces orders.

**The alert is the part that is fully buildable now** and it is the reason D-156 gives C26
a write rather than giving C16 one: §18's alert conditions for this row belong to C16 by
its own reading, and giving a second component a write to `alert` in the same phase as the
first would be two amendments to §03's Writes column where one will do. The decision states
which component raises it and that the vocabulary extends with it.

### D-153, four technical columns C08 gains, and the backfill they do not get

**§07's S2 and S5 screen blocks name four figures the store does not hold**: breakout level
and distance from the base for S2, peak-to-trough decline and days since peak for S5. The
phase 2 carried obligation to phase 6 predicted exactly this and said the screen blocks are
the dossier builder's.

**They are C08's and not C15's** [INVARIANT 9, §03's C08 row: all technicals computed
locally]. A distance from a base is a technical indicator, and computing it inside the
dossier builder would put a price series in front of the component whose entire job is to
ensure the model never sees one. So the columns land on `indicator_daily` with C08 as their
writer, in migration `0024`, and C15 reads them.

**They are null over the frozen window and that is stated here rather than discovered
later.** C08 fills them from the night they land forward. Phase 6 reads only the current
night, so nothing in this phase is affected. **What is affected is any later analysis over
history**, and the two components that would read them are C22 ScreenTuner and C24
CalibrationReporter in phase 8, which is where the obligation is filed.

**A C08 range re-run is available and is priced rather than assumed.** `PROGRESS.md`'s
measured figures put C08 over the full range at 9.44 minutes at `run_log` 1764 and 21.45 at
1720, at zero provider units, so backfilling these four columns costs a quarter of an hour
of compute and no money. **It is not taken here and the reason is not cost**: it is a
rebuild of a table five other stages read, it lands inside `SCREEN_LIFECYCLE.md` §6.7's
rebuild-forcing bundle alongside the `TradingCalendar.SessionsAsync` obligation, and
bundling it is what that obligation asks for. The decision records that the option exists,
what it costs, and that it is bundled rather than declined.

### D-154, the one-liner is not built, and `headline.source` stays null

**Two dossier inputs are reported rather than closed, and the disposition is the one
`slot_filled` and `attribution.digest_provider` already have** [D-124, D-135].

**The company one-liner.** §07 gives it a row in the local enrichment table: written
quarterly, small and mid caps only, roughly 30 tokens, closing the gap where the model has
no background on a $500M company. It has no table in `SCHEMA.md`, no writer in §03, and no
phase in `BUILD_PLAN.md`. Building it means a store, a writer, a quarterly cadence nothing
else in this system has, and a local-model call whose output is product rather than
evidence. **That is a sub-project and not a checkpoint**, and taking it inside phase 6
would be a build session widening its own scope [`CLAUDE.md` §3]. The block omits it and
the omission is visible in the block rather than silent.

**`headline.source`.** The carried obligation from phase 5 says the provider's `news`
payload carries no field that maps to it and that deriving the host from `link` is a choice
rather than a read. The dossier is where a source would matter, which is why the obligation
was owed here. **The answer is that it stays null and the S3 and S5 screen blocks render
their three headlines without a source line.** Deriving a hostname would put a value in the
block that no provider sent, and §07's whole argument for the block is that everything in
it arrives precomputed from something that was actually read.

**Both are closed as reported, which is a closure of the obligation and not of the gap.**
The row in `BUILD_PLAN.md` moves from open to answered with the answer being that nothing
is built, which is what an obligation asking a question is entitled to receive.

### D-155, the two research portfolio rows

**`cost_ledger` carries `portfolio_id` and `proposal` does not.** `proposal`'s grain is
ticker by day by model and the model id is what distinguishes the two research portfolios'
rows; `cost_ledger`'s grain is per call and it carries both. So C26 needs a portfolio id at
6.7 and `portfolio` holds no rows.

**The decision seeds two rows and not four.** `Research: Opus 5` and `Research: V4 Pro`,
with `provider`, `model_id`, `use_batch`, `is_primary` and `state` per §12's column table,
and `Research: Opus 5` carrying `is_primary` per D-36. **Screens and Random are not seeded
here**: they have no model, nothing in phase 6 reads them, and C25 PortfolioRunner is phase
7's. Seeding a row a phase does not use is a row nobody has read the composition code for.

**`portfolio`'s writer is configuration and not a stage** [`SCHEMA.md` §portfolio], so this
is a seed alongside the config keys rather than a component, and `ExpectedOwners` stays at
21 plus whatever §7 adds.

### D-156, C26 gains a write to `alert`, and the vocabulary extends with it

**§18 gives C26 two alert conditions**: cache miss rate above 20 percent, and trailing cost
run rate above target. Both are cost-ledger readings and both are phase 6's first real
exposure. `monitor.cache_hit_rate_min` is seeded at 0.80 and `cost.annual_budget` at 100,
and **neither has a consumer today**.

**`0021`'s own comment states the rule this decision follows**: section 18 names four
conditions whose owner is not C28, none of their components declares a write to `alert`,
and "when a phase gives one of them a writer it extends this constraint, which is a
migration and therefore visible". Phase 5 declined to extend it because it had no condition
to add. Phase 6 has two.

So the decision names the two type strings, adds them to `AlertType` and to the CHECK in
one migration in one checkpoint, and **names the third change a human makes in that same
checkpoint: §03's C26 Writes cell, which today reads `cost_ledger` alone.**
`WriteDeclarationConformanceTests` holds that cell against the code in both directions and
has found two drifts already, so the amendment is not optional and the checkpoint is where
it lands.

**A budget alert is an alert and not a halt, and that is stated.** §18's row for the
trailing cost run rate says "drop to the cheaper model tier and log the switch", which is a
behaviour phase 6 does not build: there is no tier ladder, the two models are two
portfolios rather than two tiers, and switching one would change what a portfolio is
mid-comparison. **The decision records that the alert fires and the switch does not
happen**, and files the tier question rather than answering it. An automated model switch
inside a running comparison is exactly the kind of change §12 says invalidates the record.

### D-157, the researcher runs on the API and not on the Claude Code CLI

**The question is asked because the alternative is sitting there.** A Claude subscription
already pays for this system to be built, the CLI has a non-interactive mode, and using it
for C16 would appear to make the researcher free. It is asked and answered here rather
than left for someone to notice in a year, which is `CLAUDE.md` §11's rule about recording
a decision before the number that would justify it exists.

**The CLI is more capable than the question assumes, and that is stated first.** Driven as
`--print --output-format json --tools "" --system-prompt <file> --no-session-persistence`
it gives a replaced system prompt with no per-machine scaffolding, no tools at all, a JSON
schema on the output, and complete token accounting. **Measured on 2026-08-28**:
`prompts/rubrics.md` as the system prompt is **3,491 tokens**; a cold call costs
**$0.0357** and writes them to the **one-hour** cache; the two calls after it read all
3,491 and cost **$0.0025**, a fourteenfold drop across separate process invocations. So
the cache economics §07 rests on are reproducible through the CLI and INVARIANT 9 and
INVARIANT 6 are both reachable. Nothing below rests on the CLI being unable to do this.

**Three design reasons stand independently of cost.**

**It can serve only one of the two research portfolios.** The CLI is Anthropic-only and
V4 Pro reaches its own endpoint whatever happens, so the two portfolios would differ in
transport, in caching and in the sidecar below. §12 holds the research-against-research
comparison to "every variable except the model held identical, including the dossier, the
rubrics, the arbitration steps and the random seed", and `VALIDITY.md` S-1 is the claim
that confounding lands on. **This is the reason that would stand at any price.**

**There is no Batch API on it**, so D-22 would have to be superseded and §17's Opus line
roughly doubles: the same night un-batched prices at about $0.36 against §17's batched
year of $45. D-151's submit-and-exit, D-152's deadline behaviour and `RUNBOOK.md`'s
asynchronous decide stage all exist because of batch and would all be rewritten.

**A second model answers on every call and nothing asked it to.** Every invocation
measured also billed `claude-haiku-4-5` at **537 input tokens, never cached, $0.00061 a
call**, roughly $4.30 a year. It is not the digest chain and no row in this system would
record it. That is D-144's finding one layer up: a model nobody chose answers, and the
only place it appears is a field nobody reads.

**And a fourth reason that is about the record rather than the run.** The CLI updates
itself. Its scaffolding, its defaults and that sidecar can change between two nights with
nothing in `attribution.config_version` to segment on, which is the pooling §12 forbids.
The SDK is pinned in `Directory.Packages.props` and a version change is a commit.

**The cost argument runs the other way from the intuition and is stated with its
uncertainty.** §17's ~$50 combined is a design estimate and `PROGRESS.md` records it as
unmeasured; 6.12 is what fills that cell. Against it, the cheapest subscription is $240 a
year and the plan that would actually carry 28 candidates a night on Opus over 252 nights
is several times that. The subscription also removes no API bill, V4 Pro being on its own
endpoint either way. **The sharpest form of it is contention rather than price**: the
subscription is already spent on building this system, and pointing it at running the
system puts the nightly job against the development work, whose resolution is a plan
upgrade costing twenty to fifty times the API bill.

**What the decision does not say.** It does not say the CLI is unsuitable for this corpus.
C23 LessonWriter in phase 8 is monthly, is one call on aggregate statistics, produces prose
for a person, and has no cache economics, no cross-model comparison and no per-night
byte-identity requirement. **Not one of the four reasons above reaches it**, and the
decision records that rather than leaving the question to be re-asked with the same
measurements missing.

---

---

## 5. The measurement 6.1 takes, and what stops the build

Six measurements, from a scratch app outside the repository, transcript to
`docs/evidence/phase-6/`, keys never printed. Nothing is committed under `src/` and the
scratch app is deleted at 6.2, which is 5.1's arrangement unchanged.

| # | What is measured | Against |
|---|---|---|
| 1 | The assembled prefix's real token count, counted by the model that will read it | §07's ~4,600 |
| 2 | One assembled candidate block's real token count, on a name surfaced by one screen and on a name surfaced by two | §07's ~800 |
| 3 | What a real response reports for cache write and cache read, and that a second call inside the window reads the prefix rather than rewriting it. **The SDK half of this is already answered and is not re-asked** | D-22, §17's cache lines |
| 4 | One batch submission end to end: submission, poll, retrieval, and the real round-trip time | §08's "completing by 09:00" |
| 5 | V4 Pro's endpoint, its exact model id, and whether it supports prompt caching at all | §17's V4 Pro column, which assumes it does |
| 6 | The four token counts each provider reports, mapped onto `cost_ledger`'s four columns | 5.14's mapping, one provider over |

**Two of the six are partly answered before the sweep runs, from a file already on disk
and from a probe already taken, and they are recorded here rather than re-asked** [D-157].

**Measurement 3's SDK half is answered.** `Anthropic` 12.42.0, already referenced by the
Pipeline and already carrying `HaikuDigestLink`, exposes `CacheControlEphemeral` with a
`Ttl` property, the `extended-cache-ttl-2025-04-11` beta, `Ephemeral1hInputTokens` and
`Ephemeral5mInputTokens` on usage, and a full `BatchService` over `/v1/messages/batches`
with create, retrieve, list, cancel, delete and results. **So the one-hour cache and the
batch endpoint are both present in the pinned package** and neither can halt this phase.
What 6.1 still measures is the runtime half: what a real response reports and whether a
second call inside the window reads rather than rewrites.

**Measurement 1 has a first data point.** `prompts/rubrics.md` assembled as a system prompt
measures **3,491 tokens** [D-157, probed 2026-08-28]. §07 puts the whole prefix at ~4,600,
so the rubrics are roughly three quarters of it and the other six sections have about
1,100 tokens of room. **That is a partial reading and not the measurement**: sections 1, 2
and 7 are static text nobody has written yet, section 6 is a real night's market context,
and 4 and 5 render empty under D-149. 6.1 assembles all seven and counts them.

**Measurements 1 and 2 need a real prefix and a real block, which is a circularity and is
resolved the way 5.1 resolved its own.** The sweep assembles them by hand from
`prompts/rubrics.md` as it stands and from one real candidate's stored values, read out of
the database by query. That is not the component and is not meant to be: it is the same
text the component will produce, assembled once, to find out how large it is before
anything is built to produce it.

### What stops, and what only gets recorded

**This is phase 5's stopping rule extended to cover the tokens, and the reason it is
extended is 5.1's own result.** §07 put an article body at five to eight hundred tokens,
5.1 measured 1,248, and the article cap the design had derived from the estimate had to be
replaced by D-143. The prefix and block estimates are of exactly the same kind, from the
same section, and §17's entire cost table is built on them.

| Measurement | Band | What the build does |
|---|---|---|
| Prefix tokens | Within roughly 15 percent of 4,600 | Record and proceed |
| Prefix tokens | Materially above 4,600 | **Halt before 6.5 and report.** Recompute §17's cache-write and cache-read lines at the measured count and state what the $50 total becomes. A prefix token is written once and read 28 times, so this is the cheap direction per night and it still moves the annual figure the design closed on |
| Block tokens | Within roughly 15 percent of 800 | Record and proceed |
| Block tokens | Materially above 800 | **Halt before 6.6 and report.** A block token is fresh input paid 28 times a night at full rate, which §07 prices at roughly seven times a prefix token. This is the expensive direction and it is a decision rather than a number to seed and continue past |
| One-hour cache, at runtime | A second call inside the window reads the prefix | Record and proceed |
| One-hour cache, at runtime | It rewrites rather than reads | **Halt before 6.9 and reopen D-22.** §17 states that five-minute caching under batch timing lands the year near $112, which is worse than not batching at all. **The SDK half of this row is retired** [D-157]: the TTL and the batch endpoint are both in the pinned package, so what remains is behaviour under batch timing rather than availability |
| V4 Pro caching | Present | Proceed |
| V4 Pro caching | Absent | **Report and wait.** §17's V4 Pro column prices cache writes at $0.50 and cache reads at $0.12 and no other document checks that the provider has a cache. The phase can proceed on an uncached V4 Pro; what cannot proceed is §17's figure standing unqualified |
| Batch round trip | Inside the window between 18:40 and 09:00 | Record and proceed |
| Batch round trip | Outside it | Record and report. D-152's deadline behaviour is what handles it and the measurement says how close the design is to needing it |

**Three fallbacks will suggest themselves at a token halt and all three are out of scope
for a build session.** Trimming sections from the prefix, which is the five rubrics and
therefore the strategy itself [D-20, `prompts/rubrics.md`: "these are where the strategy
actually lives"]. Dropping a screen block, which is the evidence the S3 and S5 rubrics turn
on, both of those rubrics saying in terms that cause classification dominates and the cause
arrives in the block. Lowering the candidate count, which is a change to the size quotas,
to D-32's caps and to what the universe exists to produce. **Each is an authored decision
and a report, never a build adjustment** [`CLAUDE.md` §3, §13]. They are named here so that
a session meeting the halt recognises them as out of scope rather than deriving them fresh
and finding them reasonable.

---

## 6. Pre-registered readings, written before anything produces them

**Phase 4 pre-registered its persistence measure with three readings and its own chance
baseline before any score existed. Phase 5 pre-registered its sweep's stopping rule. Phase
6 is the phase where this system's instrument first takes a reading, and it is the phase
with the most room to narrate a result after seeing it.**

Two quantities become measurable here for the first time. Both are recorded in
`PROGRESS.md` for every night the phase runs. **Neither is a done-when line unless the
operator authors it as one**, and neither is concluded from at this phase.

### The validator rejection rate, per model

`VALIDITY.md` §3 puts this at roughly 7,000 proposals per model a year and four to six
weeks to a usable answer, which makes it the earliest thing in this system that
distinguishes the two models. §09 says it is a measurement rather than plumbing and that it
is independent of trading results entirely.

| Reading | What it means, written in advance |
|---|---|
| At or near zero | **The first reading is that check 2 is matching nothing**, not that the models invent nothing. D-150's extraction grammar is the suspect and the models are not. A validator that rejects nothing is indistinguishable from a validator that is not running |
| Above `validator.rejection_rate_alert`, 5 percent | §18 reads this as the prompt, the schema or the model having changed. **On a first night there is no before**, so on this phase it reads as the prefix or the rubric being wrong rather than the model being unreliable |
| Between the two, differing between the models | The design working. This is the per-model rate of invented figures that D-54 says is available in weeks where the portfolio comparison needs a year |

**Read per check and not pooled**, which is why D-148 gives `status` one value per
rejection condition. A rate of 4 percent that is entirely check 1 is a schema problem and a
rate of 4 percent that is entirely check 2 is a citation problem, and pooled they are the
same number.

### The two models' agreement

Both models receive a byte-identical prefix and a byte-identical block, which makes the
first night a paired sample on the primary claim's own instrument.

**This is already pre-registered and the threshold is not this document's to set.**
`VALIDITY.md` §4 states S-1 as settled when "the two research portfolios' BUY sets diverge
on at least 20 percent of candidates and the peer-relative outcome difference exceeds 1
percentage point at 21 days over at least 1,000 paired observations". So the divergence
number exists, it is 20 percent, and it was written before any data. What this section adds
is the reading of one night's figure against it, which is a different thing from a
threshold.

| Reading | What it means, written in advance |
|---|---|
| Divergence far below 20 percent | The two-portfolio design measures less than it was built to, and D-54's question of whether both are kept is answerable far earlier and far more cheaply than a year of returns. It is a finding about the design, not about either model |
| Divergence near what independent judgment would produce by chance | At least one model is not reading the evidence. **The rubric and the block are the suspects before either model is**, because both received identical text |
| Divergence between those | What the design expects, and `WORKED_EXAMPLE.md` §9 is its illustration: BUY at 58 against PASS at 34 on the same dossier, with the losing model naming the concern the winning one put in its counter-argument |

**One night is one night, and the sample is 28 candidates against §4's 1,000.** Nothing is
concluded, S-1 is not touched, and the reading is recorded so that the four-week figure has
a first point rather than starting from nothing. `VALIDITY.md` §4's minimum evaluation
period is 12 months regardless of what the numbers do earlier, and this section does not
shorten it.

**Both readings go in `PROGRESS.md`, and the measured-figures table gains a row where a
figure moves from estimated to measured.** A measure taken and not written down is not a
measure.

---

## 7. Checkpoints

Twelve, one commit each, `Phase 6 / 6.n - what it did`. Run `ci.ps1` per checkpoint.

**The order follows five facts, in decreasing force.**

**The phase's cost model has never met a real call.** Two providers, one of them never
called by this system at all, and §17's whole table rests on two token counts nobody has
measured. So 6.1 is a sweep and nothing is built against an assumed size.

**Schema is free exactly once.** `dossier` and `proposal` both hold zero rows. All of
D-147's shape CHECK, D-148's vocabularies and bounds, and D-153's four columns land in one
migration at 6.2, with `SCHEMA.md` in the same checkpoint and the parity tests running in
both directions.

**The ledger precedes the first paid call rather than following it.** Phase 5's plan named
its own 5.14 as the checkpoint most likely to be struck and said that striking it would
leave the phase spending money with no ledger row against it. Here that ordering is
inverted: 6.7 builds C26's researcher half against injected responses, needing no paid
call, and 6.8 makes the first one. The carried obligation reads "before the first full
night"; this is stronger and costs nothing.

**A path must be exercisable alone before it can be trusted in a sequence.** 6.8 builds the
synchronous path and proves it against its real provider; 6.9 builds the batch path and
proves it against its own. 6.12 then composes two known-good things rather than debugging
two unknowns through a night.

**And the openable checkpoint lands as early as it honestly can, which is 6.11.** Phase 3.5
exists because three weeks of phase 3 produced complete records and nothing anyone could
open, and the rule taken from it is that a phase puts its openable checkpoint early.
**6.11 is not early and cannot be, and that is stated rather than left to be asked.** The
panel reads `dossier` and `proposal`. `dossier` holds nothing until 6.5 and 6.6 write it
and `proposal` holds nothing until 6.8 inserts into it, so the earliest position at which
the panel can show anything is after 6.8. The one checkpoint between there and here is the
validator whose status and reason the panel displays, and a panel showing a proposal
without saying whether it was accepted is a panel that has to be re-opened somewhere else
the moment a proposal looks wrong.

**Two earlier positions were considered and both are worse.** A dossier-only half at 6.6,
showing the prefix hash and the block with nothing beside them, is a query with a URL: it
answers what was sent and not what came back, and the first thing anyone will ask of a
block is what the model made of it. Folding the panel into 6.10 makes it the last work in a
checkpoint whose done-when is the validator, which is what gets cut when 6.10 runs long,
and it spans two projects and two conformance surfaces. **If the operator prefers the
earlier half, that is a scope call and not a build one**, and it costs one extra checkpoint
and one extra amendment to the same two documents.

| # | Scope |
|---|---|
| 6.1 | **This document is archived to `prompts/spent/` here, before a line of code.** Then the sweep, before any component and before any schema: §5's six measurements from a scratch app outside the repository, transcript to `docs/evidence/phase-6/`, keys never printed. **It carries §5's stopping rules and they cover the token counts as well as the cache**, a prefix or block materially above §07's estimate halting the build and reporting rather than being seeded and built against. Nothing is committed under `src/` |
| 6.2 | All schema, before any component and before a single row. Migration `0024`: D-147's `dossier` shape CHECK, D-148's `proposal` vocabularies and bounds, and D-153's four `indicator_daily` columns. `SCHEMA.md` in this same checkpoint, both parity directions green, `ExpectedMonetary` asserted unchanged at 19. The 6.1 scratch app is deleted here. Blocked on D-147, D-148 and D-153 |
| 6.3 | Config: the seven `researcher.*` keys, **`cost.annual_budget` seeded at last**, `cost.price_per_mtok_usd` gaining its two researcher models as entries rather than as new keys, and D-155's two `portfolio` rows through the seeder. `ConfigSeeder.Keys.Count` moves from 106 deliberately and the test moves with it. Every key resolved at 2021-01-04 and at the frontier. Blocked on D-155 |
| 6.4 | The upstream fills, and **they are phase 2 components changed inside phase 6, which is named rather than glossed**. `last_two_earnings_surprises` computed in C09 from `earnings_history`, a store its `ReadSet` already declares and which carries `surprise_fraction` since D-96. D-153's four columns filled by C08. The largest insider transaction in C34. **The frozen-window nulls are recorded here, not discovered later**: every one of these columns is null over the backfill and nothing in phase 6 reads them there. Blocked on D-153 |
| 6.5 | C15 DossierBuilder, the prefix half. `prompts/prefix-template.md`'s seven-part assembly order, D-149's empty rendering for sections 4 and 5, one invariant-culture number formatter, an explicit sort before every render, and the string built once per run and reused rather than rebuilt per call. `prefix_hash` on the row. **The prefix snapshot fixture, one hash across a full night of calls**, which is `FIXTURES.md`'s forward item and is INVARIANT 6 made mechanical [D-20]. Blocked on D-147 and D-149 |
| 6.6 | C15, the candidate block. **The prefix field key and the block keys generated from one ordered source**, with the test `prompts/candidate-block.md` asks for asserting the two lists identical and identically ordered, because a key present in one and absent from the other is a defect the model cannot report. The fixed core, the digest, the five screen blocks. **S5's block renders the gate's inputs and not its verdicts**, so the gate keeps one definition [INVARIANT 2]. **The one-liner is omitted visibly and `headline.source` renders absent** [D-154]. `FIXTURES.md` gains the two-screen candidate. §03's C15 Reads cell amended by a human in this checkpoint. Blocked on D-146 and D-154 |
| 6.7 | C26 CostLedger, the researcher half, **built before any researcher call is made**. One row per call with the four token counts, `was_batch`, `model_id` and `portfolio_id`, priced from the config table. The validator rejection count per model, which `SCHEMA.md` §cost_ledger specifies and nothing yet writes. **Both of §18's C26 alerts wired**: cache miss rate against `monitor.cache_hit_rate_min`, trailing cost run rate against `cost.annual_budget`. **Migration `0025` extends `0021`'s CHECK and `AlertType` gains the two values in this same checkpoint**, which is the rule `0021`'s own comment states; §03's C26 Writes cell gains `alert` by a human here. **A budget alert fires and no model switch happens** [D-156]. Blocked on D-156 |
| 6.8 | C16 ResearcherClient, the synchronous path. V4 Pro over the OpenAI-compatible shape C32 established, extended thinking off [D-21], a hard `max_tokens`, `model_id` on every row from what the provider said answered, insert only. **Proved against the real provider, and against it refusing.** The first paid researcher call in this system's history is recorded by 6.7 from row one. Blocked on D-148 and D-151 |
| 6.9 | C16, the batch path. Opus 5 through the Batch API with the one-hour cache [D-22], D-151's submit-and-exit, the morning collect job, the run log's waiting state, and D-152's deadline behaviour narrowed to what is observable without orders. **The evening half is a completed stage and not a partial run**, asserted. Blocked on D-151 and D-152 |
| 6.10 | C17 ProposalValidator, all three checks, updating `status` and `rejection_reason` and nothing else [INVARIANT 10 as amended]. D-150's grammar, tolerance and named exclusions. §18's retry-once-then-record-as-PASS on unparseable output. **Both `FIXTURES.md` forward items registered here**: a dossier with a deliberately corrupted citation, and a proposal whose stated probability contradicts its own stop and target. **§6's rejection-rate bands are read against the first night's number here**, per check rather than pooled. Blocked on D-148 and D-150 |
| 6.11 | **C36 gains a sixth panel, and it is this phase's openable checkpoint.** For one ticker on one date: the prefix hash and whether it matches the night's other calls, the block exactly as sent, both models' proposals, each verdict against the breakeven its own stop and target imply, the validator's status and reason, and which numerals check 2 matched against which block values. It declares `dossier` and `proposal`, computes nothing, and where a figure would have to be derived it shows the inputs and says so [D-109, 3.5's two rules]. §03's C36 Reads cell and §15's screen list both gain the two tables, by a human, in this same checkpoint |
| 6.12 | The evening order wired, C15 at 18:35 and C16 at 18:40, the morning job as its own command, and **one real night run end to end on both models**. `RUNBOOK.md`'s cycle table amended. **The observable is a chain of counts**: N candidates, one prefix row, N block rows, 2N proposal rows, 2N validator outcomes, and one cache write against N-1 cache reads per model. Recorded in `PROGRESS.md`: prefix tokens against 4,600, block tokens against 800, cache hit rate against 90 percent, the night's cost annualised against §17's $50, and **both of §6's pre-registered readings taken against their bands** |

**6.4 is the checkpoint most likely to be questioned and it should be questioned
deliberately rather than absorbed.** It changes three components belonging to phase 2 from
inside phase 6. The case for it is that all three changes are small, that two of the three
stores already declare the reads they need, and that the alternative is a dossier missing
six of the fields §07 says can flip a verdict. The case against it is that a phase changing
another phase's components is how scope creeps, and `CLAUDE.md` §3 is explicit that a build
session never widens its own. **It is in the plan because the operator was asked and
answered**, and it is flagged here so that the answer is visible at the checkpoint rather
than only in this document's history.

---

## 8. Definition of done

`BUILD_PLAN.md` gives five lines. Four are evaluable as written and one is half evaluable,
and that is said here rather than discovered at sign-off, because a done-when line the
phase cannot evaluate is not a definition of done [phase 4 §3, phase 5 §7].

1. **The prefix snapshot test passes across a full night of calls.** Evaluable. Measured as
   one distinct `prefix_hash` over every dossier row for the date, asserted in both
   directions: every block row's night resolves to that hash, and the count of distinct
   hashes for the date is one. Built as a fixture at 6.5 and observed live at 6.12.
2. **Cache hit rate exceeds 90 percent.** Evaluable, the operator having authorised the
   live night. Read from `cost_ledger` as cache-read tokens against cache-read plus
   cache-write plus fresh input, per model, and stated per model rather than pooled because
   the two providers cache differently and §5's measurement 5 may find one of them does not
   cache at all.
3. **A deliberately corrupted citation in a test dossier is rejected.** Evaluable at 6.10
   as a fixture, and it is one of the two `FIXTURES.md` forward items this phase owes.
4. **A proposal whose stated probability contradicts its own stop and target is rejected.**
   Evaluable at 6.10 as a fixture, the second forward item. `WORKED_EXAMPLE.md` §7's
   arithmetic is the worked instance and the fixture inverts it.
5. **The batch path completes and the 09:00 deadline behaviour is exercised.** **Half
   evaluable.** The batch completing is 6.9 and 6.12. The deadline behaviour as §18 states
   it is "skip the night entirely, no orders for any portfolio", and no order, portfolio
   runner or exposure matching exists until phase 7. **D-152 narrows it to what is
   observable now**, which is that the collect job records the miss, raises the alert,
   writes no proposal for the batched model and returns not completed, and **the line as
   written is re-asked at phase 7** rather than marked met here. This is D-139's treatment
   one phase on and it is the second time this corpus has needed it.

**Two lines are added, both because the phase cannot be read without them.**

**A re-run of C15 over one date is byte-identical**, asserted as a set comparison in both
directions over `dossier`, which is D-147's run-scope rule made observable and is the same
assertion 5.4 made of `headline`.

**And the prefix and block token counts are recorded against §07's estimates**, which is
what makes §17's cost table checkable for the first time. `PROGRESS.md`'s measured-figures
table gains the rows.

**Carried obligation discharged.** `6 → 10`, the cost ledger recording per model before the
first full night, is met at 6.7 and asserted at 6.12. It is met more strongly than it asks:
before the first call, not before the first night.

**Invariants at risk:** 6, 9, 10 and 16 as `BUILD_PLAN.md` states, and **13 is added**.
INVARIANT 13 because the prefix inserts the rubrics at the config version in force for the
run date and reads screen scores, lessons and a calibration table that are all versioned or
dated, and a prefix resolving any of them as-of-now would answer a different question than
the one the night asked. `prompts/prefix-template.md` §3 already says this in terms.

---

## 9. What blocks the phase, and what is only reported

### The blocker

**Twelve decisions, D-146 to D-157, none of which this document may author** [`CLAUDE.md`
§13]. Seven checkpoints are blocked on one or more. 6.1 is not, so the sweep can run while
they are being read, and the measurements it takes are inputs to none of them.

### Reports, each naming where it lands

Three phases running, findings have been written well in `PROGRESS.md` and not into the
carried-obligations table that planning actually reads, and the six obligations phase 5's
review filed were the latest instance. **So each item below names its destination table and
row, and the plan's own definition of done includes the row existing on disk and being read
back off it** [`CLAUDE.md` §7: a code comment is not a record].

| # | Item | Lands in |
|---|---|---|
| 1 | **C15's Reads cell names five stores and the block needs fourteen** [D-146]. Fourth instance of the §03 Reads drift after 4.4, 4.10 and D-125 | Amended in §03 by a human at 6.6. The count is recorded in `PROGRESS.md` against D-125's rate rather than opened as a new row |
| 2 | **The company one-liner has no table, no writer and no phase** [D-154] | `BUILD_PLAN.md` carried obligations, a new row, From 6, Owed to whichever phase reopens the local model's product surface |
| 3 | **`headline.source` is written null and deriving a host from `link` is a choice rather than a read** [D-154] | The existing `5 → 6` row in that table, **closed as reported** with the disposition named. Not a new row |
| 4 | **D-153's four `indicator_daily` columns are null over the frozen window**, and a C08 range re-run is priced at 9 to 21 minutes at zero provider units | `BUILD_PLAN.md` carried obligations, a new row, From 6, Owed to 8, since C22 and C24 are what would read them over history. Bundled under `SCREEN_LIFECYCLE.md` §6.7 with the `TradingCalendar.SessionsAsync` obligation rather than taken alone |
| 5 | **`digest.chain` is versioned and `local_model_config` is not**, and 6.3 touches that namespace without resolving it | The existing `5 → 6` row, updated with what 6.3 found. Not a new row, and not closed: the decision is still authored |
| 6 | **Two Worker sites independently name `NewsDigester.ComponentName`** and can diverge with nothing catching it [phase 5 sign-off review] | `BUILD_PLAN.md` carried obligations, a new row, From 5, Owed to 9, which owns the Worker's command surface. 6.12 adds a third caller and makes the row worse rather than better, which is why it is filed here |
| 7 | **§18's cost-run-rate row says "drop to the cheaper model tier" and this phase builds no tier ladder** [D-156] | `BUILD_PLAN.md` carried obligations, a new row, From 6, Owed to 10, the soak being where a trailing cost run rate first has a trail |
| 8 | **Both pre-registered readings, per night, with their bands** [§6] | `PROGRESS.md` phase 6 block, and the measured-figures table for any figure that moves from estimated to measured |
| 9 | **S3's composition and S5's fill** | Already filed to phase 8 at Q.10 and Q.11. **Unchanged and not restated here**, because a second copy of an obligation is one that can disagree with itself |

---

## 10. What phase 6 does not do

**No backfill of dossiers or proposals**, on the three grounds in §3: a dossier over a past
date resolves nine stores as-of or it is wrong, a proposal produced today is not
point-in-time, and `VALIDITY.md` §6 names backfill-based evaluation of the researcher as a
threat whose mitigation is that it never happens.

**No orders, no risk gate, no sizing, no arbitration, no portfolio runner.** All phase 7.
The two portfolio rows D-155 seeds carry no behaviour.

**No forward returns, no tuner, no lesson writer, no calibration report.** All phase 8. The
prefix reads `researcher_memory` and `calibration` empty and D-149 says what that renders
as.

**No judgment of proposal quality.** The rejection surface is structure, citation and
internal consistency, and §09 says in terms what the validator cannot do: the thesis prose,
the counter-argument and the digest are unverifiable by machine and pass through untouched,
and it confirms the probability is a well-formed number rather than a well-calibrated one.

**No answer to D-54**, whether both research portfolios are kept. §6 records the first
night's divergence against `VALIDITY.md` §4's pre-registered 20 percent; §4's minimum
evaluation period is 12 months and one night of 28 candidates does not shorten it.

**No twenty-night soak and no cost forecast.** Phase 10. One night is instrumented
completely and that is the whole of what is run.

**No model switch on a budget alert**, per D-156. The alert fires and the tier question is
filed.

---

## 11. Authored items owed, and files

**Owed from a human before the checkpoints they block:** D-146 to D-157 in `DECISIONS.md`;
§7's checkpoint table into `BUILD_PLAN.md`'s phase 6 section; and four amendments to
`ARCHITECTURE.html` §03, each in the checkpoint that needs it, being C15's Reads cell at
6.6, C26's Writes cell at 6.7, C36's Reads cell at 6.11, and §15's screen list at 6.11.

**Files this phase creates:**

```
docs/evidence/phase-6/                      6.1's transcript
src/StockResearcherLab.Data/Migrations/
  0024_researcher_shape.sql                 6.2
  0025_cost_alert_vocabulary.sql            6.7
src/StockResearcherLab.Pipeline/Decide/     the folder is empty today
  DossierBuilder.cs                         6.5, 6.6
  PrefixAssembly.cs                         6.5
  FieldKey.cs                               6.6, the one ordered source
  CandidateBlock.cs                         6.6
  ResearcherClient.cs                       6.8, 6.9
  ProposalValidator.cs                      6.10
  CitationCheck.cs                          6.10
```

**Files this phase amends:** `ConfigStore.cs`'s seeder at 6.3; `ValuationEngine`,
`IndicatorEngine` and `FlowEngine` at 6.4; `CostLedger.cs` and `AlertType.cs` at 6.7;
`NightlyRun.cs` and the Worker's commands at 6.12; `SCHEMA.md` at 6.2 and 6.7;
`CONFIG_REFERENCE.md`'s Consumer column at 6.12, filled from the composition code rather
than inferred from the key name; `FIXTURES.md` at 6.5, 6.6 and 6.10; `RUNBOOK.md` at 6.12;
`PROGRESS.md` throughout.

**Files this phase must not amend:** `prompts/rubrics.md`, `prompts/prefix-template.md` and
`prompts/candidate-block.md`, which are authored product; `ARCHITECTURE.html` and
`CLAUDE.md`, which are human-edited only; and `attribution`, which is not a file but is
frozen and is named here beside them.

---

## 12. Verification

Per checkpoint, `ci.ps1` green, which since 1.12 is the only step that cannot reach the
test run off a stale binary.

**At 6.5**, the snapshot fixture, plus a deliberate break of each of the five determinism
rules asserted to fail it: a clock read, an unsorted collection, a locale-formatted number,
a trailing space, and a rebuild per call.

**At 6.6**, the two key lists asserted identical and identically ordered, and one candidate
surfaced by two screens asserted to receive both screen blocks.

**At 6.7**, a priced call against an injected response reproducing a figure computed by
hand, and an unpriced model asserted to throw. `AlertTypes.Names` asserted equal to the
CHECK read out of the catalogue, which is the assertion `0021` already carries for its own
two values.

**At 6.10**, both forward fixtures, plus the rejection rate read per check.

**At 6.11**, the panel opened by a person on a real ticker and a real date, which is the
checkpoint's whole point and is not replaced by a test.

**At 6.12**, the chain of counts, both pre-registered readings, and the four measured
figures into `PROGRESS.md`.

**Throughout**, `guards.ps1`, whose `ExpectedMonetary` is asserted unchanged at 19 at 6.2
and re-asserted at 6.7 when `cost_ledger` gains its second caller.
