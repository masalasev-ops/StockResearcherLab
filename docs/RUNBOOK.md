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
| 17:40 | Freshness guard. Aborts everything if the data is stale |
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
| End-of-day file stale or short | FreshnessGuard | Abort. No orders | Check the provider. Rerun when fresh. A skipped night costs nothing |
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
