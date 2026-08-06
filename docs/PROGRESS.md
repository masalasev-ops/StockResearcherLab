# PROGRESS.md

What has actually been built, as opposed to what is designed.

**This file is written by the build, not authored.** Test counts, HEAD shas,
measured timings and row counts are observations and belong to whoever ran them.
Correct them directly. Do not record intentions here.

Corpus version: 0.1 (2026-08-05, initial document set)

---

## Phase status

| Phase | Status | HEAD | Notes |
|---|---|---|---|
| P Data probe | IN PROGRESS | d58bea9 | All five checkpoints landed and all six findings measured. D-57 to D-61 applied with their consequent edits. Not signed off: steps 2 to 5 of the sign-off procedure are outstanding, starting with a conformance pass in a fresh session |
| 0 Rails | NOT STARTED | | |
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

Filled by phase P. Until then these are open questions, and several downstream
decisions assume answers that have not been measured.

| Question | Answer | Measured |
|---|---|---|
| News archive depth for small caps | Five years is reachable. 5 of 6 return articles from the window start; the sixth listed in 2024. Depth is not the constraint, volume is | Earliest article 2021-08-05 to 2021-08-23 for 5 of 6, window opened 2021-08-05. KBDC 2025-06-13, IPO 2024-05-22. Five-year totals 177, 199, 255, 924, 3,474, and KBDC 19. Last 90 days 2, 3, 4, 12, 44, 144. Control NVDA 62,503 over five years, a floor since the 20-page cap bound in 2025 and 2026, and 1,000 in 90 days. 6 small caps plus control, 2026-08-05 |
| Sentiment series coverage, $300M-$2B | Exists and is never empty where present, so the "series exists but is empty" failure did not occur. It is sparse: rows appear only on days that carry news | Days with a row in the last 180: 4, 7, 17, 34, 63, 122 of 180. Days with a non-zero count identical to days with a row on all 7, so no empty rows. Mean count 1.18 to 2.55, max 2 to 23. Five-year rows 16, 115, 159, 170, 604, 1,112. Control 181 days, mean 190.9, max 392, 1,797 rows. Earliest row equals earliest article date on all 7. 6 small caps plus control, 2026-08-05 |
| Insider transaction counts, $300M-$2B | The documented endpoint is unusable and its replacement is thin for what S4 needs. No open-market purchase appeared on any name, so distinct_buyer_count had nothing to rank on in this window | `/insider-transactions` returned 0 over 90 days for all 7 including the control; its newest market-wide transactionDate was 2026-04-24 against a 2026-08-05 run. `/sec-filings/{t}/form4` is current: 0, 3, 6, 12, 48, 64 transactions and 0, 3, 4, 6, 8, 12 distinct insiders, control 26 and 15. Codes seen A, D, F, G, M, S. Code P, open-market purchase, was 0 on all 7. 90 days to 2026-08-05 |
| Short interest population, $300M-$2B | Populated on all 7 but as an undated snapshot with no history. A one-month change is computable; a series is not, so `flow_daily`'s weekly grain and its `publication_date` are not achievable from this source | `Technicals.SharesShort` and `SharesShortPriorMonth` non-null on all 7, so a one-month change is computable on all 7. `SharesStats.SharesShort` null on all 7 while `SharesStats.ShortPercentFloat` is populated. No key matching Date anywhere in `Technicals`. `historical=1` with from and to returns a 9-member object, not a date-keyed series: 0 observations over 180 days. 6 small caps plus control, 2026-08-05 |
| Filing date present and sane on small caps | Present and never null everywhere tested, but silently equal to `period_end` on some names. D-46 is achievable from this source only if that case is detected, because the field is populated rather than absent | Sample of 6 plus control: 56 of 56 quarters distinct from `period_end`, gaps 19 to 65 days. Per name CCS 23-30, AI 35-55, NWPX 30-58, KBDC 41-62, PHAT 30-65, BXC 29-55, NVDA 19-28. Income statement agreed with balance sheet on all 7. Separately, in-band RJET.US returns `filing_date` equal to `period_end` in 35 of 73 periods and in 11 of the newest 12, with 0 nulls. 8 quarters per name, 2026-08-05 |
| Bulk EOD row count, one US day | About 50,000 rows on a settled US day. The most recent day is still accreting during the evening and is not a valid freshness reference | 2026-07-30 50,204; 07-31 50,148; 08-03 50,029; 08-04 44,708. 08-05 still in progress: 9,072 at 20:42 UTC against 3,544 at 19:24 UTC the same evening. Settled-day spread 50,029 to 50,204, about 0.35 percent. Measured 2026-08-05 |

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

## Phase sign-offs

One block per phase, written at sign-off. All five steps in `BUILD_PLAN.md` must have
happened before a phase is marked DONE above.

Template, copied per phase:

```
### Phase <n>, signed off <date>, HEAD <sha>

Definition of done      every line run, results below
Conformance pass        session <id>, finding below
Reconciliation          prompt vs plan, divergences below
Next phase authored     checkpoints <n+1>.1 .. <n+1>.x added to BUILD_PLAN
Corpus version          bumped to <x.y.z>, CHANGELOG entry added

Definition of done
  <line>   <observed result>

Conformance finding
  <what the fresh session found, or "matches">

Reconciliation: spent prompt against plan detail
  <divergence>  <less | more | different>  <what was done about it>

Measured figures moved from estimate
  <figure>  <estimate>  <measured>
```

### Phase P, not signed off, built on branch `phase-p` at 8a48990

Recorded now rather than at sign-off because the divergences are evidence about
what was asked, and the session that produced them is the one that knows why.
Steps 2 to 5 of the sign-off procedure have not happened.

Reconciliation: spent prompt against what the design needs

  The archive at `prompts/spent/phase-P-data-probe.md` was stale against the text
  actually issued. It read "section 1 and section 5" and "per CLAUDE.md section 1",
  and lacked the commit-per-checkpoint paragraph and the AFTER block. The issued
  text says sections 7 and 10, which is correct against the current CLAUDE.md, where
  section 7 is claims about the code and section 10 is git and secrets. The archive
  header records an amendment for "CLAUDE.md section renumbering", so that amendment
  had been applied incompletely. Corrected to the issued text before P.1, with a
  `Corrected` header line. The body is otherwise untouched.

  Eight items where the prompt asked for less than the design needs. None was written
  into the prompt body, which is spent from the moment a session works from it.

  full D-4 filter on the sample          defect   price and volume floors applied, not the cap band alone
  institutional ownership measured       defect   S4 ranks on four inputs; the prompt measured neither this nor its dating
  sentiment over five years not 180 days defect   180 days cannot see whether S3 is backfillable over the five years D-47 requires
  ADRs admitted, type read off the feed  defect   D-4 admits common stock and ADRs; the feed carries no separate ADR type
  bulk EOD over five days not one        more     a tolerance is a band; one day gives only a level, and it caught the accretion below
  account plan and quota printed         more     endpoint availability is a tier answer and the quota is finite
  news 90-day density alongside 5 years  more     digests are forward-only, so density is the figure a decision rests on
  SharesShortPriorMonth captured         more     the only backfillable form of short_interest_change if no series exists

Observations that bear on later phases

  The dollar volume proxy does not survive into phase 1. The bulk feed carries 14, 50
  and 200-day average share volume and no median dollar volume, so selection used
  `avgvol_50d * adjusted_close` for `universe.min_adv_20d`. Average volume is
  unadjusted while `adjusted_close` is adjusted, so the product understates dollar
  volume for any name that split inside the window. Adequate for picking six sample
  names, not for the universe filter, which must compute the metric from the price
  series. Recommended as a carried obligation from P to phase 1.

  The D-4 funnel on 2026-08-03 gave 50,029 rows, 17,497 common stock, 2,940 in the
  $300M-$2B band, 2,046 clearing the $5 price floor and 977 clearing the volume proxy.
  977 is the small bucket only, not the universe size, and is not comparable to the
  ~2,000 estimate in the Measured figures table above.

  The megacap control earned its place. It caught two defects that every small cap
  would have hidden: an insider endpoint whose data lags by months returning a
  plausible-looking zero, and a selection pool drawn from a part-settled trading day.

---

## Conformance passes

Summary index. The detail lives in the sign-off block above.

| Phase | Date | Invariants checked | Finding |
|---|---|---|---|

---

## Corpus consistency passes

Run over the documents themselves rather than over code. Records what was found so a
later reader can tell a settled figure from one that drifted and was corrected.

### 2026-08-05, corpus v0.1.0, before any code

Six inconsistencies found and corrected, all in `ARCHITECTURE.html`, which had been
edited repeatedly while the other documents were written once.

| # | Defect | Correction |
|---|---|---|
| 1 | Title said thirty-one components; thirty-three are catalogued | Title corrected |
| 2 | Config table called `screen_config` here and `config_rows` in `SCHEMA.md` | Unified on `config_rows`, with screen definitions under the `screens.*` keys |
| 3 | Figure 2 said a 3,800 token prefix and 600 token block; section 7 said 4,600 and 650; the cost table said 800 | Unified on 4,600 and 800 |
| 4 | The headline ingest row said ~30 candidates | Corrected to ~28 |
| 5 | Two references to "portfolio A" and "portfolio D" survived the rename | Corrected to the portfolio names |
| 6 | `news_digest`, `calibration` and `local_model_config` were declared in `SCHEMA.md` but absent from the data store matrix | Rows added |

Defect 2 was the one that mattered. A component instructed to read `screen_config`
and a schema declaring `config_rows` would have produced two tables and a
one-writer-per-table violation that the registry test could not have caught, because
both would have had exactly one writer.
