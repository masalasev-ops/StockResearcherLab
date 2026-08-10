# Phase 1 — Ingest and universe

**Archived before code, per `CLAUDE.md` §3.**

This phase was not issued as a single block. It was issued as an opening request, then a
readiness report was returned, then two authored decisions were supplied verbatim, then nine
amendments arrived in four batches as the plan was reviewed. What follows is every instruction
as issued, in the order issued, with nothing added and nothing reworded.

The assembly is stated here rather than hidden, because a prompt that looks like one block
when it was nine would misrepresent why the plan has the shape it has. Sections marked
`[issued]` are the user's words. Nothing in this file is a summary.

---

## 1. Opening request `[issued]`

> Make a comprehensive step by step plan for phase 1 for this project. Check if we are ready
> for it though first

Mid-turn, while the readiness read was in progress:

> the current branch has been merged. So get latest and delete all these pr branches

---

## 2. N.1 to N.3, the authored decisions `[issued]`

Issued after the readiness report identified D-64 and D-65 as blocking 1.2 and 1.3.

### N.1 APPEND TWO AUTHORED DECISIONS

Verbatim into docs/DECISIONS.md before ## Open. Do not reword or renumber.

---
**D-64 The row count alert threshold is not revised, and the question is
deferred until settled counts exist.** `ACTIVE`
D-59 alerts below 45,000 rows. The probe's 2026-08-04 finished at 44,708,
which would alert, and that looks like a bound set too high.

It is not evidence of that. The day was read three times in one evening at
44,665, 44,686 and 44,708, rising each time, and the reads stopped rather
than converged. Its final count is unknown. A day still accreting says
nothing about where a threshold for settled days belongs, and revising the
bound on it would be loosening a bound because a measurement missed it,
which CLAUDE.md section 11 prohibits.

D-65's settledness check is what produces the evidence, since a count is
only meaningful once the day is known to be final. The question is asked
again after phase 1 has accumulated settled counts, read at sign-off. It is
a query against price_daily, not a measurement task.

**D-65 The freshness guard checks recency, completeness and settledness,
and these are three different things.** `ACTIVE`
ARCHITECTURE.html section 4 asserts the latest price date equals today.
Checkpoint 1.2 rejects the most recent available day as still accreting.
Both cannot hold, and the architecture is the half that is wrong: it was
written before the probe found a session accreting for hours and into the
following evening.

Recency. The newest date in price_daily is not older than the most recent
completed trading session. This catches a provider that has not updated and
a run that was missed. It does not assert today, which the settled-day rule
makes false.

Completeness. D-59's thresholds are unchanged, abort below 40,000 and alert
below 45,000, subject to D-64.

Settledness. A date is unsettled if re-fetching it returns more rows than
are stored for it. Both figures already exist, so this is a comparison
rather than a threshold and no new bound is introduced. The probe's
2026-08-04 fails it three times within one evening.

Ingest loads the newest date passing all three. A date failing settledness
is re-read on a later run rather than discarded, because it is incomplete
rather than wrong.
---

DoD: both present verbatim; nothing renumbered.

### N.2 CONSEQUENT EDITS

ARCHITECTURE.html section 4: strike the assertion that the latest price
date equals today, pointing at D-65, and state the three checks in its
place. Also update the freshness guard's catalogue row and its node in the
nightly flow figure, both of which currently describe the single check.
RUNBOOK.md failure table: the stale-or-short row becomes three rows, one
per check, each with what it means and what to do. A settledness failure is
not an abort; the run waits and re-reads.
CONFIG_REFERENCE.md: no new key. Settledness is a comparison and recency
reads the exchange calendar. Note that explicitly under the freshness keys
so a later reader does not add one.
DoD: no document asserts the latest price date equals today; the three
checks named consistently everywhere the guard appears.

### N.3 CHECKPOINT 1.2 IS RIGHT AND STAYS

It already rejects the most recent available day. Leave its scope alone.
Add one line to its definition of done: a date that fails settledness is
re-read on a later run rather than skipped.
DoD: 1.2 unchanged except that line.

---

## 3. Instruction to unblock and produce the plan `[issued]`

> ok do whatever it is to unblock and then generate the full plan for review for phase 1

---

## 4. Amendments A1 to A6 `[issued]`

# Amendments to the phase 1 plan, A1 to A6

Paste into the plan session before code. Each is scoped to a checkpoint and
names what proves it. Nothing here changes ARCHITECTURE.html.

## A1 Carry pass Q's two phase-1 items

Pass Q was issued and never run. Main goes `d9cb5df` then `10c77cd`, so clauses
Q.1 to Q.7 are all still open. Two of them were owed to this phase.

**A1.a, before 1.8 and cheapest at 1.4's migration.** `ARCHITECTURE.html`
section 3, section 5 and D-61 name the column `insider_net_90d_usd`.
`SCHEMA.md` line 129 and `0001_snapshot.sql` line 145 name it
`insider_net_usd_90d`. The architecture constrains the code [CLAUDE.md section
13] and names match the architecture [CLAUDE.md section 6], so the schema is
wrong. `flow_daily` has never held a row. Correct `SCHEMA.md` and rename the
column in `0002_*.sql` alongside the statement fields, or drop and re-migrate
and correct the snapshot, whichever the session judges cleaner. Record which,
and why, in the commit body. Do not touch `ARCHITECTURE.html`.

**A1.b, before 1.2 writes anything.** The rails define no unit of work.
`StageData` opens a connection per call and `StageRunner` wraps nothing in a
transaction, so a stage that throws leaves its writes committed while `run_log`
records `rows_written` as unknown. `ARCHITECTURE.html` section 4 states that a
stage can be re-run against an earlier night without side effects, and
`CLAUDE.md` section 6 states that a stage completes or it fails the run.
Neither is currently enforceable, and this phase builds seven writing stages.

Record it as **D-68**, `ACTIVE`, in the form this phase actually builds: every
stage write is idempotent on the table's own grain, so a re-run of any stage
over any date replaces rather than duplicates. That covers every table phase 1
writes, all of which have a natural key. A transaction per stage is the
alternative and is rejected here because phase 3 runs the same stages over five
years and one transaction across twelve million rows is its own failure mode.
State in the decision that a future multi-table stage with no natural key
reopens it.

**Done when:** a whitespace-tolerant grep for `insider_net_usd_90d` over the
repository returns nothing; D-68 is in `DECISIONS.md` as ACTIVE; every writing
checkpoint below states its conflict target; `migrate.ps1` runs clean from an
empty database and a second run applies nothing.

## A2 1.12 and 1.2, the COPY path is staged, and partial rows are dated

`COPY` takes no `ON CONFLICT`, so "COPY-loaded, idempotent on (ticker, date)"
as drafted throws a duplicate key error on the second load and 1.2's own test
fails. Build 1.12's route as COPY into an unlogged staging table, then a single
`INSERT ... SELECT ... ON CONFLICT (key) DO UPDATE` into the target, with the
declared-access check on the target table and operation before either statement
runs. Row order into the stream stays explicitly sorted.

1.2 loads candidate dates including partial ones, which is correct because the
stored count is what settledness compares against. Nothing marks those rows as
partial, so add the rule rather than a column: **every date filter in every
stage comes from `StageContext.Date`, never from `current_date` or any database
clock.** A stage that reaches for the database's own now reads incomplete bars
and produces a plausible night [CLAUDE.md section 1].

**Done when:** a stage COPYing into a table it does not declare throws
`UndeclaredTableAccessException` before a connection opens; loading the same
date twice leaves the row count unchanged and every value equal; a grep for
`current_date` and `now()` over `src/StockResearcherLab.Pipeline` returns
nothing.

## A3 1.3 and 1.4, both alerts emit through the run log

This is not open and does not need authoring. `SCHEMA.md` gives `alert` a
single writer, ConcentrationMonitor. `ARCHITECTURE.html` section 3 gives C07's
writes as `run_log` via C27 and C03's as `fundamental_snapshot` alone. Neither
may write `alert`, so D-59's count band and D-62's substitution-rate threshold
both emit through the run log, exactly as C07 already does for its abort.

Remove the item from "still authored, still yours" and note in the commit body
that the architecture answered it.

**Done when:** neither FreshnessGuard nor FundamentalsIngestor declares a write
on `alert` in `PipelineComposition`; the write-ownership conformance test still
shows `alert` with one writing component; the alert band and the substitution
rate each produce a `run_log` row with a status distinguishable from ok and
failed.

## A4 1.3, name the source of the most recent completed session

"Read from the exchange calendar" has no store and no declared read.
FreshnessGuard's reads in `ARCHITECTURE.html` section 3 are `price_daily` and
`run_log`. Pick one and state it in the checkpoint:

- derived from `price_daily` itself, which needs no new read and cannot detect a
  whole missing session, or
- an exchange-calendar call on the typed client from 1.1, which makes C07 the
  one non-ingest stage that talks to the provider and sits outside
  `IStageData` and `DeclaredAccess` entirely, or
- a calendar table written by C06 EventsIngestor, which moves 1.8 ahead of 1.3
  in the build order.

Whichever, say so in the checkpoint and in the commit body, because the guard is
the component whose false pass is invisible in the output.

**Done when:** 1.3's scope names the source; if it is the second option, the
commit body records that C07 makes a provider call and that no declared-access
check covers it; the three guard tests still fail in isolation.

## A5 1.13 seeds eleven keys, not nine

The enumeration gives ten: six `universe.*`, both `fundamentals.*` keys
(`min_clean_gaps_for_substitution` and `substitution_rate_alert`), and both
`freshness.row_count_*`. The plan says nine in the checkpoint, in the critical
files table and in the verification block.

Add an eleventh while `seed.ps1` and `CONFIG_REFERENCE.md` are open:
`fundamentals.widest_gap_alert_days`, default 180. `RUNBOOK.md` line 56 states
that threshold as a literal with no key, and 1.4 is where it first gets read
[CLAUDE.md section 8, no magic numbers at call sites]. Fill the Consumer column
for all eleven from the composition code rather than from the key name.

**Done when:** `seed.ps1` reports eleven keys; `CONFIG_REFERENCE.md` carries a
row for `fundamentals.widest_gap_alert_days` citing D-62; `RUNBOOK.md` line 56
names the key rather than 180; every one of the eleven has a Consumer entry read
off the composition code.

## A6 Settle CI without blocking the phase

The sign-off record already establishes that hosted runners are not being
allocated, and that it is not the YAML, the triggers, the registration or the
repository permissions. The repository is public, so hosted minutes are free
and unlimited and this is not exhaustion.

A self-hosted runner does not close it as drafted. `ci.yml` uses a
`services: postgres` container, which requires a Linux runner with Docker. A
Windows self-hosted runner needs the workflow rewritten against a local
database, which loses the empty-server property the two migrate steps prove.

Do this instead, as an unnumbered chore before 1.9:

Add `ci.ps1` at the root running the steps of `.github/workflows/ci.yml` in
their own order against a database dropped first, exiting non-zero on the first
failure. Sign-off step 1 asks that nothing be recorded by hand that a run can
record, and this makes the local path a run. Leave `ci.yml` untouched so it
works the moment runners are allocated.

Record the runner question as a phase 1 sign-off gap in `PROGRESS.md` at the
point it is taken rather than at sign-off, so the decision is made now and not
under pressure. Opening a support ticket runs in parallel and blocks nothing.

**Done when:** `ci.ps1` exists and its step list matches `ci.yml`'s in order and
in content; a run of it against a dropped database exits 0 and prints the same
seven results the sign-off block recorded by hand; `ci.yml` is unchanged.

---

## 5. Amendments A7 and A8, and two corrections `[issued]`

# Two further amendments, A7 and A8

## A7 D-68 names the two tables with no natural key, and 0002 gives them one

D-68's draft states that every table phase 1 writes has a natural key. Two do
not. `events` and `insider_transaction` are both `bigint GENERATED ALWAYS AS
IDENTITY PRIMARY KEY` with no unique constraint on any column tuple, so
`ON CONFLICT (...)` against either raises "there is no unique or exclusion
constraint matching the ON CONFLICT specification" before a row is written.
Naming a conflict target is not enough; the index has to exist.

**D-68's wording.** Replace the claim that every table has a natural key with:
every stage write is idempotent on the table's own grain, and where a table
carries a surrogate identity key the grain is declared as a unique index in the
migration that first writes it. `events` and `insider_transaction` are the two
in this phase.

**0002_*.sql gains two indexes**, alongside the statement fields and the
`flow_daily` rename:

- `insider_transaction`, unique on the tuple 1.7 already names. If a provider
  can file two transactions for one owner on one date under one code, that
  tuple is not unique and D-68's reopening clause fires; the sweep at 1.7
  answers it against real rows rather than by assumption.
- `events`, unique on the grain 1.8 declares. `SCHEMA.md` gives the grain as
  ticker by event and the columns as `ticker`, `event_type`, `event_date`,
  `announced_date`. Pick the tuple, state it in the checkpoint, and say in the
  commit body what a collision would mean.

**1.8 gains an events conflict target.** It currently names one for `flow_daily`
only, which is how the gap survived.

**Done when:** every writing checkpoint in this phase names a conflict target
that a `PRIMARY KEY` or `UNIQUE` index in `0001` or `0002` matches exactly;
re-running the events ingest and the flow ingest over the same date each leaves
the row count unchanged and every value equal; `dotnet test` green;
`migrate.ps1` runs clean from empty and a second run applies nothing.

## A8 Report the FreshnessGuard read set rather than absorbing it

`ARCHITECTURE.html` section 3 gives C07's Reads as `price_daily, run_log`. Every
other ingest component's Reads column lists its endpoint, so the omission states
that this component does not call the provider. 1.3 as planned gives it two
calls, the exchange calendar and the settledness re-fetch.

First check whether D-65 already amended that cell on `decisions-d64-d65`. If it
did, nothing to do beyond citing D-65 in the commit body.

If it did not, this is a contradiction between an authored document and the work
[CLAUDE.md §3]. Report it, do not close it, and do not edit
`ARCHITECTURE.html`. Record it in `PROGRESS.md` as a phase 1 finding naming the
cell, the two calls, and D-65 as the decision that made them necessary, so the
human can make the one-cell correction. Build 1.3 as planned meanwhile; the
invariant is not in question, only the read set the catalogue records.

**Done when:** either the commit body cites a D-65 clause that amends C07's
Reads, or `PROGRESS.md` carries the finding with the cell named; either way 1.3
builds and its three tests fail in isolation.

## Two corrections to carry

`RUNBOOK.md`'s 180-day literal is line 56 on `main` at `10c77cd`. If
`decisions-d64-d65` moved it to 59, that is the branch and not an error, but
1.13's done condition should read "RUNBOOK.md names
`fundamentals.widest_gap_alert_days` rather than the literal 180" so it does not
go stale on the next edit above it.

Pass Q's Q.4 is owed to this phase after all, so fold it into the pre-flight
chore beside `ci.ps1`: `guards.ps1`'s `Remove-Comments` strips `--` to end of
line for every extension, which in C# is the decrement operator. Scope that
strip to `.sql` and leave the `//` strip applying to all. It is inert today only
because no C# in the tree decrements anything, and this is the phase that writes
the first paging loops. **Done when:** the strip is extension-scoped, the header
comment states the scope, and a run prints 4 checks each expecting 0 and finding
0 over the same tracked file count.

---

## 6. Amendment A9 and one carried obligation `[issued]`

## A9 seed.ps1's set_at is a convention, and 1.13 tests the date before it

`config_rows.set_at` is what 1.13 resolves against, and `seed.ps1` is what writes
it. Nothing in the plan says what value it carries or what happens on a second
run.

**The convention.** Seeded rows carry a `set_at` at or before the earliest date
the system will ever resolve config for, which is the start of the five-year
backfill window, not the moment the script ran. A wall-clock stamp puts every
backfill date before every row. State the value and its reason in `seed.ps1`'s
header and in `CONFIG_REFERENCE.md`.

**Re-runnability.** `config_rows` is `PRIMARY KEY (key, version)`, so a second
run either collides or churns a version per key for no change. Seed version 1
with `ON CONFLICT (key, version) DO NOTHING`, so re-running is a no-op and a real
change still arrives as version + 1 through the ordinary path. This is the same
property `migrate.ps1` is already tested for, and the same reason.

**The missing test case at 1.13.** Add a third: a date earlier than every row's
`set_at`. State which behaviour is correct and assert it. A resolver that falls
back to `MAX(version)` when the date filter matches nothing returns today's
config for a historical date, which is exactly what INVARIANT 13 exists to stop,
and it passes the two cases already written.

**Done when:** `seed.ps1` is a no-op on a second run and says so; the seeded
`set_at` is stated in the script header and in `CONFIG_REFERENCE.md`; 1.13 has
three test cases, the third being a date before every `set_at`, and its expected
behaviour is named in the checkpoint rather than left to the implementation.

## One carried obligation, not a phase 1 change

Add to the carried obligations table: `| 1 | 3 | \`security\` is one row per
ticker carrying a single \`size_bucket\` and \`market_cap\`. C11
PercentileEngine ranks within size bucket, so a backfill of a 2021 date reads
2026's bucket for every name. \`first_seen\`, \`last_seen\` and
\`delisted_date\` make membership reconstructable per date [D-48]; bucket and
market cap are not |`

Phase 1 builds 1.5 as planned. This is a question about how phase 3 reads what
1.5 writes, and it belongs where phase 3 will look for it.

---

## What this prompt does not contain

**Pass Q's own prompt is not archived anywhere.** A1 states it was issued and
never run, and `prompts/spent/` holds only the three design prompts,
`phase-0-rails.md` and `phase-P-data-probe.md`. Q.1's `flow_daily` rename and
Q.4's comment-stripper fix are carried into this phase through A1.a and the A7
corrections, so their content survives, but the prompt that asked for them does
not. That is a record gap independent of the pass never running, and it is
reported here rather than closed, because only the person who issued it has the
text.
