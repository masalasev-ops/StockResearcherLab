# PROGRESS.md

What has actually been built, as opposed to what is designed.

**This file is written by the build, not authored.** Test counts, HEAD shas,
measured timings and row counts are observations and belong to whoever ran them.
Correct them directly. Do not record intentions here.

---

## Phase status

| Phase | Status | HEAD | Notes |
|---|---|---|---|
| P Data probe | DONE | 3099e66 | All five checkpoints landed and all six findings recorded. D-57 to D-63 produced. Closed under the five-step procedure retired at D-67, after two conformance passes and the C, E, G, H, J and K corrections. That record is in the archive |
| 0 Rails | DONE | d9cb5df | Signed off 2026-08-06. Checkpoints 0.1 to 0.8, plus CI, the sign-off review, and pass O's corrections. 25 tests green. Step 1 was met by running every CI step locally, because no hosted runner has ever picked up a job on this account. Step 2 ran at `d4baeaf` and does not cover pass O's four corrected files. Both gaps are in the phase 0 block below |
| 1 Ingest and universe | NOT STARTED | | |
| 2 Compute | NOT STARTED | | |
| 3 Backfill | NOT STARTED | | |
| 4 Screens and selection | NOT STARTED | | |
| 5 Digest chain | NOT STARTED | | |
| 6 Researcher | NOT STARTED | | |
| 7 Risk and execution | NOT STARTED | | |
| 8 Learning loops | NOT STARTED | | |
| 9 API and UI | NOT STARTED | | |
| 10 Soak | NOT STARTED | | |

---

## Probe findings

Filled by phase P. D-58, D-59, D-60 and D-62 each rest on a figure here, which is
why this table stays while the rest of the phase P record is in the archive.

| Question | Answer | Measured |
|---|---|---|
| News archive depth for small caps | Five years is reachable. 5 of 6 return articles from the window start; ~~the sixth listed in 2024~~ [struck, H.2] the sixth returns nothing before 2025-06-13. Depth is not the constraint, volume is | Earliest article 2021-08-05 to 2021-08-23 for 5 of 6, window opened 2021-08-05. KBDC 2025-06-13, ~~IPO 2024-05-22~~ [struck, H.2] no listing date was measured, and one would not account for the thirteen months between any 2024 listing and the first article anyway. Five-year totals 177, 199, 255, 924, 3,474, and KBDC 19. Last 90 days 2, 3, 4, 12, 44, 144. Control NVDA 62,503 over five years, a floor since the 20-page cap bound in 2025 and 2026, and ~~1,000 in 90 days~~ [corrected, H.2] at least 1,000 in 90 days, which is the `limit=1000` request cap on a call that does not page rather than a count. 6 small caps plus control, 2026-08-05 |
| Sentiment series coverage, $300M-$2B | Exists and is never empty where present, so the "series exists but is empty" failure did not occur. It is sparse: rows appear only on days that carry news | Days with a row in the last 180: 4, 7, 17, 34, 63, 122 of 180. Days with a non-zero count identical to days with a row on all 7, so no empty rows. Mean count 1.18 to 2.55, max 2 to 23. Five-year rows 16, 115, 159, 170, 604, 1,112. Control 181 days, mean 190.9, max 392, 1,797 rows. Earliest row equals earliest article date on all 7. 6 small caps plus control, 2026-08-05 |
| Insider transaction counts, $300M-$2B | The documented endpoint is unusable and its replacement is thin for what S4 needs. No open-market purchase appeared on any name, so distinct_buyer_count had nothing to rank on in this window | `/insider-transactions` returned 0 over 90 days for all 7 including the control; ~~its newest market-wide transactionDate was 2026-04-24 against a 2026-08-05 run~~ [struck, H.2] no market-wide call was ever made and no version of the probe prints `transactionDate`. The staleness reading stands on the per-ticker counts, which trace: the two endpoints disagree over the same seven names and the same 90 days, the legacy one returning 0 transactions for every name including the control while form4 returns 26 for the control with its newest filing dated 2026-07-06. `/sec-filings/{t}/form4` is current: 0, 3, 6, 12, 48, 64 transactions and 0, 3, 4, 6, 8, 12 distinct insiders, control 26 and 15. Codes seen A, D, F, G, M, S. Code P, open-market purchase, was 0 on all 7. 90 days to 2026-08-05 |
| Short interest population, $300M-$2B | Populated on all 7 but as an undated snapshot with no history. A one-month change is computable; a series is not, so `flow_daily`'s weekly grain and its `publication_date` are not achievable from this source | `Technicals.SharesShort` and `SharesShortPriorMonth` non-null on all 7, so a one-month change is computable on all 7. `SharesStats.SharesShort` null on all 7 while `SharesStats.ShortPercentFloat` is populated. No key matching Date anywhere in `Technicals`. `historical=1` with from and to returns a 9-member object, not a date-keyed series: 0 observations over 180 days. 6 small caps plus control, 2026-08-05 |
| Filing date present and sane on small caps | Present and ~~never null everywhere tested~~ [corrected, H.4] null in 7 of 538 periods once every quarter is read, but silently equal to `period_end` on some names. D-46 is achievable from this source only if that case is detected, because the field is populated rather than absent | Sample of 6 plus control: 56 of 56 quarters distinct from `period_end`, gaps 19 to 65 days. Per name CCS 23-30, AI 35-55, NWPX 30-58, KBDC 41-62, PHAT 30-65, BXC 29-55, NVDA 19-28. Income statement agreed with balance sheet on all 7. Separately, in-band RJET.US returns `filing_date` equal to `period_end` in 35 of 73 periods and in 11 of the newest 12, with 0 nulls [confirmed by H.4]. When written this figure was unevidenced rather than wrong: the probe took the newest 8 quarters and could not have produced it, and the 20:24 transcript shows only 7 of the newest 8. H.4 read every quarter and reproduced 35 of 73 and 11 of 12 exactly. Everything above this sentence is 8 quarters per name, 2026-08-05; the wider read and what it changes are in the H.4 block below |
| Bulk EOD row count, one US day | About 50,000 rows on a settled US day. The most recent day is still accreting during the evening and is not a valid freshness reference. Accretion outlives that status: 08-04 was still gaining rows through the evening of 08-05, after it had stopped being the most recent day [H.2] | 2026-07-30 50,204; 07-31 50,148; 08-03 50,029. 08-04 ~~44,708~~ [corrected, H.2] read three times across the evening of 08-05 at 44,665 (19:10 UTC, `filter=extended`, when it was still the last available day), 44,686 (20:24) and 44,708 (20:42), so it is a part-settled day and not a settled one. The two request forms agree where both were used: 08-03 returns 50,029 plain and extended. 08-05 still in progress: 9,072 at 20:42 UTC against 3,544 at ~~19:24~~ [corrected, H.2] 20:24 UTC the same evening, that being the transcript's start time. Settled-day spread over the three settled days 50,029 to 50,204, about 0.35 percent, unchanged by the correction because 08-04 was never in the settled set under the probe's own 90 percent rule. Measured 2026-08-05 |

### Filing dates over every quarter returned

Measured at H.4, 16 calls, transcript
`docs/evidence/phase-P/probe-filing-dates-20260806-133038.txt`. Kept here rather
than moved to the archive with the rest of the pass H record, because D-57 cites
it directly and D-62's per-ticker rule and its floor of four rest on it.

| Ticker | Periods | Equal to `period_end` | Null | Equal in newest 12 | Clean gaps | Min | Max | Median | Above 65 |
|---|---|---|---|---|---|---|---|---|---|
| CCS.US | 55 | 7 | 0 | 0 of 12 | 48 | 23 | 65 | 33 | 0 |
| AI.US | 28 | 6 | 0 | 0 of 12 | 22 | 29 | 56 | 38 | 0 |
| NWPX.US | 130 | 9 | 5 | 0 of 12 | 116 | 30 | 210 | 39 | 23 |
| KBDC.US | 19 | 8 | 0 | 1 of 12 | 11 | 39 | 62 | 44 | 0 |
| PHAT.US | 34 | 6 | 0 | 0 of 12 | 28 | 30 | 89 | 40 | 3 |
| BXC.US | 90 | 1 | 2 | 0 of 12 | 87 | 28 | 88 | 37 | 4 |
| NVDA.US | 109 | 5 | 0 | 0 of 12 | 104 | -4 | 86 | 24 | 2 |
| RJET.US | 73 | 35 | 0 | 11 of 12 | 38 | -16 | 88 | 39 | 6 |
| All | 538 | 77 | 7 | | 454 | -16 | 210 | | 38 |

Equality appears on all eight names and concentrates in older history: six of
the eight show 0 of the newest 12, so a rate measured on recent quarters
understates what a five-year backfill meets. NWPX.US alone accounts for
23 of the 38 gaps above 65, which is the clustering D-62's per-ticker
substitution rests on. The income statement agreed with the balance sheet on seven of eight
names and disagreed on NVDA.US in 2 of 109 periods.

---

## Measured figures

Every figure in `ARCHITECTURE.html` is an estimate. As real numbers arrive, record
them here rather than editing the architecture, and note the gap where it is large.

| Figure | Estimated | Measured | Date |
|---|---|---|---|
| Universe size | ~2,000 | | |
| Candidates per night | 26-30 | | |
| Screen overlap | 10-15% | | |
| Cache hit rate | >90% | | |
| Annual cost | ~$50 | | |
| Database size after backfill | ~5 GB | | |
| Full backfill rebuild time | minutes | | |

---

## Phase P, data probe

**Built.** A scratch console tool at `tools/probe`, deleted at checkpoint 0.1 as
its own scope said it would be. Its four run transcripts are kept, at
`docs/evidence/phase-P/`, because every figure in Probe findings above traces to
one of them and none is checkable without them.

**HEAD** `3099e66` when the phase was closed, merged at `b772eb2`. No test
project existed yet, so no test count.

**Produced** D-57 to D-63 and the six rows in Probe findings above. D-58, D-59,
D-60 and D-62 each rest on a figure in that table, which is why it stays here
rather than going to the archive with the rest of the phase P record.

**Owed forward.** Five obligations, all in `BUILD_PLAN.md`'s carried obligations
table rather than repeated here: news depth and flow coverage constraining what
phase 1 can promise, D-46 revisiting if filing dates are absent on small caps,
entitlement being per endpoint and invisible in the account payload, the base
rate of unknown filing dates, and the S4 open-market purchase base rate. The
last two are queries against tables phase 1 populates, read at its sign-off.

## Phase 0, rails

**Built.** Seven projects on the layout in `CLAUDE.md` §4 and D-56. Postgres
schema and migrations, snapshot-first, 33 tables in `public` and the ledger in
`meta`. The stage abstraction, the registry, and `IStageData` as the only route
to data. Run logging, the write-ownership conformance test, the Api isolation
test, a bare run viewer, one no-op stage end to end, and a CI workflow.

**HEAD** `d9cb5df`, `main` after pass O, which amended phase 0's code at O.1 and
O.3. The phase itself merged at `8fb6a6e`.

**Tests** 25, all passing.

**Signed off 2026-08-06. Both steps below, with what each does not cover.**

**Step 1, CI green on the phase branch.** Every step of `.github/workflows/ci.yml`
was run in its own order at `d9cb5df`, from a clone with no secrets file present,
against a database dropped first:

  guards.ps1                    exit 0, four checks, zero each
  dotnet restore                exit 0
  dotnet build --no-restore     0 warnings, 0 errors
  no secrets file present       none found, as expected
  migrate, from an empty server created database, 0001_snapshot.sql applied
  migrate again                 nothing to apply, gate passed
  dotnet test --no-build        Passed 25, Failed 0

**The line CI does not cover, and why.** All of them, because **no CI run has
ever executed a step of this workflow.** The sequence, since the diagnosis
changed twice:

  Nothing registered while `ci.yml` sat only on `phase-0-rails`, because GitHub
  discovers workflows from the default branch. Merging `phase-0-rails` at
  `8fb6a6e` registered it, and the workflow reads `active`.

  That merge queued one run, `31127684749`. It sat fifteen minutes, no runner
  was assigned, and GitHub cancelled it: *the job was not acquired by Runner of
  type hosted even after multiple attempts*. `runner_name` empty, zero steps,
  nothing checked out. It says nothing about the code either way.

  Merging pull request 2 queued nothing at all. The repository's total run count
  is 1.

So hosted runners are not being allocated to this account. Actions is enabled
with `allowed_actions: all`, the YAML parses with valid triggers, and the
workflow is registered and active, so it is none of those. The billing endpoint
needs a token scope this session does not have and the condition was not read
directly. Both pull requests were merged with the gap stated rather than with
`CLAUDE.md` §10 quietly satisfied.

**Step 2, a review in a session that did not build the phase.** Ran at
`d4baeaf`, in a session with no involvement in the build and no commit in this
repository. It found one invariant breach in the whole tree, eight of nineteen
definition-of-done lines asserted rather than evidenced, two recorded counts not
tracing to what produced them, and one recorded claim that was false. Its finding
is in `docs/archive/process-2026-08.md`.

**What step 2 does not cover.** Pass O corrected four of its findings and changed
four source files after it ran, so the reviewed artefact is not this one. The
session that made those corrections is the same session that ran the review and
is disqualified from re-running it over its own corrections. What stands behind
the corrected state is the local run above and `guards.ps1`, which now fails on
the class of breach the review found by hand.

**Next phase authored.** Phase 1's checkpoints 1.1 to 1.10 are in
`BUILD_PLAN.md`, authored at K.9 before this phase closed.

**Owed forward.** In `BUILD_PLAN.md`'s carried obligations: the write-ownership
test extending as each phase adds tables, the Ui's contracts assembly,
`NoOpStage`'s name, `TableWrite.Columns`, and the five probe patterns phase 1's
ingest must not inherit. Also owed and recorded below.

## Open items carried forward

Found and not closed. Each names what triggers it. The pass narratives behind
them are in `docs/archive/process-2026-08.md`.

| # | Item | Trigger |
|---|---|---|
| 1 | `equity` is a read target with no store. `ARCHITECTURE.html` §3 lists C18 reading it and `SCHEMA.md` declares no such table | Phase 7, which builds the risk layer |
| 2 | `FIXTURES.md`'s filing-date entry says "before the filing date" where D-62 makes it the effective filing date | Phase 1, checkpoint 1.10 |
| 3 | `CLAUDE.md` §9 carries the same wording | The next authored amendment to §9 |
| 4 | Checkpoint 1.8's events-ingest half is not reachable from phase 1's definition of done | Phase 1 sign-off |
| 5 | Checkpoint 1.1 is reachable from phase 1's definition of done only through "one night lands", which exercises the HTTP client without asserting the token auth, the explicit `fmt`, the encoded filter form or the rate limit | Phase 1 sign-off |
| 6 | `ARCHITECTURE.html` states writes three times over, in its own Written-by column, in the §3 catalogue and in `SCHEMA.md`. Every write-column defect in passes K through N came from that duplication. Its stated trigger, phase 0 proving the registry and its test, has fired | An authored change to `ARCHITECTURE.html` |
| 7 | The 0.4 conformance test asserts the registry against `SCHEMA.md`'s table list and against a hardcoded list of the three permitted splits, not against `SCHEMA.md`'s own writer declarations, which are stated in prose that varies in form | Phase 1, when real components make the check worth something |
| 8 | 0.5 has no permanent fixture for its failure path, and no test exercises `/api/runs` or renders the viewer. Both are code and belong to a phase rather than to a correction pass | Phase 9, or the next phase touching either |
| 9 | `TheCompiledApiCarriesNoPipelineDependency` reads `deps.json` from disk. Its stale-artifact defect was closed by having the test project reference the Api, which holds only while the build succeeds: after a failed build, `dotnet test --no-build` reads the previous artifact and the assertion passes against it. CI is not exposed, because its Build step gates Test | Phase 9, with item 10 |
| 10 | The Api's `appsettings.Secrets.json` flows into the test output directory through the 0.5 project reference, so a local test run can take its connection string from a file other than the test project's own. All four are byte identical today | Whenever the two need to differ |
