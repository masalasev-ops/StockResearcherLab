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
| P Data probe | NOT STARTED | | |
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
| News archive depth for small caps | | |
| Sentiment series coverage, $300M-$2B | | |
| Insider transaction counts, $300M-$2B | | |
| Short interest population, $300M-$2B | | |
| Filing date present and sane on small caps | | |
| Bulk EOD row count, one US day | | |

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
