# Archive: the five-step process record, 2026-08

A closed record of the sign-off procedure retired at D-67, kept whole because it
is the evidence of what that procedure found and cost. It is never edited.

Moved here from `PROGRESS.md` at O.5, unedited. Section numbering, line
references and phase status inside it are as they stood when each block was
written and are not brought up to date.

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

### Phase P, signed off 2026-08-06, HEAD `3099e66`

~~Phase P, not signed off, built on branch `phase-p` at 8a48990. Steps 2 to 5 of
the sign-off procedure have not happened.~~ [superseded by this block, K.10]

`3099e66` is the HEAD this block was written against, not the merge that carries
it. Everything below the five steps is the build session's own account, written
before steps 2 to 5 happened and kept as written, because the divergences are
evidence about what was asked and the session that produced them is the one that
knows why.

```
Definition of done      every line run, results below
Conformance pass        two passes, 2026-08-06, findings below. Attested, not evidenced
Reconciliation          prompt against plan detail, redone at H.5, patched at K.6
Next phase authored     checkpoints 1.1 .. 1.10 added to BUILD_PLAN at K.9
Corpus version          bumped to 0.2.0, CHANGELOG entry added
```

Definition of done

  Both plan lines, the plan's secrets clause, and the prompt's line per
  checkpoint. Met means an observable result exists and the repository records
  it. Asserted means the line is true and nothing records it.

  all four questions have numeric answers in PROGRESS.md
      met. Six rows filled at `8a48990`, six lines changed and nothing else in
      the file. Every figure traced to the call that produced it at the second
      conformance pass.
  any that came back badly has an entry in DECISIONS.md
      met at HEAD by D-57 to D-62, and not met when the phase ended. The prompt
      forbade opening `DECISIONS.md`, so the C, E and J passes authored every
      entry afterwards. The line is closed and the phase did not close it.
  no API key enters the repository
      met. 87 blobs in the object database scanned, reaching the one unreachable
      commit and the dangling blob. No literal anywhere, both secrets files
      ignored rather than untracked.
  P.1 solution builds
      asserted by the phase, evidenced at the second conformance pass. `dotnet
      build StockResearcherLab.slnx` on .NET 10.0.301, 0 warnings, 0 errors.
      Nothing in the repository records the phase itself having run it.
  P.1 git status --ignored shows the secrets file ignored, not untracked
      asserted. Re-established independently at the second conformance pass,
      `git check-ignore -v` attributing both paths to `.gitignore:5`.
  P.1 a grep of everything staged finds no token-shaped string
      asserted. Re-established independently over every blob, not only the
      staged set.
  P.2 runs end to end on one ticker, printing every measurement including failures
      met. `probe-20260805-191003.txt`, nine endpoints, five 403s printed with
      status code and body.
  P.3 the printed set shows six names in band across four or more sectors
      met. `probe-20260805-204226.txt`, `in band: 6   distinct sectors: 6`.
  P.4 all four measurements produce numbers for all seven tickers
      met. Same transcript, plus the bulk row count over five days.
  P.5 the table is filled and the rest of PROGRESS.md is byte-identical
      met. `8a48990` changed six lines in one file and nothing else.

  Three of the ten are asserted, all in P.1, and all three are true. The phase
  recorded none of them, which is the distinction this line exists to draw.

Conformance finding

  Two passes ran, both on 2026-08-06.

  The first, at HEAD `2802e75`, failed the phase. Four figures inside the
  answers traced to no probe call, and the evidence behind every figure was
  excluded from the repository by `.gitignore`, so nothing recorded here was
  checkable without the build machine. Corrective pass H answered it and
  corrective pass J carried its findings into authored amendments, D-57
  superseded by D-62.

  The second, at HEAD `5afcd52`, found every figure in the Measured column
  tracing to a committed call or transcript. It left six divergences, every one
  corrected in pass K and none by the pass that found them: three P.1 lines
  asserted rather than evidenced, an evidence set that does not account for the
  run date's API consumption, two divergences dropped by the H.5 rewrite, a code
  comment asserting a measurement that exists only in a commit message, two
  executable references to a superseded decision, and one cross-reference in
  `CLAUDE.md` that had never resolved since the baseline commit.

  Both findings are under Conformance passes below, with their methods.

  **Step 2 is attested rather than evidenced.** That a checking session had no
  involvement in the build or in any correction to it is an operator
  attestation. Git carries no session identity and no repository can hold that
  fact. Attested for both passes: the first had no involvement in the build or
  in the C, E or G passes, the second none in any of those nor in H, J or the
  first pass.

Reconciliation: spent prompt against plan detail

  Redone at H.5 against this plan's phase P detail, which is what step 3 asks
  for, and patched at K.6 with three divergences it had dropped. Twelve items
  against the plan and six recording the build going past both documents, all
  in the H.5 block below rather than repeated here.

  The one that cost something: the prompt narrowed the plan's *per quarter* to
  the newest eight, which is *less*, and that bound is why the tool could not
  produce the figure recorded against it and why D-57's constant rested on 56
  quarters instead of 454. The one that was structural: the plan's definition of
  done requires `DECISIONS.md` entries and the prompt forbids opening that file,
  which is *different*, and both are right. What the plan was missing is a
  sign-off step that authors them.

Measured figures moved from estimate

  None. Nothing in the Measured figures table above is answerable from a probe
  over seven names, and the D-4 funnel's 977 is the small bucket on one day
  rather than a universe size. The figures phase P did measure are provider
  behaviour and live in the Probe findings table, not that one.

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

Limits of the evidence set [K.5]

  Three things the second conformance pass surfaced by tracing every figure to the
  call that produced it. All three are limits on what the recorded numbers cover
  rather than faults in them, and nothing here moves into or out of the Measured
  column.

  **Which run each figure came from.** The phase measured two different samples on
  2026-08-05 and the table names neither. The 20:24 run drew CCS, ALNT, IMTX, RJET,
  MFIC and OOMA from 2026-08-05 itself, which was part-settled at 3,544 rows.
  `52b64ba` then moved selection to the most recent day within 10 percent of the
  largest count seen, and the 20:42 run drew CCS, AI, NWPX, KBDC, PHAT and BXC from
  2026-08-03 at 50,029 rows.

  Every figure in all six rows comes from `probe-20260805-204226.txt`, the 20:42 run,
  with three exceptions that the cells already label: the three 08-04 bulk readings
  are one each from the 19:10, 20:24 and 20:42 transcripts; the 08-05 pair of 3,544
  and 9,072 is one each from the 20:24 and 20:42 transcripts; and the RJET and
  full-history filing date figures come from
  `probe-filing-dates-20260806-133038.txt` at H.4. The 20:24 run's own measurements
  over its own six names are recorded nowhere. They were displaced by the
  settled-day selection rather than corrected, and that run is why RJET was in the
  probe's reach at all.

  **The committed transcripts do not account for the run date's API consumption.**
  The provider's weighted call counter reads 110 at the scaffold's open and 220 at
  its close, 1,883 at the 20:24 open and 3,733 at its close, and 3,813 at the 20:42
  open. That leaves roughly 1,663 weighted calls between the scaffold and the 20:24
  run, and 80 more between the 20:24 and 20:42 runs, with no committed transcript
  accounting for either.

  Stated as an inference and not a measurement. 1,663 is the scale of one full probe
  run, and every version from P.2 onward wrote a transcript, so the most likely
  reading is a run whose output was lost with `bin/` before H.1 moved the directory
  out of it. Provider-side counter lag and non-probe use of the same token would
  also move the counter and neither can be excluded from the repository.

  This bounds what H.1 achieved. What it committed is the evidence that survived,
  not everything the phase produced, and the mechanism it fixed is the reason the
  difference exists. Nothing recorded in the Probe findings table depends on the
  missing runs: every figure there traces to a transcript that is present.

  **Three readings the measurement supports without establishing.** The sentiment
  answer states that rows appear only on days that carry news. What was measured is
  that the earliest row equals the earliest article on all seven names and that row
  counts sit below article counts throughout, which is consistent with that reading
  and does not establish it. The form4 scan stops paging at the first transaction
  older than the cutoff and caps at 400 filings, which is correct only if the
  provider returns filings newest first; the code assumes that ordering and does not
  test it, and on these names it cannot bite because none carries 100 filings inside
  90 days. And the insider row's `unusable` is a fair reading of a zero return
  across seven names, but the word stale remains an inference from the disagreement
  between the two endpoints rather than a measurement of either.

### Phase 0, not signed off, built on branch `phase-0-rails`

Written by the build session. Steps 2 to 5 of the sign-off procedure have not
happened: no conformance pass has run, the phase is not signed off, and nothing
below is corrected here.

Definition of done, line by line [P0.A]

  Evidenced means an observable result the repository records, and the row names
  where it sits. Asserted means the line is true and nothing here records it, and
  the row says what would evidence it. Several lines were proved by output pasted
  into commit bodies during the build, and a commit body is an account of a run
  rather than a record of one: check 1 reads those as asserted and so does this.

  **Naming a test evidences that the assertion is encoded, not that it currently
  passes.** Nothing in this repository has ever run the suite. The CI workflow at
  `.github/workflows/ci.yml` would cover the build, migrate, idempotence and test
  lines, and **it has never run**: `gh run list` returns nothing for the whole
  repository, `gh pr checks 1` reports no checks, and the PR head carries zero
  check-runs. Every line whose evidence would be a CI run is therefore asserted,
  and the run id column below is empty because there is no run id.

| # | Line | State | Where the evidence sits, or what would evidence it |
|---|---|---|---|
| 0.1 | `dotnet build` green from a clean clone | asserted | A CI run. Proved in the 0.1 commit body only. No run exists |
| 0.1 | Every project in `CLAUDE.md` §4 exists and is in the solution | evidenced | `StockResearcherLab.slnx` and the seven `.csproj` files, all readable in the tree. No test asserts it, so a project could be deleted from the solution without anything going red |
| 0.2 | `migrate.ps1` runs clean against an empty database | asserted | A CI run. The workflow's "Migrate, from an empty server" step. Proved by hand in the 0.2 commit body |
| 0.2 | Idempotent on a second run | asserted | A CI run. The workflow greps for `nothing to apply` and fails otherwise. Proved by hand in the 0.2 commit body |
| 0.2 | A test asserts SCHEMA.md and the database agree, both directions | evidenced | `SchemaParityTests.EveryTableInSchemaDocumentExistsInTheDatabase` and `.EveryTableInTheDatabaseIsDeclaredInSchemaDocument`, plus `.SchemaDocumentDeclaresTheTablesItIsExpectedTo` guarding the parser against passing over an empty set |
| 0.3 | An undeclared read fails at runtime with a named error | evidenced | `StageDataGuardTests.AnUndeclaredReadFailsBeforeTheDatabaseIsTouched`, and six more in `DeclaredAccessTests` |
| 0.4 | The conformance test passes over the real registry | evidenced | `WriteOwnershipConformanceTests.NoTwoComponentsClaimTheSameTableAndOperation`, built from `PipelineComposition.BuildRegistry`, which is the real registry rather than a fixture |
| 0.4 | A deliberately conflicting stage proves the test fails | evidenced | `WriteOwnershipConformanceTests.TheConformanceTestFailsOnADeliberatelyConflictingRegistry`, a permanent fixture registered in `FIXTURES.md`. The stronger proof, a conflicting component added to the real composition, is in the 0.4 commit body only and is asserted |
| 0.5 | A test asserts the Api does not reference Pipeline | evidenced | `ApiIsolationTests.ApiDoesNotReferencePipeline`, `.ApiDoesNotReferenceWorkerEither`, `.TheCompiledApiCarriesNoPipelineDependency` |
| 0.5 | The test fails if the reference is added, proved by adding it | **asserted** | Nothing. Unlike 0.4 there is no permanent fixture for the failure path: the proof exists only in the 0.5 commit body and nothing re-runs it. A fixture in the shape of 0.4's would evidence it |
| 0.6 | A run appears in the viewer with durations and row counts | **asserted** | Nothing. No test exercises `/api/runs` or renders the page. Proved by curl output in the 0.6 commit body. An endpoint test, or a render assertion, would evidence it |
| 0.7 | The stage runs and writes its `run_log` row | evidenced | `StageRunnerTests.TheNoOpStageRunsAndLogsOk`, with the failure path in `.AStageThatReachesOutsideItsDeclaredSetFailsTheRunAndSaysSo` |
| 0.7 | It runs from the command line | asserted | Nothing. The runner is tested in process; no test invokes the Worker binary. Commit body only |
| 0.7 | It is visible in the viewer | **asserted** | Nothing. Same gap as the 0.6 row |
| 0.7 | It passes the 0.4 conformance test | evidenced | The 0.4 tests read the same registry the stage is registered in |
| 0.8 | Corpus version matches in PROGRESS and newest CHANGELOG, README names none | evidenced | Committed artefacts, directly readable: this file's version line, `CHANGELOG.md`'s `## 0.3.0`, and no version string anywhere in `README.md`. No test asserts it, so it can drift silently |
| Done when | A no-op stage runs, logs, and appears in the viewer | part evidenced | Runs and logs: `StageRunnerTests.TheNoOpStageRunsAndLogsOk`. Appears in the viewer: asserted, as above |
| Done when | The write-ownership test reads the registry and passes | evidenced as encoded | `WriteOwnershipConformanceTests`. That it passes is asserted, since no run is recorded |
| Done when | Migrations run clean from empty | asserted | A CI run |

  Six lines are asserted outright and three more are evidenced only as encoded
  assertions whose results nothing records. The two that would cost real work to
  close are 0.5's failure path, which wants a fixture, and the viewer lines,
  which want a test that exercises the endpoint. Both are gaps for the correction
  pass rather than work for now, and no code was changed to make any line pass.

Reconciliation: spent prompt against plan detail [P0.B]

  `prompts/spent/phase-0-rails.md` against `BUILD_PLAN.md`'s phase 0 detail.
  Written by this session because step 3 names no owner, it is not the
  conformance pass's, and the facts below live in the session that has them.

  archive written after the work        different  `CLAUDE.md` §3 requires the prompt archived before code is written and gives the reason: one archived at the end is archived after the session has learned things. It was written at the end, so its being verbatim rests on the session's own account rather than on the ordering the rule exists to guarantee. A divergence from the procedure rather than between the two documents, and recorded as such
  tests under `src/` not `tests/`       different  Arrived mid-session, so it is in neither the plan nor the archived prompt. The plan's 0.1 cites `CLAUDE.md` §4, which said `tests/`. Closed by the author amending §4 and `README.md`
  commit subject `Phase 0 / 0.1 - ...`  different  Arrived mid-session and is in neither document. `CLAUDE.md` §3 and `BUILD_PLAN`'s convention both say `4.3 <what it did>`. **Closed in neither document**, so the repository now states one convention and practises another
  the database password                 less       Arrived mid-session. Both documents assume a reachable Postgres and neither says how to reach one, so the prompt asked for less than the plan's definition of done needs. It changed no work; it made work possible that was otherwise blocked
  `tools/probe` deleted at 0.1          more       The plan's 0.1 is solution layout, central package management, `.gitattributes` and the `guards.ps1` stub. Deleting the probe is not in it. The prompt added it
  transcripts kept, not deleted        more       In neither document. The build moved the four probe transcripts to `docs/evidence/phase-P/` rather than deleting them with the tool, because they are the evidence behind every figure in the Probe findings table and H.1 exists for that reason. Unplanned scope, and the `PROGRESS.md` and `DECISIONS.md` references to `probe-output/` paths are now stale by one directory
  CI workflow as an unnumbered chore    more       In neither document. The plan's phase 0 conventions name `guards.ps1` for the CI greps and no workflow. It arrived after 0.8, outside the checkpoint sequence
  schema detail beyond the plan         more       The plan's 0.2 says schema and migrations from `SCHEMA.md`, snapshot-first, running clean from empty. The prompt added money as native `numeric` [INVARIANT 16] and named `portfolio_selection` explicitly

  Eight entries, none resolved. The one worth reading twice is the commit subject
  format: the other mid-session changes were either closed by the author or
  changed no work, and that one changed every commit on the branch while leaving
  both documents saying something else.

---

## Conformance passes

Summary index. The detail lives in the sign-off block above.

| Phase | Date | Invariants checked | Finding |
|---|---|---|---|
| P | 2026-08-06 | None named for phase P. `CLAUDE.md` §7 and §10, which the prompt scoped in | Four questions carry numeric answers. Four figures inside those answers trace to no probe call. Reconciliation compares the prompt against the design rather than against plan detail and omits three divergences. Authorship clean, no secret in any blob. Three cross-reference defects, all older than the phase |
| P | 2026-08-06 | Same. Second pass, read at HEAD `5afcd52` | Every figure in the Measured column now traces to a committed call or transcript and the four figures the first pass found are closed. Three P.1 lines are still asserted rather than evidenced. The committed transcripts do not account for the run date's API consumption, so the evidence set is incomplete and nothing says so. The redone reconciliation drops two items the list it replaced carried, both underpinning authored decisions. One code comment asserts a provider limitation with no committed measurement, and two references still point at what a later decision or section replaced. Phase P is not signed off by this pass |
| 0 | 2026-08-06 | 10 and 11, named by `BUILD_PLAN.md`. `ARCHITECTURE.html` §14 and §16 | INVARIANT 10 is built and mechanically enforced. INVARIANT 11 is built and broken in the same phase, at `Migrator.cs:77`, with the grep named for it shipped as a stub. Eight of nineteen definition-of-done lines are asserted rather than evidenced and CI has never run, so nothing in the repository records a passing suite; all eight were established independently here. Two recorded counts do not trace to what produced them and one recorded claim is false. The reconciliation compares the right two texts and carries no build-against-both list, so nine findings reported in commit bodies are recorded nowhere a later reader will look, two of them consequential. Four live references point at `tools/probe`, deleted at 0.1. All 150 cross-references resolve, and the recorded sweep's file set no longer reaches fourteen of them. Phase 0 is not signed off by this pass |

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

### Phase 0 conformance finding, 2026-08-06, read at HEAD `d4baeaf`

A session with no involvement in the phase 0 build, in any correction to it, or in
any earlier pass in this repository, and which has made no commit here.

**Step 2 is attested rather than evidenced.** Git carries no session identity and no
repository can hold that fact, so the paragraph above is an operator attestation.
Every other step of the procedure produces something checkable from the repository
alone; this one does not.

Divergences are stated and not corrected. Nothing in the tree was changed. Where
this pass ran something it ran it against a fresh clone in a scratch directory and a
database created for the purpose, and `git status` is clean at `d4baeaf`.

**Commit sets used.** Derived from the log rather than adopted. `git rev-list
--reverse main..HEAD`, with `main` at `ed4668d`, the merge of pass N, returns
thirteen commits and correctly excludes `ed4668d`, which is the baseline rather than
a member of the set:

`1c83d5d` 0.1, `82a1689` 0.2, `1eef2bc` chore, `ec4c245` 0.3, `1542c3e` 0.4,
`7599867` chore, `0c37495` 0.5, `3b8466f` 0.6, `a27bdfb` 0.7, `408931a` 0.8,
`7f4e900` chore, `8b3d408` chore, `d4baeaf` chore.

Eight carry a checkpoint number and five are unnumbered chores. The branch is linear
and unmerged, pull request 1 open. Unlike passes H through N this set is not
recoverable from a letter prefix, because the phase opens its subjects
`Phase 0 / 0.1 -` rather than `0.1 `. Check 5 carries that as a divergence; it is
noted here because it is also what makes the boundary worth deriving.

**The architecture sections this phase implements.**

*§14, stack and runtime.* Matches. .NET 10 across every project, Postgres, an
ASP.NET Core minimal API that is read-only, and a Blazor client that talks to it and
to nothing else. §14's backfill half belongs to phase 3. The Ui is Blazor Server
rather than WebAssembly; the 0.1 commit body records that as a deviation, and it is
not one against the text, because neither §14 nor §15 names a hosting model. What
does diverge is the reference shape, under check 5.

*§16, data stores.* Matches exactly, and this was checked rather than read. §16's
store matrix has thirty rows which expand to thirty-three tables, `order / fill /
position` giving three and `cost_ledger / run_log` giving two. `0001_snapshot.sql`
creates thirty-three tables in `public`. Set difference in both directions is empty.
The migration ledger sits in a `meta` schema rather than `public`, which is what lets
the parity assertion hold without an exception carved out of it. §16's Written-by
column is not asserted by anything, and cannot be until components exist; the one
writer phase 0 registers, `RunLog` owning `run_log`, is what §16 and §3 give as C27.

**INVARIANT 10, one writer per table per operation.** Built, and enforced
mechanically rather than by review, which is the point of it. `StageRegistry` is
constructed in exactly one place, `PipelineComposition.BuildRegistry`, and the
conformance test reads that rather than a fixture. Ownership is per operation from
the first line: `TableWrite` is a table, a `WriteOperation` and the column set owned,
so `Update` on a table a component declares only `Insert` on is a different claim and
fails. `IStage` is split into `IWriteOwner` plus the runnable part so that a writer
which is not a stage still appears, which closes the hole where a writer the test
cannot see is a writer the invariant is not enforced against. The failure path is
covered permanently by a registered fixture rather than by a commit body, and the
case the amended rule exists for, allocator inserting and filler updating
`attribution`, is asserted **not** to be a conflict. All four assertions pass.

One gap, which the 0.4 commit body reports and no document carries. The test asserts
the registry against `SCHEMA.md`'s table list, which it parses, and against a
hardcoded array of the three permitted split tables, `["attribution", "proposal",
"order", "fill", "position"]`. It does not assert the registry against `SCHEMA.md`'s
own writer declarations, because those are stated in prose that varies in form. So
half the assertion reads the document and half carries a copy of it, which is the
shape `SchemaDocument` exists to avoid. With one stage registered it proves little
either way. `BUILD_PLAN.md` already carries "the write-ownership test must be
extended as each phase adds tables" as an obligation from 0 to all; this is a second
and different obligation about the same test and it is recorded nowhere.

**INVARIANT 11, no ambient clock. Built, and broken in the same phase.**

`src/StockResearcherLab.Data/Migrator.cs:77` passes `DateTime.UtcNow` as the
`applied_at` value written to `meta.schema_migration`. The invariant says nothing
reads system time outside the clock implementation and `CLAUDE.md` §6 states it as a
rule about those two calls by name. `SystemClock` is the implementation; `Migrator`
is not, takes no `IClock`, and is not a stage.

The consequence is small and the breach is exact. It is also the only one. The same
search over `src/` returns `SystemClock.cs` twice, which is the implementation, one
comment in `RunLogTests.cs` naming the rule, and nothing else. `Guid.NewGuid()`
appears three times, all in tests, all producing a unique stage name so rows can be
isolated in a shared `run_log`, and none reaches output or ordering. No `now()`,
`current_date` or `current_timestamp` default appears anywhere in
`0001_snapshot.sql`, so there is no database-side clock either. Everything the
invariant is actually for is right: `StageRunner` takes `started_at` from the
injected clock and the elapsed time from a `Stopwatch`, with a comment saying why a
stopwatch is not a second clock; the Worker resolves an absent run date from
`clock.Today`; both the date and the config version arrive in `StageContext` rather
than being resolved inside a stage.

What makes it a finding rather than a detail is what was meant to catch it.
`guards.ps1` names this exact grep, "INVARIANT 11 no ambient clock. Grep for
DateTime.Now and DateTime.UtcNow anywhere outside the IClock implementation", and
ships as a stub that prints and exits zero. The plan's 0.1 asks for a stub and the
file is candid about being one, so nothing was hidden. The result stands anyway:
phase 0 built the mechanism the invariant depends on and shipped the first violation
of it in the same phase, with the guard named for it deliberately unwired.

---

**1 Definition of done, line by line.** The plan states three Done-when clauses and
the prompt adds a Test/DoD line per checkpoint. Nineteen lines. *Recorded* means an
observable result the repository holds. *Asserted* means the line is true and
nothing here records it. *Encoded* means a test states the assertion and no run of
it is recorded, which is the state of every test-backed line in this phase, because
**CI has never run**: `gh run list` returns nothing for the repository and the GitHub
API reports zero check-runs on `d4baeaf`. Re-established at this HEAD, not taken from
the build's account.

| # | Line | Recorded | Established independently by this pass |
|---|---|---|---|
| 0.1 | `dotnet build` green from a clean clone | asserted | Cloned `phase-0-rails` at `d4baeaf` into an empty directory, no secrets file present, .NET 10.0.301: build succeeded, 0 warnings, 0 errors |
| 0.1 | Every project in `CLAUDE.md` §4 exists and is in the solution | recorded | Seven `.csproj` files, all seven listed in `StockResearcherLab.slnx`. §4 also still names `tools/probe`, which 0.1 deleted, so the line as written is no longer exactly satisfiable. Nothing asserts solution membership |
| 0.2 | `migrate.ps1` runs clean against an empty database | asserted | Created an empty database and ran the Worker's `migrate` from the clean clone with the connection string in the environment: `0001_snapshot.sql applied`, `1 migration(s) applied`, exit 0, 33 tables in `public` and 1 in `meta` |
| 0.2 | Idempotent on a second run | asserted | Second run: `already applied`, `nothing to apply, schema already current`, exit 0 |
| 0.2 | A test asserts `SCHEMA.md` and the database agree, both directions | encoded | `SchemaParityTests`, three tests, all pass. The third guards the parser against both directions passing over an empty set |
| 0.3 | An undeclared read fails at runtime with a named error | encoded | `StageDataGuardTests` and `DeclaredAccessTests`, all pass. The guard sits in `StageData`, so it is on the path a stage actually takes rather than on the rule in isolation |
| 0.4 | The conformance test passes over the real registry | encoded | Passes, over `PipelineComposition.BuildRegistry` |
| 0.4 | A deliberately conflicting stage proves the test fails | encoded | Passes. A permanent fixture, registered in `FIXTURES.md`. The stronger proof, a conflict added to real composition, is commit body only |
| 0.5 | A test asserts the Api does not reference Pipeline | encoded | Three tests, all pass. The closure is walked transitively and the deps.json assertion is guarded against passing over an empty set |
| 0.5 | The test fails if the reference is added, proved by adding it | **asserted** | Added `<ProjectReference Include="..\StockResearcherLab.Pipeline\...">` to `Api.csproj` in the scratch clone, rebuilt, ran: `ApiDoesNotReferencePipeline` and `TheCompiledApiCarriesNoPipelineDependency` both fail, `ApiDoesNotReferenceWorkerEither` passes. Reproduces the commit body exactly. No fixture makes this repeatable, and see the residual below |
| 0.6 | A run appears in the viewer with durations and row counts | **asserted** | Started the Api and the Ui from the clean clone against the freshly migrated database. `GET /api/runs` returned the `NoOpStage` row as JSON; the page at `/` rendered it with `175 ms` and `0` in the duration and rows columns |
| 0.7 | The stage runs and writes its `run_log` row | encoded | `StageRunnerTests.TheNoOpStageRunsAndLogsOk` passes, and the command-line run below wrote `run_log` row 1: `ok`, `175`, `0`, null error |
| 0.7 | It runs from the command line | asserted | `run NoOpStage 2026-08-06` printed `ok, 0 row(s) written`, exit 0. `stages` listed `NoOpStage` and `RunLog` |
| 0.7 | It is visible in the viewer | **asserted** | Yes, as the 0.6 row above |
| 0.7 | It passes the 0.4 conformance test | encoded | The 0.4 tests read the registry the stage is registered in |
| 0.8 | Corpus version matches in PROGRESS and newest CHANGELOG, README names none | recorded | `Corpus version: 0.3.0`, `## 0.3.0` as the newest CHANGELOG entry, zero `x.y.z` strings in `README.md`. Nothing asserts it, so it can drift silently |
| Done when | A no-op stage runs, logs, and appears in the viewer | part | Runs and logs: encoded. Appears in the viewer: asserted. All three halves established here |
| Done when | The write-ownership test reads the registry and passes | encoded | It reads the real registry, and it passes |
| Done when | Migrations run clean from empty | asserted | As 0.2 above |

**Eight of the nineteen are asserted and every one of the eight is true.** This pass
established all eight, and that does not make any of them recorded. Nine more are
encoded assertions whose results nothing in the repository holds. Two lines are
recorded outright, both by committed artefacts rather than by a run.

The prompt's AFTER block adds a twentieth thing: *print HEAD and the branch commits,
state phase 0's Done when against what was built*. Its output is in no committed
file, and the one place it partly landed, the phase status row, carries a HEAD four
commits stale.

**2 The recorded numbers against what produced them.** Five figures, three of which
trace and two of which do not, plus one recorded claim that is false.

*Traces.* "Thirty-three tables in `public`", in the 0.2 commit body and in
`CHANGELOG.md` 0.3.0. Reproduced here on a database migrated from empty: 33 in
`public`, 1 in `meta`. The corpus version 0.3.0 agrees across `PROGRESS.md` and the
newest `CHANGELOG.md` entry. `FIXTURES.md`'s three rows each name a test that exists
and passes.

*"25 tests green", in the phase status row.* Traces to no committed run. Reproduced
here exactly, `dotnet test StockResearcherLab.slnx --no-build` at `d4baeaf`: 25
discovered, 25 passed, 0 failed. The figure is right and unrecorded, and the same
file, three hundred lines below it, says nothing in this repository has ever run the
suite. The distinction P0.A draws correctly, that a workflow dry-run locally is an
account of a run and not a record of one, is not applied to the row that carries the
number.

*P0.A's "Six lines are asserted outright".* **The table immediately above it marks
eight rows `asserted`**: 0.1 build, 0.2 migrate, 0.2 idempotence, 0.5 failure path,
0.6 viewer, 0.7 command line, 0.7 viewer, and the Done-when migrations line. Three of
the eight are bolded and five are not, which is the likeliest source of the count,
but the state column says `asserted` in all eight. This is the check-2 failure in
miniature: a count stated beside the thing that produces it and not equal to it,
inside the block whose subject is that distinction.

*P0.A's "three more are evidenced only as encoded assertions whose results nothing
records".* By the block's own opening rule and its own statement that the suite has
never been run here, every `evidenced` row resting on a named test is in that state.
Seven rows rest on a named test and one more is explicitly marked "evidenced as
encoded", which is eight, not three.

*The claim that does not hold.* P0.B and the 0.1 commit body both state that "the
`PROGRESS.md` and `DECISIONS.md` references to `probe-output/` paths are now stale by
one directory". **`DECISIONS.md` carries no such reference.** Searched
whitespace-tolerantly over the whole file for `probe[-\s]*output`, `tools\s*/\s*probe`,
`probe-2026` and `probe-filing`: no hit, and the only occurrence of the word
transcript, at line 394, carries no path. The `PROGRESS.md` half is correct, at lines
778 and 786.

The 0.6 commit body's "200, 5,637 bytes, 17 table rows" followed by "16 row(s)" in
the same block is not a repository record either way, and the two counts differ by a
header row without saying so.

**3 Authorship boundaries.** Across the thirteen commits the files written are the
build files, `StockResearcherLab.slnx`, everything under `src/`,
`.github/workflows/ci.yml`, `guards.ps1`, `migrate.ps1`, `seed.ps1`,
`.gitattributes`, the four probe transcripts moved into `docs/evidence/phase-P/`,
`docs/CHANGELOG.md`, `docs/FIXTURES.md`, `docs/PROGRESS.md`,
`prompts/spent/phase-0-rails.md`, and `CLAUDE.md` and `README.md`. `.gitignore` is
untouched: `git log main..HEAD -- .gitignore` returns nothing.

**One of the six documents was written on this branch.** `7599867` modifies
`CLAUDE.md`, dropping the `tests/` line from the §4 layout, and `README.md` line 69.
Its body states that the edits were made by the human author and committed separately
by the build session because `git add -A` had swept them into 0.4. Git holds no
evidence for that: every commit on the branch carries the same author and committer,
so the attribution rests on the commit body, exactly as this pass's own freshness
does. Recorded as attested rather than evidenced. On its face it is authored content
correctly authored, and the sequence is the one `CLAUDE.md` §13 asks for: the build
reported the divergence at `1eef2bc` and did not close it, and the author closed it at
`7599867`.

`prompts/spent/` was written once, by `8b3d408`, which **creates**
`prompts/spent/phase-0-rails.md` whole, 115 lines. That is neither a header change
nor a body change to an existing archive, and it is what the corpus does not have a
rule for. `CLAUDE.md` §3 requires the prompt archived before code is written and
gives the reason. D-63 and `prompts/README.md` authorise correcting an archive toward
the text actually issued. Neither covers writing the archive after the phase is
built, and nothing forbids it either, because the corpus assumes the ordering §3
requires. The commit body discloses the ordering, states the text is recoverable and
verbatim, and names three instructions that arrived mid-session and are deliberately
absent because they were not in the prompt as issued. That disclosure is the right
treatment and it does not restore what the ordering rule was for: the archive being
verbatim rests on the build session's own account, which is the condition §3 exists
to remove. The alternative was no archive at all, which would have blocked sign-off
step 3 and this pass's check 5 entirely. The header carries Target, Issued, Status
`SPENT` and Produced, which is the four-field shape `prompts/README.md` names, with
one of its three status values.

**4 Secrets.** Clean, and the method carries more weight than usual because the
repository is public, `masalasev-ops/StockResearcherLab`.

Method. Enumerated every object with `git cat-file --batch-all-objects
--batch-check`: 215 blobs, 85 commits, 214 trees. `git rev-list --all` returns 82
commits, so three are unreachable and the enumeration reaches them. `git fsck --full`
reports eight dangling blobs and no missing object. Every one of the 215 blobs was
piped through six patterns: `api_token=` followed by anything that is not a
placeholder, the provider's hex-dot-digits token shape, `sk-` keys, `Password=`
inside a connection string, a quoted value against `ApiToken`, `ApiKey`, `Password`,
`Token` or `Secret`, and an Authorization bearer header. **No hit for any pattern in
any blob.**

A zero is worth only as much as the pattern behind it [deferred item 9], so the six
were first run against a positive control: the four real untracked
`appsettings.Secrets.json` files in the working tree, which carry a live provider
token and a database password. The token-shape pattern fires on all four; `Password=`
and the quoted-value pattern fire on the three that carry a connection string. The
pattern set is therefore known to detect the exact secrets this repository actually
holds, and the zero across the object database is a measurement rather than a pattern
failing to match.

`git status --ignored` shows all four secrets paths as ignored rather than untracked,
and `git check-ignore -v` attributes every one to `.gitignore:5`. The only tracked
secrets-shaped path in the tree is `appsettings.Secrets.example.json`, whose every
value is empty, which is what D-55 permits. The CI workflow introduces no credential:
the service container uses `POSTGRES_HOST_AUTH_METHOD: trust` and the connection
string names a user and no password, and its "Confirm no secrets file is present"
step fails the job before anything reads configuration.

One property of the instrument, reported by the CI chore's body and recorded nowhere
else. The Api's `appsettings.Secrets.json` flows into the test output directory
through the project reference 0.5 added, so a local test run can take its connection
string from a file other than the test project's own. Confirmed:
`src/StockResearcherLab.Tests/bin/Debug/net10.0/appsettings.Secrets.json` exists, and
it, the test project's source file and the Api's are byte identical, sha256
`b078bc7b...`. The ambiguity is real and it changed nothing for this pass, because
every candidate file carries the same connection string.

**5 The reconciliation record.** P0.B compares
`prompts/spent/phase-0-rails.md` against `BUILD_PLAN.md`'s phase 0 detail, which is
the comparand step 3 asks for, and classifies all eight of its entries in the three
prescribed forms. Each of the eight was checked against both texts and each is
classified correctly. The commit subject entry is right to be singled out: the
repository states one convention in two documents and practises another in every
commit on the branch.

Five divergences are not recorded.

  *The prompt scoped the architecture read to §3 and §16, and said "Nothing else."*
  Phase 0 implements §14 for the stack, and §14 is the section naming .NET, Postgres,
  the read-only minimal API and the Blazor client. **Less**, against `CLAUDE.md` §3's
  instruction to read the sections the phase implements. It cost nothing, because the
  build produced all four anyway, and it is the mechanism by which a stack decision
  could have been missed without anyone noticing.

  *`TreatWarningsAsErrors` on.* The prompt's 0.1 requires it. The plan's 0.1 names
  central package management and says nothing about warning policy. **More**, and it
  is load-bearing: it is why a warning fails the build rather than accumulating.

  *The corpus version bump inside 0.8.* The build reported the tension in the 0.8
  commit body and bumped, which is what the checkpoint asks in its own words. The
  tension is not between the prompt and the plan. **`BUILD_PLAN.md` contradicts
  itself**: checkpoint 0.8 says "first corpus version bump" and sign-off step 5 says
  the bump happens at sign-off. The version is now 0.3.0 with steps 2 to 5 unstarted.
  Visible rather than hidden, authored, and unresolved.

  *The plan's 0.8 says to open `CHANGELOG.md`.* It was opened at corpus 0.1.0, before
  phase P. There was nothing to open. A small inaccuracy in the plan, in neither list.

  *There is no build-against-both list, and that is the structural one.* H.5
  established on phase P that the prompt-against-plan question and the
  build-against-both question are different, and that folding them together is how a
  reconciliation loses the items that later become decisions. P0.B has only the first.
  What belongs in the second is the set of findings the phase reported in commit
  bodies and nowhere else, which is nine: the transcripts kept rather than deleted,
  the Ui hosting model, the root secrets file not matching its template, the money
  column in `SCHEMA.md`, the migrator not repairing drift, two reserved table names,
  the writer-name half of the 0.4 assertion, the stale-artifact defect found and fixed
  inside the 0.5 test, and the secrets file flowing into the test output. `CLAUDE.md`
  §7 says what makes something a record is where it will be read. Two of the nine are
  consequential:

  **0.2 resolved a contradiction between an authored document and an invariant rather
  than stopping.** `SCHEMA.md`'s `indicator_daily` lists `median_dollar_volume_20d`
  among its columns and then says "Store as 32-bit floats". A median dollar volume is
  money and INVARIANT 16 does not bend for storage size. `CLAUDE.md` §3 says that when
  something contradicts an authored document you stop, report it, and do not proceed
  past that point, and specifically do not implement what you judge the document
  should have said. The build built `numeric`, reported in the commit body, and
  continued. The column is right, the schema is right, and the procedure is what was
  broken. Nothing in any document records that the contradiction exists, so the next
  reader of `SCHEMA.md` §indicator_daily meets it again from scratch.

  **0.1 gave the Ui a reference to the whole Api project.** `CLAUDE.md` §4 says the Ui
  "References Api contracts only". There is no contracts project; `RunContracts.cs`
  sits inside the Api. The build reported this and said correctly that a separate
  contracts project is an authored decision and not its own. The consequence is
  checkable and was checked: the Ui's compiled dependency closure carries
  `StockResearcherLab.Data/1.0.0` and `Npgsql/9.0.4`. 0.5's own test treats the
  transitive closure as decisive when the subject is the Api, on the stated ground
  that a direct check would pass while the guarantee was broken. Both
  `StockResearcherLab.Ui.csproj` and `Ui/Program.cs` state "no reference to Data",
  which is true directly and false transitively, and no test applies 0.5's standard to
  the Ui at all.

**6 Measured against inferred.** Nothing in the Measured figures table moved, and
correctly: phase 0 measures nothing about the system under study. Its numbers are
properties of the build.

Two figures are recorded as fact and inferred from a run nothing recorded, both under
check 2: "25 tests green" in the phase status row, and "Passed 25, Failed 0" in the CI
chore's body, which describes a local dry run in the vocabulary of the CI job it was
rehearsing. Both are true.

One claim attributes to the toolchain a limitation with no committed evidence. The
0.1 commit body states that a WebAssembly Ui cannot carry a project reference to the
Api, because the Api's `FrameworkReference` to `Microsoft.AspNetCore.App` has no
browser-wasm runtime pack, and that the build fails with `NETSDK1082`, marked "Tried
it, that is the observed error, not a prediction". The identifier is specific and the
reasoning holds, and there is no transcript. It is the same shape as the phase P
finding about `Program.cs:665`, a measurement whose only record is a commit message,
and it matters more here because it is the entire justification for the Ui's hosting
model differing from what a reader of §15 would expect.

The rows that separate an instrument limitation from a system one do it well.
`TestDatabase` refuses to skip when the database is unreachable and says why; the
`SchemaParityTests` parser guard exists so both directions cannot pass over an empty
set; the deps.json assertion checks the file exists and that a project library is
listed. Each is a case of a check that would otherwise report green while asserting
nothing, caught in advance.

One residual of exactly that class survives inside the 0.5 test, found here rather
than reported. The 0.5 commit body says the stale-artifact defect was closed by having
the test project reference the Api, "so the artifact is rebuilt by construction rather
than by someone remembering to". That holds only while the build succeeds. In the
first attempt at the failure-path proof above, the build failed for an unrelated
reason, a running Api process holding its own exe, which is the Windows detail the 0.6
commit body warned about. `dotnet test --no-build` then read the previous `deps.json`
and `TheCompiledApiCarriesNoPipelineDependency` passed with the Pipeline reference
present, while `ApiDoesNotReferencePipeline` correctly failed. After stopping the
process and rebuilding, both failed together. CI is not exposed, because its Build
step gates the Test step. A local `--no-build` run after a failed build is. The defect
is narrowed rather than closed, and the narrowing is not written down.

**7 Consistency with superseded decisions.** No executable reference to a superseded
rule, which is the half that matters most. Every `D-<n>` cited in code, migrations,
scripts and the workflow was resolved against `DECISIONS.md`: D-9, D-23, D-25, D-27,
D-29, D-36, D-38, D-40, D-43, D-44, D-46, D-48, D-51, D-55, D-56, D-58, D-61, D-62,
all `ACTIVE`. D-57 appears nowhere outside `DECISIONS.md` itself, `PROGRESS.md`'s
record of the passes that superseded it, and the phase P transcripts under
`docs/evidence/phase-P/`, which were written while it was live and are records rather
than defects. INVARIANT 10 is carried in its amended form at every one of the eleven
places it appears in code, and the registry, the guard and the test are all per
operation.

Four live references now point at something that does not exist, all of them created
by 0.1 deleting `tools/probe`.

  `.gitignore` lines 13 to 16 carry a comment reading "tools/probe/probe-output/ is
  deliberately tracked and must stay outside bin/", with H.1's reasoning attached. The
  directory is gone and the transcripts are at `docs/evidence/phase-P/`. A live
  instruction in a live file about a path that does not exist. The phase did not
  report this one, and `.gitignore` was never opened, which is consistent with
  `CLAUDE.md` §10's rule being about inspecting a diff where there was none.

  `CLAUDE.md` §4 still draws a `tools/` block containing `probe`, "phase P only,
  deleted or rewritten afterwards". It was deleted, by an instruction that cited this
  very file. This is also what makes 0.1's own definition of done, "every project
  named in `CLAUDE.md` §4 exists", not exactly satisfiable as written. Authored, not
  the build's to fix, and not reported either.

  `PROGRESS.md` line 778 states that pass H's evidence is `tools/probe/probe-output/`
  and line 786 names a transcript by that path. Both read as current pointers. The
  phase reported this class, over-broadly, per check 2.

  `README.md` line 45 describes `SCHEMA.md` as "Tables, grain, and single-writer
  ownership". INVARIANT 10 has been per operation since L.3, and phase 0 has now built
  the per-operation registry and its test, so the line is contradicted by shipped code
  rather than only by a document. It stands as deferred item 1 after pass N with "the
  next edit to `README.md`" as its trigger; `7599867` was an edit to `README.md` and
  did not take it.

Three deferred items reached their stated trigger during this phase and are open.
Item 2, `README.md`'s "Where to start" sending a reader to `BUILD_PLAN.md` phase P,
triggered by phase 0 becoming current. Item 10, the architecture stating writes three
times over, triggered "after phase 0 has proved the registry and its test work", which
this pass observes to have happened. Item 5, `CLAUDE.md` line 397 saying "a test that
no fundamental is readable before its filing date" where D-62 makes it the effective
filing date, is owed to the next authored amendment to §9 and has not had one.

**8 Cross-reference integrity.** Pattern, whitespace-tolerant per N.11, run over the
whole tree:

```
(?:§|[Ss]ections?)[\s]*[0-9]+(?:\.[0-9]+)?
```

**All 150 section references resolve**, each established by reading its target rather
than by matching a filename, with `ARCHITECTURE.html` resolving by its
`<span class="secno">` numbers. Every count in this check is as at `d4baeaf` and
therefore excludes this finding, which added 32 more to this file: re-running the
sweep after it returns 168 over the recorded set and 182 over the extended one, and a
later pass comparing against a prior run needs the `d4baeaf` figures rather than
these. Every `D-<n>` reference in the tree resolves, D-1
through D-63, and the only apparent miss, D-250, is arithmetic in the two-pass
backfill note, as three earlier passes recorded. The wrapped-reference detector, a
token at the end of one line and its number at the start of the next, returns zero.

**The recorded file set no longer covers the tree.** The sweep adopted at H.6 and
re-audited at N.11 is `--include=*.md --include=*.html --include=*.cs`. Over that set
the pattern now returns 136 hits in 27 files. Over a set extended with `.razor`,
`.ps1`, `.yml`, `.props` and `.csproj`, all five introduced or newly populated by
phase 0, it returns 150 in 38 files. **Fourteen references sit outside the recorded
sweep**: three in `Directory.Build.props`, six in the `.csproj` comments, two in
`.github/workflows/ci.yml`, and one each in `guards.ps1`, `seed.ps1` and
`Runs.razor`. All fourteen resolve, so nothing was wrong; the file set is what needs
extending before the next phase adds more code than documents.

Per deferred item 9, the count is compared against a prior run rather than read as a
pass on its own. The same pattern over the same file set at `ed4668d`, the baseline
this branch was cut from, returns 103, which is N.11's 100 plus the three references
`ca0012c` added when it recorded the deferred items. The difference reconciles
exactly: 103, less the 2 that left with `tools/probe/Program.cs`, plus 24 in
`src/**/*.cs`, 4 in the spent prompt, 2 in the new `CHANGELOG.md` entry and 5 in this
file's phase 0 block, is 136.

**Also, outside the eight checks.** Two things.

The phase status row carries HEAD `a27bdfb`, which is 0.7. It was written at
`408931a` and has not moved through four later commits, one of which added the P0.A
and P0.B blocks to the same file. This is the third occurrence of the same staleness
in this document, after `88a8f6e` and K.4 corrected it twice on phase P. Not
corrected here.

The phase 0 sign-off block opens "Steps 2 to 5 of the sign-off procedure have not
happened: no conformance pass has run". That was true when written and this pass
supersedes the second clause of it. Marking it is sign-off's job and not this pass's,
so the sentence is left as the build session wrote it.

**Phase 0 is not signed off by this pass.**

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
  bulk full-day confirmation dropped      less   the plan says "confirm the bulk end-of-day endpoint returns a full US day and report the row count"; the prompt keeps only the row count [added K.6]
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
  news 90-day density                  in neither; the plan asks for the earliest article and the prompt for earliest, total and per-year. D-60's 12-article threshold rests on it [added K.6]
  SharesShortPriorMonth captured       in neither; the prompt asks only whether short interest is populated and how many 180-day observations exist. D-58's one-month change rests on it [added K.6]

The last two were in the build session's own list and this one dropped them when
it was rewritten. Both are the reason an authored decision has a number in it, so
a reconciliation that omits them leaves D-60's threshold and D-58's surviving
input looking like they came from the plan when they came from the build going
past it. A rewrite narrower than the record it supersedes is the failure mode to
watch for here, and it is the second time on this phase that a correction lost
something the thing it corrected had.

The bulk clause is the near miss. Neither list had it, and it is the one line in
the plan that would have caught the accretion by construction rather than by the
build happening to read five days instead of one. Recorded against the prompt
because the prompt is what dropped it.

**Count.** Twelve items against the plan and six against both, eighteen in all.
K.6 called for thirteen. The difference is placement rather than coverage: the
90-day density and `SharesShortPriorMonth` are the build exceeding both documents
rather than the prompt diverging from the plan, so they belong in the second list,
and putting them in the first would classify the prompt for something it did not
do. All three items K.6 named are present.

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

**It is line-anchored, and that is a second way to miss silently** [N.11]. `grep` matches
within a line, so `[[:space:]]` never crosses a newline, and prose here is hard-wrapped.
A reference written as `section` at the end of one line and `5` at the start of the next
produces no hit and the sweep reads as a pass. The whitespace-tolerant form, run over the
same file set:

```
python -c "import re,io,glob; rx=re.compile(r'(?:§|[Ss]ections?)[\s]*[0-9]+(?:\.[0-9]+)?')"
```

Run at N.11 over every `.md`, `.html` and `.cs` file in the tree: **100 section
references, none wrapped**, so the line-anchored sweeps at H.6 and K.3 were not false
passes. The same re-audit covered the `D-<n>` references and every done-condition grep
in passes L, M and N, twelve patterns in all. Exactly one hit in the corpus breaks
across a line, `CLAUDE.md:403`, and N.10 had already found it by accident rather than by
pattern. That is the whole argument for the rule: the miss rate was one, and nothing
about the method would have told us if it had been ten.

### 2026-08-06, corrective pass K, cross-references

One reference did not resolve. Present in the baseline commit `b1a0095`, untouched
by phase P, and corrected in place [K.3].

| # | Defect | Correction |
|---|---|---|
| 1 | `CLAUDE.md` §6 closed the set-based rule with "See section 3" for the partition-key rule | Corrected to §5, The stage pattern, which is where `Parallelism has two partition keys and they are not interchangeable` is stated. §3 is Workflow |

The H.6 sweep ran this exact pattern and matched this line, so the miss was in the
reading rather than in the pattern. Worth knowing about a sweep whose whole argument
is that the target is established by reading every hit: the pattern only guarantees
the hit is seen, not that it is resolved.

The full sweep at K.3 returned 74 hits across 12 files. Every other section reference
resolves, and every `D-<n>` reference from D-1 to D-63 resolves. The only apparent
miss, D-250, is arithmetic in the two-pass backfill note.

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

---

## Corrective passes L, M and N, 2026-08-06

**L and M ran after the 0.2.0 entry was written**, which happened at K.10 as sign-off
step 5. That entry names phase P's documents and cannot name theirs, because they did
not exist yet. `CHANGELOG.md` is appended to and never rewritten, so 0.2.1 carries
them rather than 0.2.0 being corrected.

### Pass L, the architecture absorbs D-57 to D-63

Changed `ARCHITECTURE.html`, `SCHEMA.md`, `CLAUDE.md`, `BUILD_PLAN.md`,
`CONFIG_REFERENCE.md`, `DECISIONS.md` and `prompts/candidate-block.md`.

What it settled. D-61 had no component: `SCHEMA.md` already named `FlowEngine` as
`flow_daily`'s writer and no such thing existed in the architecture, so C34 was added
with the two source tables that were also missing from the store matrix. D-62's
exclusion went into the universe definition, where INVARIANT 1 requires absolute
filters to live. INVARIANT 10 was restated as one writer per table per operation,
after its exception count turned out wrong in both directions. And the rest of the
drift: a conviction integer in section 7 against D-18, calibration described as
bucketing conviction, nine three-portfolio references, a call count of 31 against a
figure and a cost table that both said 29, D-60's coverage condition that the runtime
rubric carried and the architecture did not.

The candidate block told the model a missing digest cannot happen because INVARIANT 15
prevents it, while D-60 is built on exactly that case. A runtime prompt, so a clean
deletion rather than a strike [`CLAUDE.md` §13].

### Pass M, one reversal and one limitation

Changed `SCHEMA.md`, `ARCHITECTURE.html`, `DECISIONS.md` and `CONFIG_REFERENCE.md`.

Reversed L's `security.clean_gap_count` column and the per-operation split it carried.
The count is as-of: a ticker has more clean gaps now than three years ago, so a stored
scalar read during backfill admits names a live system would have excluded, which is
the permissive direction and puts backfilled screen scores on a different population
than live ones. UniverseBuilder computes it for the date being built instead. The
split count went back to three.

Stated the limitation that survives the reversal, inside D-62 rather than as a new
decision. `filing_date_effective` is computed at ingest from the widest clean gap
known then, so a backfill reading an older row uses a window derived partly from
filing behaviour that had not happened yet. A widest gap only grows, so affected rows
become readable later than they truly would have been rather than earlier. Accepted
rather than corrected, and it does not reach the universe exclusion.

Struck D-54's portfolio letters, the last authored decision naming a portfolio by
letter.

### Pass N, eight places two documents disagreed

Changed `ARCHITECTURE.html`, `SCHEMA.md`, `BUILD_PLAN.md`, `FIXTURES.md`, `README.md`,
`CHANGELOG.md` and this file.

What it settled. `order` has one writer: RiskGate for all four portfolios, with
PortfolioRunner persisting to a new `portfolio_selection` store rather than writing
orders, which is the structural form of INVARIANT 8 and left figure 7 unchanged. The
clean-gap exclusion moved from checkpoint 1.4 to 1.5, matching the five documents
that already placed it in UniverseBuilder. Three write-column mismatches between
sections 3 and 16 resolved toward the store matrix. Phase 1's definition of done
extended from four of its ten checkpoints toward all of them. `FIXTURES.md` caught up
with the fixtures checkpoint 1.10 had been enumerating in its place.

Two things pass N found and did not correct, because each sits outside the clause that
found it. `README.md` describes `SCHEMA.md` as single-writer ownership, which
INVARIANT 10 no longer is. And `equity` appears in C18's Reads column with no `equity`
table declared anywhere, which is a read-side question phase 7 has to settle: whether
portfolio equity is a store or is derived from fills and positions at read time. Both
are recorded here so that a later reader can tell a line nobody looked at from one
that was looked at and left.

### Deferred after pass N

Pass N ran to eleven clauses rather than the eight the heading above names. N.9, N.10
and N.11 arrived as rulings on what N.6 and N.8 reported rather than as part of the
original set. The heading is left as written because it describes what was issued.

Ten items were found and not corrected. Nothing here is a clause and nothing here
changed the corpus. Each was left because it sat outside the clause that found it, and
each is recorded so a later reader can tell a line that was looked at and left from one
nobody read.

| # | What it is | Owner or trigger | Found at |
|---|---|---|---|
| 1 | `README.md:44` describes `SCHEMA.md` as "Tables, grain, and single-writer ownership". INVARIANT 10 is per operation since L.3, and three tables carry more than one writer | The next edit to `README.md`, or the next corpus consistency sweep | N.5 commit body |
| 2 | `README.md` "Where to start" sends a reader to `BUILD_PLAN.md` phase P, which is DONE as of K.10 | Phase 0 becoming current | N.5 commit body |
| 3 | `equity` is a read target with no store. ARCHITECTURE §3 lists C18 reading `proposal`, `position`, equity and `indicator_daily`, and `SCHEMA.md` declares no `equity` table. The row's own typography already separates it, the other three being in `<code>` and equity bare, which is how it survived every sweep so far, all of them write-side | Phase 7, which builds the risk layer and would declare it | The pass N record above, N.4 |
| 4 | `FIXTURES.md`'s existing filing-date entry says "asserting no read before the filing date" where D-62 makes it the effective filing date | Phase 1, when checkpoint 1.10 writes the fixture and registers it | N.8 commit body |
| 5 | `CLAUDE.md:394` carries the same wording, "a test that no fundamental is readable before its filing date" | The next authored amendment to `CLAUDE.md` §9 | N.8 commit body |
| 6 | Checkpoint 1.8's events-ingest half is not exercised by phase 1's definition of done. `flow_daily` is covered and events ingest is not | Phase 1 sign-off, step 1 | N.6 commit body |
| 7 | Checkpoint 1.1 is reachable from phase 1's definition of done only through "one night lands", which exercises the HTTP client without asserting the token auth, the explicit `fmt`, the encoded filter form or the rate limit it names | Phase 1 sign-off, step 1 | N.6 commit body |
| 8 | `CHANGELOG.md` 0.2.0 does not name `RUNBOOK.md`, `prompts/rubrics.md` or `prompts/README.md`, all three changed during phase P. They are covered only by 0.1.0's initial-corpus listing | None. The file is appended to and never rewritten, so the omission stands and 0.2.1 says so | N.4 commit body |
| 9 | A sweep whose pass condition is a non-zero count cannot validate its own pattern. Full text below | Conformance check 8, whenever it is next touched | N.11, never written down until now |
| 10 | The architecture states writes three times over. Full text below | After phase 0 has proved the registry and its test | Passes K through N, never written down until now |

**Item 9, in full.** A sweep whose pass condition is a non-zero count must state the
expected count in advance or compare against a prior run. A pattern cannot validate
itself, so a wrong pattern reports a clean number and reads as a pass. A sweep expecting
zero is self-validating and needs no baseline. Owed to conformance check 8 whenever it is
next touched.

**Item 10, in full.** The architecture's store matrix states writes twice, once in its
own Written-by column and once in the section 3 catalogue, and SCHEMA states them a third
time. Every write-column finding in passes K through N came from that duplication.
Dropping the Written-by and Read-by columns and pointing at SCHEMA would end the class.
It is an architecture change, it is human-authored, and it waits until phase 0 has proved
the registry and its test work.

Item 9 is the general form of what N.11 found in the particular. N.11's rule catches a
pattern that misses hits it should have made; item 9 catches a pattern that finds hits
that are not there, or the wrong ones, and neither is visible from the number alone. The
N.11 re-audit is the worked example: my own first audit pattern returned 51 section
references where there are 100, and the only thing that caught it was the count
disagreeing with a prior run.
