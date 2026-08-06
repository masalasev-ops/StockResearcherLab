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
| P Data probe | IN PROGRESS | 7a0e8b1 | All five checkpoints landed and all six findings recorded. D-57 to D-61 applied with their consequent edits. Sign-off step 2 has run and phase P did not pass it: four figures had no measurement behind them and the evidence was excluded from the repository. Corrective pass H answers the finding and corrective pass J carries its findings into authored amendments, D-57 superseded by D-62. Step 2 re-runs after both, in a session with no involvement in phase P or in passes H or J. Steps 3 to 5 outstanding |
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
| News archive depth for small caps | Five years is reachable. 5 of 6 return articles from the window start; ~~the sixth listed in 2024~~ [struck, H.2] the sixth returns nothing before 2025-06-13. Depth is not the constraint, volume is | Earliest article 2021-08-05 to 2021-08-23 for 5 of 6, window opened 2021-08-05. KBDC 2025-06-13, ~~IPO 2024-05-22~~ [struck, H.2] no listing date was measured, and one would not account for the thirteen months between any 2024 listing and the first article anyway. Five-year totals 177, 199, 255, 924, 3,474, and KBDC 19. Last 90 days 2, 3, 4, 12, 44, 144. Control NVDA 62,503 over five years, a floor since the 20-page cap bound in 2025 and 2026, and ~~1,000 in 90 days~~ [corrected, H.2] at least 1,000 in 90 days, which is the `limit=1000` request cap on a call that does not page rather than a count. 6 small caps plus control, 2026-08-05 |
| Sentiment series coverage, $300M-$2B | Exists and is never empty where present, so the "series exists but is empty" failure did not occur. It is sparse: rows appear only on days that carry news | Days with a row in the last 180: 4, 7, 17, 34, 63, 122 of 180. Days with a non-zero count identical to days with a row on all 7, so no empty rows. Mean count 1.18 to 2.55, max 2 to 23. Five-year rows 16, 115, 159, 170, 604, 1,112. Control 181 days, mean 190.9, max 392, 1,797 rows. Earliest row equals earliest article date on all 7. 6 small caps plus control, 2026-08-05 |
| Insider transaction counts, $300M-$2B | The documented endpoint is unusable and its replacement is thin for what S4 needs. No open-market purchase appeared on any name, so distinct_buyer_count had nothing to rank on in this window | `/insider-transactions` returned 0 over 90 days for all 7 including the control; ~~its newest market-wide transactionDate was 2026-04-24 against a 2026-08-05 run~~ [struck, H.2] no market-wide call was ever made and no version of the probe prints `transactionDate`. The staleness reading stands on the per-ticker counts, which trace: the two endpoints disagree over the same seven names and the same 90 days, the legacy one returning 0 transactions for every name including the control while form4 returns 26 for the control with its newest filing dated 2026-07-06. `/sec-filings/{t}/form4` is current: 0, 3, 6, 12, 48, 64 transactions and 0, 3, 4, 6, 8, 12 distinct insiders, control 26 and 15. Codes seen A, D, F, G, M, S. Code P, open-market purchase, was 0 on all 7. 90 days to 2026-08-05 |
| Short interest population, $300M-$2B | Populated on all 7 but as an undated snapshot with no history. A one-month change is computable; a series is not, so `flow_daily`'s weekly grain and its `publication_date` are not achievable from this source | `Technicals.SharesShort` and `SharesShortPriorMonth` non-null on all 7, so a one-month change is computable on all 7. `SharesStats.SharesShort` null on all 7 while `SharesStats.ShortPercentFloat` is populated. No key matching Date anywhere in `Technicals`. `historical=1` with from and to returns a 9-member object, not a date-keyed series: 0 observations over 180 days. 6 small caps plus control, 2026-08-05 |
| Filing date present and sane on small caps | Present and ~~never null everywhere tested~~ [corrected, H.4] null in 7 of 538 periods once every quarter is read, but silently equal to `period_end` on some names. D-46 is achievable from this source only if that case is detected, because the field is populated rather than absent | Sample of 6 plus control: 56 of 56 quarters distinct from `period_end`, gaps 19 to 65 days. Per name CCS 23-30, AI 35-55, NWPX 30-58, KBDC 41-62, PHAT 30-65, BXC 29-55, NVDA 19-28. Income statement agreed with balance sheet on all 7. Separately, in-band RJET.US returns `filing_date` equal to `period_end` in 35 of 73 periods and in 11 of the newest 12, with 0 nulls [confirmed by H.4]. When written this figure was unevidenced rather than wrong: the probe took the newest 8 quarters and could not have produced it, and the 20:24 transcript shows only 7 of the newest 8. H.4 read every quarter and reproduced 35 of 73 and 11 of 12 exactly. Everything above this sentence is 8 quarters per name, 2026-08-05; the wider read and what it changes are in the H.4 block below |
| Bulk EOD row count, one US day | About 50,000 rows on a settled US day. The most recent day is still accreting during the evening and is not a valid freshness reference. Accretion outlives that status: 08-04 was still gaining rows through the evening of 08-05, after it had stopped being the most recent day [H.2] | 2026-07-30 50,204; 07-31 50,148; 08-03 50,029. 08-04 ~~44,708~~ [corrected, H.2] read three times across the evening of 08-05 at 44,665 (19:10 UTC, `filter=extended`, when it was still the last available day), 44,686 (20:24) and 44,708 (20:42), so it is a part-settled day and not a settled one. The two request forms agree where both were used: 08-03 returns 50,029 plain and extended. 08-05 still in progress: 9,072 at 20:42 UTC against 3,544 at ~~19:24~~ [corrected, H.2] 20:24 UTC the same evening, that being the transcript's start time. Settled-day spread over the three settled days 50,029 to 50,204, about 0.35 percent, unchanged by the correction because 08-04 was never in the settled set under the probe's own 90 percent rule. Measured 2026-08-05 |

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

  [superseded by H.5, which compares the prompt against `BUILD_PLAN.md`'s phase P
  detail as step 3 requires. Kept rather than replaced because it is the build
  session's own account of what it found. Two things to read it with: the eight
  items are classified against the design rather than against the plan, and the
  sentence introducing all eight as the prompt asking for less is contradicted by
  the four of them labelled `more`.]

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
| P | 2026-08-06 | Same. Second pass, read at HEAD `5afcd52` | Every figure in the Measured column now traces to a committed call or transcript and the four figures the first pass found are closed. Three P.1 lines are still asserted rather than evidenced. The committed transcripts do not account for the run date's API consumption, so the evidence set is incomplete and nothing says so. The redone reconciliation drops two items the list it replaced carried, both underpinning authored decisions. One code comment asserts a provider limitation with no committed measurement, and two references still point at what a later decision or section replaced. Phase P is not signed off by this pass |

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

### Phase P conformance finding, second pass, 2026-08-06, read at HEAD `5afcd52`

A session with no involvement in the build, in the C, E, G, H or J passes, or in
the first conformance pass. Commit sets were derived from the log rather than
adopted from a report. Divergences are stated and not corrected.

**Commit sets used.** Build: `2901ab2`, `fd64f51`, `92ff200`, `7c04769`,
`2f754ce`, `52b64ba`, `8a48990`, `1d05300`, `189e3d9`. C: `ede929b`, `e2d238c`,
`6ec37f3`. E: `ab7308b`, `72dd12d`, `f037557`, `d58bea9`, `88a8f6e`, where E.6 is
the earliest commit of the pass rather than the latest. G: `f0466ac`, `637acc8`,
`df8cc3c`. First conformance pass: `121686c`, one commit, `PROGRESS.md` only, 136
insertions. H: `824029e` to `88c07d1`, nine commits, H.4 spanning two. J:
`2780a84` to `3dfd533`. Outside every pass: `b1a0095`, `ff862bc` and six merges.
42 objects, all accounted for. Read as git range notation each pass boundary
would exclude its own opening commit, which drops C.1, E.6, G.1, H.1 and J.1; the
sets above are inclusive of both endpoints.

**1 Definition of done.** The plan states two lines plus a secrets clause and the
prompt adds one per checkpoint. *All four questions have numeric answers recorded
in `PROGRESS.md`*: met, six rows filled at `8a48990` with six lines changed and
nothing else in the file, satisfying the plan's *editing the existing table in
place* and the prompt's byte-identical clause together. *Any that came back badly
has a corresponding entry in `DECISIONS.md`*: met at HEAD by D-57 to D-62, and
not met when the phase ended. The prompt forbade opening `DECISIONS.md`, so every
entry was authored afterwards by the C, E and J passes. The line is closed and the
phase did not close it. *No API key enters the repository*: verified
independently under check 4. P.2, P.3, P.4 and P.5 each define done as something
printed, and all four are now evidenced inside the repository:
`probe-20260805-191003.txt` shows the scaffold running end to end with five 403s
printed with status and body, `probe-20260805-204226.txt` shows `in band: 6
distinct sectors: 6` and numbers for all seven tickers across all four
measurements, and `8a48990`'s diff shows the table edited in place. H.1 is what
made these three checkable. Three lines remain asserted rather than evidenced,
all in P.1: *solution builds*, *`git status --ignored` shows
appsettings.Secrets.json as ignored rather than untracked*, and *a grep of
everything staged for the first commit finds no token-shaped string*. Nothing in
the repository records any of the three. This pass ran the first independently,
`dotnet build StockResearcherLab.slnx` on .NET 10.0.301, succeeding with 0
warnings and 0 errors, and re-established the other two under check 4. That the
lines are true does not make them recorded.

**2 Probe findings against the probe.** Every value in the six rows was followed
from the call that produced it to the row that records it, against
`tools/probe/Program.cs`, every committed version of it, and the four committed
transcripts. **Every figure in the Measured column traces.** The four the first
pass found are closed, and closed three different ways: the listing date and the
market-wide `transactionDate` are struck as never measured, the 19:24 timestamp
is corrected to 20:24, and the RJET figure is reproduced exactly by H.4. The
`limit=1000` control figure it noted separately is now marked as a request cap
rather than a count. Spot checks that carry the most weight: the news five-year
totals 177, 199, 255, 924, 3,474 and 19 and the 90-day counts 2, 3, 4, 12, 44 and
144 are the six small caps of the 20:42 run read off in sorted order; sentiment
days with a row 4, 7, 17, 34, 63 and 122 likewise, with non-zero equal to rows on
all seven, which is what makes the *exists but empty* case testable; form4 0, 3,
6, 12, 48, 64 and distinct insiders 0, 3, 4, 6, 8, 12 with control 26 and 15;
codes A, D, F, G, M, S as the union across seven names with P absent from all of
them; the three 08-04 readings 44,665, 44,686 and 44,708 sit in the 19:10, 20:24
and 20:42 transcripts respectively. The H.4 table reproduces line for line and
its totals are the column sums: 538 periods, 77 equal, 7 null, 454 clean gaps, 38
above 65. Three things the trace surfaced that no row states.

  The recorded sample is not the only sample the phase measured. The 20:24 run
  selected CCS, ALNT, IMTX, RJET, MFIC and OOMA from a part-settled 2026-08-05,
  and `52b64ba` moved selection to a settled day, after which the 20:42 run
  selected CCS, AI, NWPX, KBDC, PHAT and BXC. Every recorded figure comes from the
  second. The Measured column dates the measurement but never names its run, and a
  reader holding three same-day transcripts carrying two different samples has to
  work that out.

  The committed transcripts do not account for the run date's API consumption. The
  provider's own counter reads 110 at the scaffold's open and 220 at its close,
  1,883 at the 20:24 open and 3,733 at its close, and 3,813 at the 20:42 open.
  That leaves 1,663 weighted calls between the scaffold and the 20:24 run and 80
  more between the 20:24 and 20:42 runs, unaccounted for by any committed
  transcript. 1,663 is the scale of a full probe run, and every version from P.2
  onward wrote a transcript, so the most likely reading is a run whose transcript
  was lost with `bin/` before H.1 moved the output directory. Provider-side
  counter lag and non-probe use of the same token would also move it, and neither
  can be ruled out from the repository. Stated as an inference, not a measurement.
  It bears on H.1's claim to have committed the evidence: what was committed is
  the evidence that still existed.

  `Program.cs` lines 665 to 667 assert that the legacy endpoint *is documented as
  obsolete and its data lags by months*. Neither clause has committed evidence.
  The measurement behind it, NVDA over two years with a newest `transactionDate`
  of 2026-03-25, exists only in `52b64ba`'s commit message, and no committed
  version of the probe prints `transactionDate` at all. Confirmed by scanning all
  87 blobs in the object database: the string appears in fourteen `PROGRESS.md`
  versions, which are the struck text discussing it, and in no version of
  `Program.cs`. The row itself was rebased by H.2 onto the endpoint disagreement,
  which does trace; the code comment was not.

**3 Authorship boundaries.** Across the nine build commits the files written are
`Directory.Build.props`, `Directory.Packages.props`, `StockResearcherLab.slnx`,
`docs/PROGRESS.md`, `prompts/spent/phase-P-data-probe.md`,
`tools/probe/Program.cs` and `tools/probe/probe.csproj`. None of the six
forbidden documents, and `.gitignore` untouched as P.1 required. Clean.

`prompts/spent/` was written by three commits in the whole repository.
`b1a0095` creates the file. `189e3d9` is header only, `ISSUED` to `SPENT` with
`Produced` filled, which `prompts/README.md` authorises where it names the header
fields and the three status values. `2901ab2` changed the header and the body:
a `Corrected:` line added, and four body changes, "section 1 and section 5" to
"Sections 7 and 10", "per CLAUDE.md section 1" to "section 7", a new
commit-per-checkpoint paragraph and a new AFTER block. It landed at 15:00, four
minutes before P.1, while the header read `ISSUED`. `CLAUDE.md` §14 and
`prompts/README.md` authorise amending an unrun prompt when a decision changes
it, and this edit is not decision-driven: it asserts that the archive did not
match the text actually issued. No corpus rule covers that case, and the only
evidence that the archive was stale is the build session's own account of it.
Narrower than it first reads, since the rule that bites is *never edited once
run* and the prompt had not been run. Disclosed in the header and in the
reconciliation block, which is the right treatment of an edit no rule covers.

**4 Secrets.** Method: enumerated every object in the object database with
`git cat-file --batch-all-objects --batch-check`, 87 blobs, 43 commits, 88 trees,
which reaches unreachable objects as well as reachable ones. `git rev-list --all`
returns 42 commits, so one is unreachable: `c3633af`, a rewritten form of the
baseline, inspected and clean. `git fsck --full` reports one dangling blob, a
`.claude` permissions file. Every blob was piped through a scan for
`api_token=` followed by anything, `sk-` keys, the provider's hex-dot-digits
shape, runs of 28 or more alphanumeric characters, and quoted values against
`ApiToken`, `ApiKey`, `Password` and `token`. The only distinct hits across all
87 are `api_token={Uri.EscapeDataString(token)}` in the source, `api_token=***`
in the transcripts and the header comment, and the MSBuild property
`ManagePackageVersionsCentrally`. No literal anywhere. Every `api_token=`
occurrence in the four tracked transcripts resolves to `***`, 15 of them, and the
H.4 transcript prints no URLs at all. `git status --ignored` shows both
`appsettings.Secrets.json` and `tools/probe/appsettings.Secrets.json` as ignored
rather than untracked, and `git check-ignore -v` attributes both to
`.gitignore:5`. The only tracked secrets-shaped path is
`appsettings.Secrets.example.json`, whose every value is empty, which is what
D-55 permits. `ff862bc` is additive, two lines for `.claude/`, touching no
secrets rule; H.1's removal of `**/probe-output/` likewise leaves the secrets
block intact.

**5 Reconciliation.** H.5 compares the prompt against `BUILD_PLAN.md`'s phase P
detail, which is what step 3 asks for, separates the prompt-against-plan question
from the build-against-both question, and classifies every item in one of the
three prescribed forms. Each of the eleven was checked against both texts and
each is classified correctly, including the two hardest: the eight-quarter bound
against the plan's *per quarter* is *less*, and the `DECISIONS.md` conflict is
*different* rather than a defect in either document. Two divergences are not
recorded.

  The build-against-both list drops two items the superseded list carried, and
  both underpin authored decisions. The 90-day news density is in neither the
  plan, which asks only for the earliest article returned, nor the prompt, which
  asks for the earliest date, the total and the per-year counts; D-60's threshold
  of 12 articles rests on it. `SharesShortPriorMonth` is likewise in neither, the
  prompt asking only whether short interest is populated and how many
  observations exist over 180 days; D-58's *a one-month change is computable*
  rests on it. A redone reconciliation that is narrower than the one it supersedes
  loses exactly the items that later became decisions.

  The plan asks the probe to *confirm the bulk end-of-day endpoint returns a full
  US day and report the row count*. The prompt asks only to *print the row count
  returned by the bulk end-of-day endpoint for one recent US trading day*, which
  drops the confirmation clause. That is *less*, and it is the clause that would
  have caught the accretion problem by construction. The build caught it anyway by
  reading five days, which is why this reads as a near miss rather than a failure,
  and it is not recorded on either side.

**6 Measured against inferred.** No figure in the Measured column is inferred;
check 2 covers the three items where the record is thinner than it reads. On the
second half of the question, the rows now separate provider limitations from
probe limitations well: the control's five-year news total is marked as a floor
because the 20-page cap bound, the 90-day figure is marked as the `limit=1000`
request cap on a call that does not page, and the short interest row names the
call it tried, `historical=1` with from and to returning a nine-member object, so
a reader can judge for themselves whose limitation it is. Three residual items.
The sentiment answer states that *rows appear only on days that carry news*,
which the probe did not measure; it measured that the earliest row equals the
earliest article on all seven and that row counts sit below article counts
throughout, which is consistent with the claim and does not establish it. The
claim sits in the Answer column where judgement belongs, so this is a
qualification and not a defect. The form4 scan stops paging at the first
transaction older than the cutoff and caps at 400 filings, which is correct only
if the provider returns filings newest first; the code assumes that ordering in a
comment and never tests it, and on these names it cannot bite because no name has
100 filings inside 90 days. And the legacy endpoint's *unusable* is a fair
reading of a zero return across seven names, but *stale* remains an inference
from the disagreement rather than a measurement of it, per check 2.

**7 Consistency with superseded decisions.** D-62 supersedes D-57 and the
documents were carried across correctly: `CONFIG_REFERENCE.md` strikes
`fundamentals.filing_date_substitution_days` and adds
`fundamentals.min_clean_gaps_for_substitution`, `SCHEMA.md` cites D-62 for
`filing_date_effective` and the per-ticker rule, and `RUNBOOK.md` alerts on a
ticker's widest clean gap. Two live references survive in code. `Program.cs:872`
tells a reader the eight-quarter bound *left D-57's 65 day substitution resting on
56 quarters*, and `Program.cs:919` to `921` classify a gap as `GAP EXCEEDS 65`
against a comment naming 65 as D-57's substitution constant. Both are defects
rather than records, because they are executable and a re-run would measure
against a constant no decision now sets. The same strings inside
`probe-filing-dates-20260806-133038.txt` are the opposite case: that run happened
while D-57 was live, and the strings are an accurate record of what it measured
against. The transcript should not be touched. `PROGRESS.md:344` states that
*D-57's detection has to run over the whole backfill* as a live obligation, four
lines above the sentence naming D-62 as its replacement; accurate as the H.4
pass's own reasoning, and it reads as current on its own.

**8 Cross-references.** Pattern, the sweep adopted at H.6 and run over the whole
tree rather than over `docs/`:

```
grep -rnoiE "(§[[:space:]]*[0-9]+([.][0-9]+)?|sections?[[:space:]]+[0-9]+([.][0-9]+)?)" \
  --include=*.md --include=*.html --include=*.cs .
```

74 hits across 12 files. Every hit was resolved by reading the target rather than
by matching a filename, and `ARCHITECTURE.html` resolves by its
`<span class="secno">` numbers. The three defects H.6 corrected all resolve now.
One reference does not resolve, and it is not one of the three.

  `CLAUDE.md:305` closes the set-based rule with *Opening a connection inside a
  loop over tickers usually means the partition key is wrong. See section 3.* §3
  is Workflow and says nothing about partition keys. The rule is in §5, The stage
  pattern, under *Parallelism has two partition keys and they are not
  interchangeable*. Present in `b1a0095`, untouched by phase P, and inside a
  human-edited-only document. The H.6 sweep used this exact pattern and matched
  this line, so the miss is in the reading rather than in the pattern, which is
  worth knowing about a sweep whose whole argument is that it reads every hit.

Every `D-<n>` reference was checked the same way. D-1 to D-62 all resolve, and
the only apparent miss, D-250, is arithmetic in the two-pass backfill note.
`Program.cs:129` cites `CLAUDE.md` §6 for null meaning unknown and `Program.cs:867`
cites `ARCHITECTURE.html` §14 for the five-week filing gap; both resolve.

**Also, outside the eight checks.** The phase status row above carries HEAD
`7a0e8b1`, which is H.7, while its own note describes pass J and HEAD is
`5afcd52`. The column was corrected once already for the same staleness at
`88a8f6e`. Not corrected here.

---

## Corrective pass H, 2026-08-06

Follows the phase P conformance finding above. Phase P is not signed off by this
pass and the conformance pass re-runs after it, in a session with no involvement
in either. Evidence is `tools/probe/probe-output/`, tracked from H.1 onward.

### Filing dates re-measured over every quarter returned [H.4]

The one measurement this pass was authorised to make. `Quarters()` dropped its
eight-quarter bound and the measurement re-ran over the seven names in the
recorded sample plus RJET.US, which is not among them and which the disputed
clause exists to settle. 16 calls. Transcript
`probe-output/probe-filing-dates-20260806-133038.txt`.

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

**The RJET claim is confirmed exactly.** 73 periods, 35 equal, 11 of the newest
12, 0 nulls. It was unevidenced rather than wrong, and those are different
defects. Only the wider read could tell them apart, which is the argument for
H.1 stated as a result rather than as a principle.

**Equality is not one bad name.** It appears on all eight, from 1 of 90 on BXC
to 35 of 73 on RJET. It concentrates in older history: six of the eight show 0
of the newest 12. So a rate measured on recent quarters understates what a
five-year backfill meets, and D-57's detection has to run over the whole
backfill rather than over the live window.

Three states the eight-quarter window hid. All are reported and none is acted
on, per H.4. **All three are answered by D-62**, which supersedes D-57 in
corrective pass J: the substitution becomes each ticker's own widest clean gap
observed to date rather than a universal 65, and unknown widens to cover null
and negative alongside equality.

**1 The 65 day substitution is too narrow.** D-57 sets it from a range of 19 to
65 across 56 clean quarters. Over 454 clean quarters the range is -16 to 210 and
38 gaps exceed 65, which is 8.4 percent. The widest is NWPX.US 2011-09-30 filed
2012-04-27, 210 days. NWPX alone has 23 above 65. A substitution of 65 would
read 38 of these quarters before they were public, which is the failure
invariant 12 exists to prevent, arriving through the rule meant to prevent it.
Widening it is an authored amendment and is not this pass's to make.

**2 Filing dates earlier than their own period end exist.** NVDA.US 2006-07-31
filed 2006-07-30 and 2002-01-31 filed 2002-01-27, RJET.US 2007-03-31 filed
2007-03-15. D-57 treats equality as unknown and says nothing about a negative
gap, so these pass every check as ordinary rows and are read up to 16 days
before the quarter closed. Three in 454 is rare and it is the same class of
defect as the one the decision exists for.

**3 Nulls exist.** 5 on NWPX.US and 2 on BXC.US. The recorded finding of never
null was true of the newest eight quarters and is false over the full history.
Null is the state the design already handles correctly, so this narrows the
recorded answer rather than opening anything new.

Also, the income statement agreed with the balance sheet on seven of the eight
names and disagreed with it on NVDA.US in 2 of 109 periods. The recorded finding
of agreement on all 7 held only within the eight-quarter window.

### Reconciliation redone against plan detail [H.5]

Step 3 compares the spent prompt against `BUILD_PLAN.md`'s phase P detail. The
record above compares it against what the design needs, which is a different
question, and it also folded three separate comparisons into one list. They are
separated here.

**The prompt against the plan.** Every divergence, classified in the plan's own
three forms.

  filing dates bounded to eight quarters   less   the plan says "per quarter"; the prompt says "the last eight quarters"
  sentiment bounded to 180 days           less   the plan puts no window on it; the build ignored the bound and read five years
  news across all tickers, not one name   more   the plan says "request news for a small cap"
  news totals and per-year counts         more   the plan asks for the earliest article returned and nothing else
  sentiment rows split from non-zero rows more   the plan asks for days returned and counts per day; the split is what made "exists but empty" testable
  distinct insiders, 180d short interest  more   the plan asks for transaction counts and whether short interest is populated
  git status --ignored and a staged grep  more   the plan says only "check the gitignore before the first commit"
  P.5 byte-identical rest of the file     more   the plan's checkpoint says "editing the existing table in place"
  the provider's OpenAPI spec named       more   not in the plan at all
  DECISIONS.md entries                    different  the plan's definition of done requires an entry for anything that came back badly; the prompt says "Do not open DECISIONS.md" and report instead
  six names, not five or six              different  the plan's narrative says five or six and its own checkpoint table says six, so the prompt matches the checkpoint and diverges from the prose

The eight-quarter bound is the one that cost something. It is the same defect as
the RJET figure seen from the other side: the bound is why the tool could not
produce the number recorded against it, and H.4 shows the same bound hid the gap
distribution D-57's constant rests on. A prompt narrowing a plan's "per quarter"
to eight looks like a detail and reached a decision.

The DECISIONS.md divergence is the interesting one, which is what the plan says
the *different* class usually is. Both instructions are right and one session
cannot execute both: the prompt is right on authorship, and the plan is right
that the phase is not done without the entries. What the plan is missing is a
sign-off step that authors them, and the C and E passes are that step arriving
without having been planned.

**The build against both.** Recorded separately because the list above folded
these in as though the prompt had diverged. The plan and the prompt agree on all
four; it is the build that went further.

  bulk EOD over five days              plan and prompt both say one day
  institutional ownership measured     in neither
  D-4 price and volume floors applied  in neither; both ask only for the cap band
  account plan and quota printed       in neither

**Gaps in the plan itself**, where the plan and the prompt agree and are both
wrong. Neither required the probe's printed output to be committed, and
`.gitignore` excluded it, so a phase whose entire deliverable is measurement
recorded its numbers with the evidence outside the repository. H.1 fixes the
mechanism. The plan clause that would have caught it does not exist.

### The subscription changed mid-phase [H.7]

The account was upgraded between the scaffold run and the first full run, and
nothing recorded it. Both states are in committed transcripts.

**2026-08-05 19:10:03 UTC**, `probe-20260805-191003.txt`, the P.2 scaffold run
against AAPL.US. Five endpoints returned HTTP 403: `insider-transactions`,
`sec-filings/{t}/form4`, `fundamentals` filtered to `Technicals`, `fundamentals`
filtered to `Financials::Balance_Sheet::quarterly`, and `screener`. Four
returned data: `user`, `news`, `sentiments`, and `eod-bulk-last-day/US` at
44,665 rows for 2026-08-04.

**2026-08-05 20:24:54 UTC**, `probe-20260805-202454.txt`, and again at 20:42:26.
No 403 anywhere. Fundamentals, form4 and the short interest fields all returned
data, and every P.4 measurement in the record comes from this state.

Two things follow that a reader needs.

The `user` endpoint does not show the change. It reported `subscriptionType`
monthly, `subscriptionMode` paid, `dailyRateLimit` 100,000 and `extraLimit` 500
on both sides, identically. Only `apiRequests` moved, 110 then 1,883, which is
consumption rather than entitlement. So nothing in the payload dates or names
the tier, and neither state can be reproduced from the account endpoint. If
phase 1 needs to know what a given subscription reaches, the answer is a probe
of each endpoint, not a field.

The screener conclusion is struck [J.3]. ~~The screener is not available on this
plan.~~ It was called once, at 19:10, returned 403, and the conclusion was drawn
from that single pre-upgrade call. Nothing retested it afterwards, because
selection moved to the bulk endpoint and no later run calls it.

It is struck rather than re-tested. Selection uses the bulk endpoint by design,
so whether the screener is reachable now changes nothing that is built on it, and
an unmeasured conclusion left standing as a measured one is worse than an open
question. Its state since the upgrade is unmeasured, and no call was made to find
out.

---

## Corpus consistency passes

Run over the documents themselves rather than over code. Records what was found so a
later reader can tell a settled figure from one that drifted and was corrected.

**The sweep pattern**, adopted at H.6 in place of any filename-anchored one:

```
grep -rnoiE "(§[[:space:]]*[0-9]+([.][0-9]+)?|sections?[[:space:]]+[0-9]+([.][0-9]+)?)" \
  --include=*.md --include=*.html --include=*.cs .
```

It anchors on the section token alone and requires nothing before it, so backticks,
brackets, markdown link syntax and abbreviations such as "ARCH section 14" all match,
and the target document is established by reading each hit rather than by matching a
filename first. That is the difference that matters: the 2026-08-05 sweep used a
filename-anchored pattern, found six defects all inside `ARCHITECTURE.html`, and
silently missed `prompts/README.md`, which sits outside `docs/` and carries one of
the three below. A pattern that starts from the filename can only find files it
already thought to look at.

Run it over the whole tree, not over `docs/`. `CLAUDE.md` and `README.md` are at the
root, `prompts/` is its own directory, and `tools/` carries section references in
code comments. `ARCHITECTURE.html` numbers its sections in `<span class="secno">`,
so references into it resolve by reading rather than by counting headings.

### 2026-08-06, corrective pass H, cross-references

Three references did not resolve. All three were present in the baseline commit
`b1a0095`, none was touched by phase P, and all three are corrected in place [H.6].

| # | Defect | Correction |
|---|---|---|
| 1 | `BUILD_PLAN.md` cited `CLAUDE.md` §8, Configuration, for the rule against tuning screens on forward returns | Corrected to §11, Decisions and evidence, which is where the rule is and which `VALIDITY.md` already cited for it |
| 2 | `prompts/README.md` cited `CLAUDE.md` §13, Documents, for the reconciliation question quoted directly beneath it | Corrected to §14, Prompts. The spent phase P prompt already cited §14 |
| 3 | `ARCHITECTURE.html` sent a reader to section 14 for the presentation layer | Corrected to section 15. Section 14 is Stack and runtime |

Defect 2 is the one that mattered, because it is the rule about not editing spent
prompts pointing at the wrong section of the file that states it, in the document
whose whole job is to say which prompts may be edited.

Every `D-<n>` reference was checked the same way at the same time. D-1 to D-61 all
resolve. The only apparent miss, D-250, is arithmetic in the two-pass backfill note.

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
