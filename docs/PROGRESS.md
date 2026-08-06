# PROGRESS.md

What has actually been built, as opposed to what is designed.

**This file is written by the build, not authored.** Test counts, HEAD shas,
measured timings and row counts are observations and belong to whoever ran them.
Correct them directly. Do not record intentions here.

Corpus version: 0.1.0 (2026-08-05, initial document set)

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

Note on the supersession convention and how far back it reaches

  Two short interest facts were removed by plain deletion before the supersession
  rule existed, then re-inserted and struck once it did. They were restated because
  they were consequences of D-58, the same decision that drove the change being
  applied. That is the boundary: the convention applies from D-58 forward.

  Removals made under earlier decisions stay as they are. The corpus history is not
  retroactively marked up.

  Recorded because without a stated boundary a later session reads those
  re-insertions as licence to rework every prior deletion in the corpus, which would
  turn a convention about design history into an excuse to rewrite it.

---

## Conformance passes

Summary index. The detail lives in the sign-off block above.

| Phase | Date | Invariants checked | Finding |
|---|---|---|---|
| P | 2026-08-06 | None named for phase P. `CLAUDE.md` §7 and §10, which the prompt scoped in | Four questions carry numeric answers. Four figures inside those answers trace to no probe call. Reconciliation compares the prompt against the design rather than against plan detail and omits three divergences. Authorship clean, no secret in any blob. Three cross-reference defects, all older than the phase |

### Phase P conformance finding, 2026-08-06, read at HEAD `2802e75`

A session with no involvement in the build and none in the C, E or G document
passes. Divergences are stated and not corrected.

**1 Definition of done.** The plan states two lines plus a secrets clause. *All
four questions have numeric answers recorded in `PROGRESS.md`*: met at `8a48990`,
six rows filled, six lines changed and nothing else in the file touched. *Any that
came back badly has a corresponding entry in `DECISIONS.md`*: met at HEAD by D-57
to D-61, none written by the build, which is correct under section 13 and means
the line was closed by a later authored pass rather than by the phase. *No API key
enters the repository*: verified independently under check 4. The prompt's
per-checkpoint lines are a different matter. P.2, P.3 and P.4 each define done as
something the probe prints, and the printed artifact is the transcript under
`probe-output/`, which `.gitignore` excludes by `**/probe-output/`. Three
transcripts exist on the build machine and were read for this pass; nothing in the
repository evidences them. P.1's *solution builds* is likewise asserted rather than
recorded. No line is false, but four are evidenced only outside the repository.

**2 Probe findings against the probe.** Every value in the six rows was traced
against `tools/probe/Program.cs`, every earlier committed version of it, and the
three run transcripts. The sentiment and short interest rows trace completely.
Four figures do not correspond to anything the probe measures.

  `IPO 2024-05-22`, and the answer clause *the sixth listed in 2024*. No version of
  the probe requests a listing or IPO date. What it measured is KBDC's earliest
  article, 2025-06-13.

  *its newest market-wide transactionDate was 2026-04-24*. Every call to
  `/insider-transactions` passes `code={ticker}`, so no market-wide call exists,
  and no version prints `transactionDate`. Not in any transcript. Commit `52b64ba`
  records a related but different observation: NVDA over two years, newest
  `transactionDate` 2026-03-25.

  *RJET.US ... 35 of 73 periods and ... 11 of the newest 12*. `Quarters()` takes
  the newest eight in every committed version. The surviving RJET transcript, from
  the 20:24 run whose sample included that name, shows seven of eight equal with
  zero nulls. 73 periods cannot come from this tool. D-57 carries the same figure
  as its justification.

  *3,544 at 19:24 UTC*. The value is real and appears twice in
  `probe-20260805-202454.txt`, whose start time is 20:24:54 UTC and whose bulk
  section ran later still. The three transcripts start at 19:10, 20:24 and 20:42
  UTC. No run at 19:24 exists.

Separately, the control's *1,000 in 90 days* is the `limit=1000` request cap rather
than a count, since the 90-day news call does not page. The cell marks the
five-year total as a floor and does not mark this one.

**3 Authorship boundaries.** The set is cleanly identifiable as
`git rev-list b1a0095..phase-p` less the three C commits: `2901ab2`, `fd64f51`,
`92ff200`, `7c04769`, `2f754ce`, `52b64ba`, `8a48990`, `1d05300`, `189e3d9`. Across
all nine the files written are `Directory.Build.props`, `Directory.Packages.props`,
`StockResearcherLab.slnx`, `docs/PROGRESS.md`,
`prompts/spent/phase-P-data-probe.md`, `tools/probe/Program.cs` and
`tools/probe/probe.csproj`. None of the six forbidden documents, and `.gitignore`
untouched as P.1 required. `prompts/spent/` was written twice. `189e3d9` is header
only, `ISSUED` to `SPENT` with `Produced` filled, which `prompts/README.md`
authorises where it names the header fields and the three status values. `2901ab2`
changed the header and the body: a `Corrected:` line added, and four body changes,
"section 1 and section 5" to "Sections 7 and 10", "per CLAUDE.md section 1" to
"section 7", a new commit-per-checkpoint paragraph and a new AFTER block. It was
made before P.1 while the header read `ISSUED`. The corpus authorises amending an
unrun prompt when a decision changes it; this edit is not decision-driven, it
asserts that the archive did not match the text actually issued. No corpus rule
covers that case, and no evidence of the issued text exists outside the build
session's own account. Disclosed in the header and in the reconciliation block.

**4 Secrets.** Method: enumerated every object in the object database with
`git cat-file --batch-all-objects --batch-check`, 60 blobs, which reaches
unreachable objects as well as reachable ones. `git fsck` reports one dangling
blob and one dangling commit; both were inspected, the blob is a `.claude`
permissions file and the commit is a rewritten form of the baseline. Every blob was
scanned for `api_token=` followed by anything but a placeholder, the provider's
hex-dot-digits token shape, `sk-` keys, and any run of 32 or more token characters.
The only hits are `api_token={Uri.EscapeDataString(token)}` in four versions of
`Program.cs`, comment rules of dashes, and one config key name. No literal
anywhere. `git status --ignored` shows both `appsettings.Secrets.json` and
`tools/probe/appsettings.Secrets.json` as ignored rather than untracked, and
`git check-ignore -v` attributes both to `.gitignore:5`. The only tracked
secrets-shaped path is `appsettings.Secrets.example.json`, which is what D-55
permits. Redaction is a single helper inside the request function and the
transcripts confirm every printed URL carries `api_token=***`.

**5 Reconciliation.** The block is headed *spent prompt against what the design
needs*. Step 3 and `CLAUDE.md` §3 ask for the prompt against `BUILD_PLAN.md`'s
detail, so the comparand differs and divergences against the plan are not reached.
Its own sentence introduces "Eight items where the prompt asked for less than the
design needs" and then labels four of the eight `more`. Four carry `defect`, which
is not one of the three prescribed forms, though it reads as "asked for less".
Three divergences against plan detail are not recorded: the prompt bounded P.4.4 to
the last eight quarters where the plan says "per quarter", which is the bound that
put the RJET figure out of the probe's reach and is *less*; the prompt required
per-year news counts, distinct insider counts and 180-day short interest
observations that the plan does not ask for, which is *more*; and the plan's
definition of done requires a `DECISIONS.md` entry for anything that came back
badly while the prompt instructs "Do not open DECISIONS.md", which is *different*
and was resolved by the later passes rather than recorded. No divergence of the
*different* class appears at all. The archive correction is recorded with its
cause, which is the right treatment of it.

**6 Measured against inferred.** The four figures in check 2 sit in the Measured
column and are not probe measurements. The short interest row is the model of how
to do this correctly: it names the call, `historical=1` with from and to returning
a nine-member object, so a reader can see what was tried and judge whether the
limitation is the provider's. The insider row's answer that the documented endpoint
is unusable is a fair reading, but the probe alone establishes only that it
returned zero rows for all seven names over ninety days; the staleness claim rests
on the unevidenced figure. Two further points of uncertainty are not recorded:
2026-08-04 was read three times through the evening at 44,665, 44,686 and 44,708
rows, only the last is recorded, and nothing notes the day was still accreting a
day later, though the probe's own 90 percent rule and the recorded spread both
exclude it from the settled set; and the P.2 scaffold run at 19:10 UTC took 403 on
fundamentals, form4, insider transactions and the screener while the 20:24 run took
none, so what the subscription returned changed during the session and no record
says so.

**7 Cross-references.** Pattern:
`grep -rnoiE "(§[[:space:]]*[0-9]+([.][0-9]+)?|sections?[[:space:]]+[0-9]+([.][0-9]+)?)" --include=*.md --include=*.html --include=*.cs .`
It anchors on the section token alone and requires nothing before it, so backticks,
brackets, markdown link syntax and abbreviations such as "ARCH section 14" are all
caught and the target file is established by reading each hit rather than by
matching a filename first. 48 hits across 12 files, including `prompts/README.md`,
which sits outside `docs/`. `ARCHITECTURE.html` numbers its sections in
`<span class="secno">`, so references into it are checkable. Three do not resolve,
all present in the baseline commit `b1a0095` and none touched by phase P.
`docs/BUILD_PLAN.md:242` cites `CLAUDE.md` §8 for the rule against tuning screens
on forward returns, which is §11, and `docs/VALIDITY.md:158` cites §11 for the same
rule. `prompts/README.md:27` cites `CLAUDE.md` §13 for the reconciliation question
quoted directly beneath it, which is §14, and the spent prompt cites §14 correctly.
`docs/ARCHITECTURE.html:134` sends a reader to "section 14" for the presentation
layer, which is §15. Every `D-<n>` reference in the corpus was checked the same
way: D-1 to D-61 all resolve, and the only apparent miss, D-250, is arithmetic in
the two-pass backfill note.

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
