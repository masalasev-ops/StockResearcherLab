# RUNBOOK.md

How to run this and what to do when it stops.

Mostly forward-looking until phase 0 exists. The failure table is derived from
`ARCHITECTURE.html` §18 and is the part worth reading before the first unattended
night.

---

## The nightly cycle

| Time (ET) | What runs |
|---|---|
| 17:30 | Price ingest, whole US market, one bulk call |
| 17:40 | Freshness guard. Recency, completeness and settledness. Aborts everything on the first two; the third re-reads on a later run [D-65] |
| 17:45 | Fundamentals, flow, events |
| 18:00 | Sentiment, whole universe |
| 18:05 | Indicators, valuation, market context |
| 18:15 | Percentiles |
| 18:20 | Gates |
| 18:25 | Screens |
| 18:30 | Candidate allocation, attribution write |
| 18:32 | Headlines for candidates only |
| 18:33 | News digests via the provider chain |
| 18:35 | Dossier assembly |
| 18:40 | Researcher calls. Opus 5 submits a batch; V4 Pro runs inline |
| by 09:00 | Batch returns, validator runs, risk gate, orders queued |
| 09:35 | Fills at the open |
| 18:10 daily | Position marking, exits, forward return filling |

**The decide stage is asynchronous.** The evening run submits and exits. A separate
job completes the pipeline when the batch returns. This is normal and the run health
timeline shows a waiting state rather than a stall.

---

## Prerequisites for an unattended night

- The machine is awake and connected between 17:30 and 19:00 ET
- Postgres service is running
- At least one digest provider is healthy
- `appsettings.Secrets.json` present beside the running project and populated

**The machine being asleep or rebooted for updates is the same failure as the local
model being down, and is more likely.** Decide deliberately whether this box stays
on, because missed nights are silent and the run log is the only place they appear.

---

## Failure table

| Condition | Detected by | Behaviour | What to do |
|---|---|---|---|
| Recency: the newest date in `price_daily` is older than the most recent completed trading session | FreshnessGuard | Abort. No orders | The provider has not updated, or a run was missed. Check the provider, then check `run_log` for a gap. Rerun when fresh. A skipped night costs nothing |
| Completeness: the row count is below `freshness.row_count_abort_below` | FreshnessGuard | Abort. No orders. Between the abort floor and `freshness.row_count_alert_below`, alert without aborting | A truncated file. Check the provider and rerun. Do not revise the threshold from a single night: the bound is asked again against accumulated settled counts at sign-off, and moving it because a measurement missed it is what `CLAUDE.md` §11 prohibits [D-64] |
| Settledness: a date's row count is below a configured fraction of the median count of the dates before it | FreshnessGuard | **Not an abort.** The ingest walks back to the newest date that does pass, and C02's trailing re-load window tops the short date up on a later run | Nothing. The session is still accreting, which the probe saw run for hours and into the following evening. If every candidate date fails, the run has nothing settled to work on and stops on recency instead |
| Filing date substitution rate above `fundamentals.substitution_rate_alert`, or any ticker whose widest clean gap exceeds 180 days | FundamentalsIngestor | Alert | The provider's date handling has changed. Investigate before the next backfill, since every substituted row reads late by that ticker's own widest gap and the rate going up widens that silently. A widest gap above 180 days is a filer whose fundamentals reach a screen too late to be worth much, and the universe should be told rather than left to carry it [D-62] |
| A screen returns zero names | ScreenEngine | Normal. Smaller candidate set | Nothing |
| All five screens return zero | ScreenEngine | Halt before the researcher | Data fault, not a quiet market. Investigate |
| A screen cannot fill a size slot | CandidateAllocator | Slot stays empty | Nothing. This is the guarantee working |
| No digest provider healthy | NewsDigester | Halt before the researcher. No orders | Start the local server, or check the secondary. Rerun |
| Primary digest provider down | NewsDigester | Falls through, recorded | Nothing urgent. The indicator shows secondary |
| Batch not returned by 09:00 | ResearcherClient | Skip the night entirely | Check batch status. Do not force partial orders |
| Model returns invalid JSON | ProposalValidator | Retry once, then PASS | Nothing unless the rate rises |
| Cited figure absent from dossier | ProposalValidator | Reject that proposal | Nothing unless the rate rises |
| Rejection rate above 5% in a night | ProposalValidator | Alert | Something changed in the prompt, schema or model. Investigate before the next run |
| Cache hit rate below 80% | CostLedger | Alert | The prefix is not byte-identical, or calls are too spread out |
| Megacap share above a third over 20 days | ConcentrationMonitor | Alert | Treat as a bug in a screen definition, not a market condition |
| Distinct tickers over 60 days below 250 | ConcentrationMonitor | Alert | The system is converging. The quotas are not holding |
| Cost run rate above target | CostLedger | Alert | Check output token drift first. That is the line that moves |
| Order above 1% of median dollar volume | PaperBroker | Reject the order | Nothing. An unrealistic fill is worse |
| Any stage produces zero rows | RunLog | Halt remaining stages | A partial run is worse than none |

---

## Recovery

**Any stage can be re-run against a past date.** Stages are pure functions of date
and config version and write to declared tables only, so re-running is safe and
produces identical output.

**The exception is anything that spends money.** Re-running the researcher for a
past date costs a fresh set of calls and produces a second set of proposals for that
date. Decide whether you want that before doing it.

**Never re-run the attribution write.** Those rows carry scores frozen at the time
they were first written, and a re-run would apply today's screen definitions to a
past date, which is lookahead bias arriving through the back door.

---

## Backfill

### Running an ingest sweep

`Worker backfill <stage> [from] [to]`, one stage at a time.

**Pass `from`, and pass `to` as well if the range end matters.** A sweep's attempt rows
are stamped with `from`, so that is the argument one sweep has to keep constant across
the days it spans. `from` defaults to `backfill.window_start`, which is a stored date
and does not move, so the defaults are safe for the standard window; a sweep over
anything else states its start every time. `to` defaults to today and moves at midnight,
and nothing resumes on it.

**A halt is the mechanism working.** The allowance gate stops the sweep when the next
unit will not fit above `backfill.unit_reserve`, keeps everything written, and exits 2.
Run the same command again after the provider's day rolls over. Exit 0 is a completed
range and exit 1 is a failure.

**Every exit resumes the same way, including a killed process** [D-99]. What a sweep has
done is in its attempt record, written as it goes, so a clean halt, a command timeout and
a `kill -9` are the same thing to the next run: it dispatches the pool members carrying
no attempt row for this range start. A re-invocation of a finished sweep fetches nothing.
A failure costs at most the chunk in flight, whose rows were never recorded.

**The provider's day rolls lazily, on the first billable call.** After the UTC boundary
the counter still reads the previous date until something spends a unit, and the gate
refuses to spend against a stale reading, so a backfill run alone can sit in front of a
day it is entitled to. A nightly run clears it. `/api/user` is free and never will.

**The run log reports and decides nothing.** The pre-run line names the last range run's
status, date and row id so an operator can see what happened before spending another
day. Deleting those rows loses the account and changes no behaviour.

**Establishing a connection retries twice; nothing else retries.** The count is in every
range run's line, including its zero. A count that is regularly non-zero is not the
retry working, it is the database or the pool needing attention.

**Vacuum after a bulk load, deliberately.** `VACUUM price_daily`, never `FULL`. A sweep
re-fetching ground it has already covered turns every write into an `ON CONFLICT DO
UPDATE`, and at this size autovacuum does not trigger until roughly 15.6 million dead
tuples and then scans the whole table while the sweep is still writing. Left alone it
falls behind: 2026-08-13 measured 27 million dead tuples against 77 million live, the
upsert crossing its 300 second command timeout on the third pass over the same rows
after clearing the first two. Plain vacuum makes the dead space reusable and the
remaining inserts consume it. `FULL` rewrites 16 GB under an exclusive lock to shrink a
file the sweep then refills.

### Why a per-ticker write failure is not tolerated

Two per-ticker failures are tolerated and they are enumerated above in the failure
table: a provider 404, and a D-71 short page. **There is deliberately no third, and a
write failure is the one that keeps being proposed.**

A 404 is a fact about the world. The provider does not carry that ticker, and recording
zero rows is the true answer, so the sweep continues having lost nothing.

A write failure is not a fact about the world. The data was fetched and the units were
paid, and the write is what was lost, so continuing past it reports `Completed` over a
partial load. That is the same shape as the swallowed 402 this phase already fixed
once: a stage that returns success while the store is short is the failure mode nothing
downstream can detect.

It also fails worst exactly when it matters most. A database unavailable for ten minutes
would burn hundreds of tickers as tolerated failures, spend their units, write nothing,
and exit zero. The attempt record above is the answer instead: the run fails, loudly,
and the chunk in flight recorded no attempts, so it costs one chunk to re-do.

### The two-pass screen build

Run in two passes. Pass one computes raw screen scores for every ticker on every
date, fully date-parallel with no cross-date dependency. Pass two computes floors and
applies them in a single window function over the completed table.

Expect to rerun this repeatedly during development, because every screen definition
change invalidates it. That is why it was built to finish in minutes.

Postgres settings worth raising before the first run: `maintenance_work_mem` and
`max_wal_size`. The shipped defaults are tuned for a small shared server and govern
how fast a twelve million row bulk load completes.

---

## Changing things once live

A change to a screen definition, a rubric, the dossier format or a model **splits
history into halves that cannot be pooled.** It stays possible forever, but the
analysis must be segmented on config version.

Cheap: reporting, the learning loops, the UI.
Expensive: the candidate generator, the dossier, the rubrics, the risk layer.

Bundle expensive changes rather than making them one at a time.
