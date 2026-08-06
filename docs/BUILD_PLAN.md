# BUILD_PLAN.md

Eleven phases in dependency order. A phase is not done because the code exists. It
is done when the stated proof passes, which in every case is something that can be
run rather than something that can be argued.

Each phase names the invariants from `CLAUDE.md` §2 that its work can plausibly
break. That list is not a summary of the phase. It is what the conformance pass at
the end of the phase checks specifically.

**The conformance pass runs in a separate session from the one that built the
phase.** The session that wrote the code has already convinced itself. It reads the
architecture section the phase implements, checks the named invariants, and produces
a written finding.

Status markers: `NOT STARTED`, `IN PROGRESS`, `DONE`, `BLOCKED`.

---

## Checkpoints, commits and sign-off

**A phase is divided into numbered checkpoints and a commit maps to exactly one.**
Checkpoint `4.3` is committed as `4.3 <what it did>`. That is the whole convention, and
it exists so that a commit can be traced to the plan clause that asked for it without
anyone remembering what was happening that week.

Churn commits carry no checkpoint number and say what they are: `chore: line endings`.
A commit that spans two checkpoints means the checkpoints were drawn wrong; split it.

**Detail is authored one phase ahead, not eleven.** Phases below carry scope and a
definition of done from the outset, because those are decisions. Numbered checkpoints
are authored when the previous phase signs off, because writing them earlier means
writing against assumptions the build has not tested yet. Phase P and phase 0 carry
checkpoints now; phase 1 gets them when phase 0 signs off.

### Sign-off procedure

Run in order. A phase is not done until all five have happened.

1. **Definition of done.** Every line runs and produces an observable result. It is a
   checklist, not a description.
2. **Conformance pass**, in a session that did not build the phase. Reads the
   architecture sections implemented and the invariants named for that phase. Writes a
   finding to `PROGRESS.md`. It is not a code review. It answers one question: does
   what was built match what was authored. Its checks are below.
3. **Reconciliation.** Compare the spent prompt against this plan's detail for the
   phase. They will diverge, because a prompt is written before the work and the plan is
   what was intended. Record the divergence in `PROGRESS.md` under that phase, in one of
   three forms:
   - *the prompt asked for less than the plan* — a gap, and the phase is not done
   - *the prompt asked for more* — scope that arrived unplanned, which either becomes a
     decision or gets removed
   - *the prompt asked for something different* — the interesting case, and usually
     means the plan was wrong rather than the prompt
   **Never edit the spent prompt to close a divergence.** The record of what was asked
   is the only evidence of why the code looks the way it does.
4. **Author the next phase's checkpoints** in this document, now that what exists is
   known rather than assumed.
5. **Bump the corpus version** and add a `CHANGELOG.md` entry naming the phase and what
   changed in the documents.

**The prompt issued for each phase is archived verbatim in `prompts/spent/` as
`phase-<n>-<slug>.md` and is corrected afterwards only to match the text actually
issued [D-63].** This plan says what a phase should do; the archive says what was
actually asked of it. Where the two diverge, the divergence is the finding, and it
goes in `PROGRESS.md` rather than being tidied away in either file.

### The conformance pass, step 2 in full

Written here because it had existed only in chat, so every run waited on someone
pasting it and the checks drifted between passes.

**Who may run it.** A session that has made any commit in this repository is
disqualified, including a commit correcting a finding of an earlier pass. Checking
your own correction is checking your own build one step removed.

**This step is attested rather than evidenced.** Git carries no session identity, so
that the checking session was fresh is an operator attestation and no repository can
hold that fact. Every other step in this procedure produces something checkable from
the repository alone; this one does not, and treating the attestation as evidence is
the way the whole procedure fails quietly. The sign-off block records the attestation
as an attestation.

**It derives its own commit sets from the log** rather than adopting a range it was
handed. Every pass prefixes its commits with its letter, so the sets are recoverable
from the messages alone. A boundary taken on trust is how a pass examines the wrong
commits and reports a clean result that means nothing. `A..B` range notation excludes
`A`, which silently drops the opening commit of every set.

**Eight checks. Each produces a written finding, including the ones that pass.**

1. **Definition of done, line by line.** For each line, did it produce an observable
   result, and is that result recorded in the repository. Name every line that is
   asserted rather than evidenced. A line that is true and unrecorded is still
   asserted.
2. **The recorded numbers against what produced them.** Follow every value from the
   call, query or run that produced it to the row that records it, reading the code
   and the committed evidence rather than any summary of them. A row filled from a
   plausible number rather than from a measurement is the failure this check exists
   for, and it is the most important one here.
3. **Authorship boundaries** [`CLAUDE.md` §13]. The build session writes `PROGRESS.md`
   and never `DECISIONS.md`, `BUILD_PLAN.md`, `ARCHITECTURE.html`, `SCHEMA.md`,
   `VALIDITY.md` or `CLAUDE.md`. Later passes that applied authored decisions under
   instruction are outside the check and are not reported as violations. Also every
   commit that wrote to `prompts/spent/`, what it changed, and whether the corpus
   authorises that kind of edit, distinguishing a header change from a body change
   [D-63].
4. **Secrets** [`CLAUDE.md` §10]. No token-shaped string in any blob ever committed,
   reachable or not. Both secrets files ignored rather than untracked. State the
   method used, not only the result.
5. **The reconciliation record.** Is every divergence between the spent prompt and
   this plan's detail recorded, and is each classified as the prompt asking for less,
   for more, or for something different. A rewritten reconciliation is checked against
   the one it superseded, because the failure seen here is a correction narrower than
   the record it replaced.
6. **Measured against inferred.** Anything recorded as measured that is actually
   inferred, and anything recorded as a limitation of the provider or the system that
   is actually a limitation of the instrument [`CLAUDE.md` §7].
7. **Consistency with superseded decisions.** Anywhere the code, its comments, its
   output strings or its committed evidence still asserts what a later decision
   replaced. Say of each whether it is a defect or an accurate record of what a past
   run measured against. An executable reference to a superseded rule is a defect; a
   transcript written while that rule was live is a record and is never edited.
8. **Cross-reference integrity across the corpus.** Every reference from one document
   to a numbered section of another resolves to the section it names. Match on the
   section token alone so backticks, brackets, markdown links and abbreviations are
   all caught, and establish the target by reading each hit rather than by matching a
   filename first. State the pattern so the coverage is checkable. The pattern in
   current use is recorded in `PROGRESS.md` under Corpus consistency passes.

**The pass states divergences and does not correct them.** A conformance pass that
fixes what it finds destroys the evidence that it was found. Corrections are a
separate pass with its own numbered checkpoints, and it does not run the checks.

---

## Phase P — Data probe

**Status:** `DONE`, signed off 2026-08-06. Produced D-57 to D-63 and four committed
transcripts. Sign-off block in `PROGRESS.md`.
**Runs before phase 0, because its answers change the schema.**

A scratch tool under `tools/probe`. A measuring instrument, not a component. Print
raw counts. No interfaces, no repository pattern, no retry policy, no abstraction
over the data provider. It will be deleted or rewritten and that is fine.

Run everything against five or six companies known to sit between $300M and $2B
across different sectors, plus one megacap as a control. Everything looks fine on a
megacap, which is exactly why a megacap tells you nothing.

Four questions:

1. **News archive depth.** Request news for a small cap with a from-date five years
   back. Report the earliest article actually returned. If it stops short, the
   digest field is null for the earlier backfill and that has to be known now.
2. **Sentiment coverage.** Pull the sentiment endpoint for each small cap. Report
   days of series returned and article counts per day. A sparse or empty series
   means the sentiment screen cannot see small caps at all.
3. **Flow coverage.** Insider transactions and short interest for each. Report
   transaction counts over 90 days and whether short interest is populated. If
   these are thin below $2B, the flow screen is a large and mid cap screen in
   practice and the size quota arithmetic changes.
4. **Filing dates.** Pull fundamentals for each and report, per quarter, whether a
   filing date is present and what the gap is between it and period end. If it is
   missing or equals period end on small caps, D-46 is not achievable from this
   source, which is a design problem rather than an inconvenience.

Also confirm the bulk end-of-day endpoint returns a full US day and report the row
count, since that number is the basis of the freshness guard tolerance.

### Checkpoints

| # | Scope |
|---|---|
| P.1 | Repository baseline: solution, console project at `tools/probe`, secrets wiring |
| P.2 | Probe scaffold: one file, raw HTTP and JSON, no abstraction, failures printed not thrown |
| P.3 | Sample selection, programmatic, six names in band across four or more sectors plus one control |
| P.4 | The four measurements plus the bulk end-of-day row count |
| P.5 | Recording into `PROGRESS.md`, editing the existing table in place |

**Done when:** all four questions have numeric answers recorded in `PROGRESS.md`,
and any that came back badly has a corresponding entry in `DECISIONS.md`.

**No API key enters the repository.** ~~Environment variable or user secrets.~~
[superseded, D-55] Secrets live in `appsettings.Secrets.json` beside the project that
needs them, excluded by wildcard, with `appsettings.Secrets.example.json` as the
committed template. D-55 replaced the older mechanism before this phase ran, and the
phase was executed against the corrected instruction. Check the gitignore before the
first commit.

---

## Phase 0 — Rails

**Status:** `NOT STARTED`

Repository structure, solution layout, Postgres schema and migrations, the stage
registry, run logging, and the conformance test over write ownership. One trivial
stage end to end to prove the rails work.

Conventions: .NET 10, `.slnx` solution format, `Directory.Build.props` and
`Directory.Packages.props` for central package management, `guards.ps1` for the CI
greps, `migrate.ps1` and `seed.ps1` at the root. Migrations are snapshot-first.
Also opens `FIXTURES.md` and `CHANGELOG.md`.

Project layout is `CLAUDE.md` §4 and D-56. The `Api` project must not reference
`Pipeline`, and phase 0 adds a test asserting that, since it is the structural form of
the read-only guarantee.

Also a bare run viewer. Not the run health screen, just enough to see stages,
durations and row counts. Debugging a thirty-four component pipeline through raw
SQL for eight phases is miserable, and this costs an afternoon.

The rails are the anti-drift mechanism. Building them after the first real component
means the component defines the pattern instead of the pattern defining the
component.

### Checkpoints

| # | Scope |
|---|---|
| 0.1 | Solution layout per `CLAUDE.md` §4 and D-56, central package management, `.gitattributes`, `guards.ps1` stub |
| 0.2 | Postgres schema and migrations from `SCHEMA.md`, snapshot-first, running clean from empty |
| 0.3 | Stage abstraction and registry: declared read and write sets, date and config version in, pure |
| 0.4 | Run logging, and the conformance test asserting that no two components claim the same component-table-operation triple, matching the per-operation declarations in `SCHEMA.md` [INVARIANT 10 as amended] |
| 0.5 | The `Api` must not reference `Pipeline` test |
| 0.6 | Bare run viewer: stages, durations, row counts. Not the run health screen |
| 0.7 | One no-op stage end to end, proving the rails |
| 0.8 | Open `FIXTURES.md` and `CHANGELOG.md`, first corpus version bump |

**Done when:** a no-op stage runs, logs, and appears in the viewer; the
write-ownership test reads the registry and passes; migrations run clean from empty.

**Invariants at risk:** 10, 11.

---

## Phase 1 — Ingest and universe

**Status:** `NOT STARTED`

Typed HTTP client for the data provider. Bulk end-of-day, fundamentals keyed on
filing date, sentiment for the whole universe, flow, events. The universe builder
applying D-4. The freshness guard.

### Checkpoints

| # | Scope |
|---|---|
| 1.1 | Typed HTTP client: `api_token` query auth, explicit `fmt=json` on every call, the `::` filter form with colons percent-encoded, and the 1,000-requests-a-minute limit |
| 1.2 | Bulk end-of-day ingest into `price_daily`, plus the settled-day rule: the most recent available day is still accreting and is not valid |
| 1.3 | Freshness guard on D-59's thresholds, abort below 40,000 and alert below 45,000 |
| 1.4 | Fundamentals ingest keyed on `filing_date_effective`, implementing D-62's per-ticker substitution, setting `filing_date_unknown_reason` across its four states, and excluding tickers below `fundamentals.min_clean_gaps_for_substitution` |
| 1.5 | Universe builder applying D-4, with 20-day median dollar volume computed from `price_daily` rather than any provider average |
| 1.6 | Sentiment ingest for the whole universe, tolerating a series with rows only on days carrying news |
| 1.7 | Flow ingest from `sec-filings/form4` and not the legacy endpoint, into `insider_transaction` and `institutional_holding` at natural grain per D-61, with `transaction_code` retained so open-market purchases are separable |
| 1.8 | Events ingest, and the derived `flow_daily` at ticker-by-day |
| 1.9 | Endpoint sweep at phase start, recording what the subscription reaches |
| 1.10 | Tests: no fundamental readable before its effective filing date; substitution fires on equality, null and negative-gap fixtures; a stale end-of-day file aborts the run |

**Done when:** one night of the whole US market lands; the universe builds to
roughly 2,000 names; feeding the freshness guard deliberately stale data aborts the
run and produces no orders; a test asserts no fundamental value is readable before
its filing date.

**Invariants at risk:** 1, 10, 11, 12.

**Carried obligation:** whatever the probe found about news depth and flow coverage
constrains what this phase can promise downstream. Record it.

---

## Phase 2 — Compute

**Status:** `NOT STARTED`

Indicators, valuation, market context including sector relative strength, and the
percentile engine with size-and-sector cells and the fifteen-member fallback.

**Done when:** a known ticker's indicators match a hand-computed reference; a
percentile spot-check confirms cell membership is correct and the fallback fires
where cells are thin.

**Invariants at risk:** 10, 11, 12.

---

## Phase 3 — Backfill of ingest and compute

**Status:** `NOT STARTED`

Five years, including delisted tickers, two-pass and parallel. Ticker-partitioned
for ingest, indicators and valuation. Date-partitioned for percentiles.

**Done when:** five years present; a spot check confirms delisted names are included;
a full rebuild finishes in minutes rather than hours; a replay of one historical
date produces byte-identical output to the first run.

**Invariants at risk:** 11, 12, 13.

---

## Phase 4 — Screens and candidate selection

**Status:** `NOT STARTED`
**This is the phase where you find out whether the idea works, and it is the last
one before money is spent on models.**

Gate engine, screen engine with the five screens as config rows, floors, the
candidate allocator with quotas and no backfill, the attribution write, and the
concentration monitor. Then backfill screen scores over the five years using the
two-pass approach, so floors are live from real history.

**Done when:** a night yields roughly 26 to 30 candidates; the 2/3/3 size
distribution holds; megacap share sits under a third including inside the 2022
drawdown; distinct tickers over any 60-day window exceed 250; overlap between
screens falls somewhere near 10 to 20 percent; attribution rows exist for every
candidate with the config version stamped.

**Invariants at risk:** 1, 2, 3, 4, 10, 13.

**What this phase does not test.** Profitability. The screens are a candidate
generator and their job is to surface a plausible and varied set for judgement.
Discrimination is the researcher's job, which is the entire reason the researcher
exists. Screen scores failing to predict forward returns on their own is not a
failure here. See D-42 and the rule in `CLAUDE.md` §11 against tuning screens on
forward returns before anything has judged them.

---

## Phase 5 — Digest chain

**Status:** `NOT STARTED`

Local client over the OpenAI-compatible endpoint, Haiku client, the ordered chain
with health checks, the nightly rotation of two candidates to the secondary, and the
gate.

**Done when:** digests are produced for a night and recorded with provider and model
name; stopping the local server falls through to the secondary and the fallthrough
is visible in the record; stopping both halts the run before any researcher call and
produces no orders; the rotation is present every night regardless of primary health.

**Invariants at risk:** 7, 10, 15.

---

## Phase 6 — The researcher

**Status:** `NOT STARTED`

Dossier builder producing the cached prefix and per-candidate blocks. Researcher
client running twice, Opus 5 batched and V4 Pro synchronous. Proposal validator with
all three checks.

**Done when:** the prefix snapshot test passes across a full night of calls; cache
hit rate exceeds 90 percent; a deliberately corrupted citation in a test dossier is
rejected; a proposal whose stated probability contradicts its own stop and target is
rejected; the batch path completes and the 09:00 deadline behaviour is exercised.

**Invariants at risk:** 6, 9, 10, 16.

**Carried obligation:** this is the first phase that spends money. The cost ledger
must be recording per model before the first full night, not after.

---

## Phase 7 — Risk, execution and portfolios

**Status:** `NOT STARTED`

Risk gate with the arbitration steps, paper broker, position manager, portfolio
registry, portfolio runner.

**Done when:** a night produces orders for every active portfolio; the worked
arbitration example in `ARCHITECTURE.html` §10 reproduces exactly as a fixture;
exposure matching holds across a sequence including a full-abstention night;
retiring a portfolio by config change stops new entries while open positions run to
natural exit.

**Invariants at risk:** 8, 10, 14.

---

## Phase 8 — Learning loops

**Status:** `NOT STARTED`

Forward return filler with all three benchmark columns, screen tuner, lesson writer,
calibration reporter.

**Done when:** forward returns fill correctly including the acquisition and
delisting cases; a test asserts the tuner's query never touches the SPY column; the
tuner moves slots on backfilled data and respects the floor of four and cap of
twelve; a reliability diagram renders from real backfilled attribution.

**Invariants at risk:** 5, 10, 13, 14.

---

## Phase 9 — API and UI

**Status:** `NOT STARTED`

Read-only query API, read model builder, Blazor client, the six screens.

**Done when:** all six screens render from real data; the digest chain indicator
shows all three states correctly including running-on-secondary; the readiness check
fires ahead of the evening window; no API endpoint can write anything except the
local model connection config.

**Invariants at risk:** 10, 15.

---

## Phase 10 — Soak

**Status:** `NOT STARTED`

Run nightly with nothing acted on. Watch cost, failures, concentration, and the
validator rejection rate.

**Done when:** twenty consecutive clean nights; annual cost forecast tracking within
range of the estimate; no concentration alert unexplained; validator rejection rate
per model recorded and stable.

**Nothing is concluded here.** A soak proves the machine runs, not that the idea
works. Portfolio returns over twenty nights carry no information at all, and the
temptation to read them is strongest exactly when the system has just started
producing them. The first readable evidence is the validator rejection rate at four to
six weeks; the primary claim needs a year [VALIDITY.md §3].

**Invariants at risk:** all of them, which is the point of a soak.

---

## Carried obligations

Items that a phase inherits rather than discovers. Add here rather than losing them
in a phase prompt.

| From | Owed to | Item |
|---|---|---|
| P | 1 | Whatever the probe finds about news depth, sentiment and flow coverage constrains what phase 1 can promise |
| P | 4 | If filing dates are absent on small caps, D-46 needs revisiting before screens are built |
| P | 1 | Entitlement is per endpoint and invisible in the account payload, which showed identical fields on both sides of the mid-phase upgrade. Phase 1 determines what a subscription reaches by sweeping endpoints, never by reading a field |
| P | 1 | The base rate of unknown filing dates across the whole universe is unknown, and seven names is a hint rather than an answer. It is a query against `fundamental_snapshot` once 1.4 has ingested, read at phase 1 sign-off, and it decides whether D-62's exclusion rule removes a handful of names or a meaningful slice |
| P | 1 | The dollar volume proxy does not survive into phase 1. Sample selection used `avgvol_50d * adjusted_close` because the bulk feed carries no median dollar volume, and average volume is unadjusted while `adjusted_close` is adjusted, so the product understates for any name that split inside the window. **Closed by 1.5**, which computes the metric from `price_daily`. Recommended by the build session at P.5 and never entered here until K.9 |
| P | 4 | The S4 open-market purchase base rate is unknown. Transaction code P was 0 on all seven names over 90 days, so `distinct_buyer_count` had nothing to rank on in that window, and six small caps plus a control cannot say whether that is the market or the sample. It is a query against `insider_transaction` once 1.7 has ingested, read at phase 1 sign-off, not a task for any phase |
| 0 | all | The write-ownership test must be extended as each phase adds tables |
| 4 | 8 | Config version must be stamped on attribution rows from the first write, or the tuner cannot segment history |
| 6 | 10 | Cost ledger recording per model before the first full night |
