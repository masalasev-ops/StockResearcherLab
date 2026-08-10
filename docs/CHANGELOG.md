# CHANGELOG.md

Corpus versions. Appended to, never rewritten.

The corpus is versioned as a whole rather than per document, so a version identifies
one consistent set of documents. `PROGRESS.md` records which corpus version the build
was working against.

---

## 0.1.0 — 2026-08-05

Initial corpus, written before any code exists.

- `CLAUDE.md` with sixteen invariants specific to this lab and the stage pattern
- `ARCHITECTURE.html`, 33 components across 7 layers, 11 figures
- `DECISIONS.md`, D-1 through D-55, all settled during scoping
- `BUILD_PLAN.md`, phases P and 0 through 10 with definitions of done
- `SCHEMA.md` with single-writer ownership per table
- `VALIDITY.md` with pre-registered thresholds
- `WORKED_EXAMPLE.md` tracing one candidate end to end
- `CONFIG_REFERENCE.md`, roughly 45 keys, all consumers unverified
- `GLOSSARY.md`, `FIXTURES.md` empty, `RUNBOOK.md`, `PROGRESS.md` empty
- `prompts/` with the five rubrics, prefix template, candidate block format and
  digest instruction, plus `prompts/spent/` holding three issued prompts

Repository conventions: .NET 10, `.slnx`, central package management, root holding
only `README.md` and `CLAUDE.md`, secrets in `appsettings.Secrets.json`, EODHD
dash-form tickers, append-only versioned config. Postgres throughout.

---

## 0.2.0 — 2026-08-06

Phase P, the data probe, signed off. The first version in which a figure in the
corpus is a measurement rather than a design estimate, and four of the assumptions
the architecture was built around turned out to be wrong.

- `DECISIONS.md`, D-57 through D-63. D-57 treats a filing date equal to its period
  end as unknown and is superseded by D-62, which makes the substitution each
  ticker's own widest clean gap rather than a universal 65 days; D-58 drops short
  interest from the flow screen, the provider having no series and no as-of date;
  D-59 sets the freshness guard thresholds from measured row counts; D-60 gates the
  mean reversion no-digest disqualifier on coverage having existed; D-61 ingests flow
  at each source's natural grain; D-63 permits correcting a spent prompt's archive to
  the text actually issued and nothing else
- `SCHEMA.md` gains `filing_date_effective` and `filing_date_unknown_reason` with its
  four states, and `flow_daily` becomes two source tables at natural grain plus a
  derived daily table
- `CONFIG_REFERENCE.md` loses the one key that had no decision behind it and gains
  D-59's two thresholds and D-62's minimum clean gap count
- `BUILD_PLAN.md` gains the conformance pass checks as the content of sign-off step 2
  and phase 1 checkpoints 1.1 to 1.10
- `CLAUDE.md` gains the supersession rule and the rule that a build session never
  adds a measurement to its own scope, and its spent-prompt clause is amended for
  D-63
- `PROGRESS.md` carries six probe findings, two conformance findings, corrective
  passes H and K, and a statement of what the evidence set does and does not cover
- `tools/probe` with four transcripts committed as the evidence behind every recorded
  figure

The phase failed its first conformance pass and the record says so. Four figures had
no measurement behind them and the transcripts were excluded from the repository, so
nothing recorded was checkable without the build machine. That is why `probe-output/`
is tracked and why sign-off step 2 is now written down rather than passed between
sessions.

---

## 0.2.1 — 2026-08-06

Corrective passes L, M and N. No code, no measurement, nothing signed off. The
documents were made to agree with the decisions phase P had already produced.

- `ARCHITECTURE.html` absorbs D-57 to D-63. C34 FlowEngine added for D-61, which
  `SCHEMA.md` had already declared a writer for and the architecture had no component
  for; D-62's exclusion added to the universe criteria; D-60's coverage condition,
  D-18's probability and D-59's thresholds carried in; the store matrix gains
  `insider_transaction`, `institutional_holding` and `portfolio_selection`
- INVARIANT 10 restated as one writer per table per operation, in `CLAUDE.md` under the
  supersession rule and in `SCHEMA.md`'s ownership declarations. The old
  two-exceptions rule had been wrong in both directions: three tables carried multiple
  writers from the first draft. Checkpoint 0.4 now describes a test that can be written
- The clean filing-gap count made a computation rather than a stored column, because it
  is as-of and a stored total read during backfill would admit names a live system
  would have excluded
- D-62 gains a stated limitation: `filing_date_effective` is itself as-of, runs
  conservative rather than early, and is accepted rather than corrected
- `order` given one writer. RiskGate inserts for all four portfolios and
  PortfolioRunner persists to `portfolio_selection`, so risk rules exist in exactly one
  place [INVARIANT 8]
- `BUILD_PLAN.md` phase 1's definition of done extended from four of its ten
  checkpoints; `FIXTURES.md` gains the substitution and exclusion fixtures that
  checkpoint 1.10 had been enumerating in its place; `README.md` stops naming a corpus
  version in the sentence that says it names none
- `prompts/candidate-block.md` no longer tells the model that a missing digest cannot
  happen, which D-60 is built on

Recorded rather than fixed: the 0.2.0 entry above does not name `RUNBOOK.md`,
`prompts/rubrics.md` or `prompts/README.md`, all three changed during phase P. This
file is appended to and never rewritten, so the omission stands and is stated here
instead.

---

## 0.3.0 — 2026-08-06

Phase 0, the rails. The first version with code in it. Nothing is signed off and
no measurement was made.

- Six source projects and the test project, per `CLAUDE.md` §4 and D-56. `Core` has
  no project references and `Api` has none to `Pipeline`, which is the structural
  form of the read-only guarantee and now carries a test that fails if the
  reference is added
- Postgres schema and migrations, snapshot-first, from `SCHEMA.md`. Thirty-three
  tables in `public`, one per table the document declares, including
  `portfolio_selection`. The migration ledger sits in a `meta` schema so that
  "every table in the database is in `SCHEMA.md`" can be asserted without an
  exception carved out of it
- The stage abstraction: read set, write set per operation, date and config version
  in, and no route to data except one that checks the declaration first. `IClock` is
  the only route to the current time
- The stage registry, and the conformance test asserting no two components claim the
  same table and operation, with the three splits `SCHEMA.md` declares permitted and
  a fourth failing
- Run logging into `run_log`, a bare run viewer over it, and one no-op stage that
  runs from the command line, logs, and appears in the viewer
- `guards.ps1`, `migrate.ps1` and `seed.ps1` at the root. `migrate.ps1` is real;
  the other two are stubs that say what they will do and exit zero
- `FIXTURES.md` opened with three fixtures, each one existing to make a test fail on
  purpose
- `tools/probe` deleted. Its four transcripts were kept, in `docs/evidence/phase-P/`,
  because they are the evidence behind every figure in the Probe findings table and
  deleting them would undo H.1

The test project lives in `src/` rather than `tests/`, and `CLAUDE.md` §4 and
`README.md` were updated by the author to match.

---

## Closed — 2026-08-06

**Corpus versioning stops here. This is the last entry and the file is not
appended to again** [D-67].

Versioning the corpus as a whole made sense while the documents were the only
artefact and a phase could change six of them at once. Three versions later the
code is the artefact, git is the version, and a number bumped by hand at sign-off
recorded that documents had changed without recording what a reader could not
already see from the diff. It caught nothing across phases P and 0, and the
sign-off step that produced it went with the other three at D-67.

Two lines above are now historical rather than current. The header says
`PROGRESS.md` records which corpus version the build was working against; that
line is removed from `PROGRESS.md` at O.6. And the 0.1.0 entry describes
`SCHEMA.md` as single-writer ownership per table, which INVARIANT 10 has not been
since L.3. Both stand as written, because this file is a record and correcting a
record is how it stops being one.

What replaces it: nothing. A document change is a commit, the commit says what it
changed and why, and `PROGRESS.md` says what is built.

---

## Reopened — 2026-08-10

**D-73 reopens this file, for a different job.** The entry above closed it because a
version number bumped by hand recorded that documents had changed without recording
what a reader could not already see from the diff. That reason stands and nothing
undoes it: no version number is resumed, no sign-off step produces these entries, and
the headings below are dates rather than versions.

What comes back is the file, holding one thing it did not hold before. D-73 makes a
removal from a spec document a clean edit rather than a strike, and a clean edit
destroys the only copy of the prior wording unless something else keeps it. This is
that something else. The rule from here: text deleted from `ARCHITECTURE.html`,
`SCHEMA.md`, `CONFIG_REFERENCE.md` or `RUNBOOK.md` is recorded here before it goes,
and this is the only place it survives.

The closing entry above says this is the last entry and that the file is not appended
to again. It was appended to, here. That entry is a record and stands as written
[`CLAUDE.md` §13].

---

## 2026-08-10, post phase 1 reconciliation

`ARCHITECTURE.html` reconciled with the code phase 1 built and with decisions already
in the register, then left with no struck text in it. Four decisions, one document,
no code change. The conformance test that would have caught the Reads drift is a
separate commit and is recorded in `PROGRESS.md` rather than here.

### The decisions

- **D-73** splits the supersession convention by what a document is for. Specs are
  clean and records keep their strikes, and this file holds what the clean edits
  removed. Amends `CLAUDE.md` §13
- **D-74** puts the source of a stage's ticker list in §3's Reads column. Four ingest
  components had endpoints there and nothing else, while `DeclaredAccess` enforced
  read sets the catalogue never mentioned. No code change: the document begins
  describing what the code already does
- **D-75** makes `FlowIngestor` nightly, against `RUNBOOK.md`'s 17:45, on coverage
  latency rather than filing latency. A universe pass is about twelve runs at
  `flow.max_tickers_per_run`, which is twelve days nightly and three months weekly
  against a ninety-day trailing window
- **D-76** drops the Written-by and Read-by columns from §16's store matrix and
  closes open item 6. Writes are stated in §3 and in `SCHEMA.md`, which a test holds
  against each other, and the third statement was checked by nothing

### What §3 and §16 now say

- C03, C04, C05 and C06 name `security` in Reads, C03 also naming `price_daily`,
  each cited to D-74
- C05 runs `Daily 17:45` rather than `Weekly`, cited to D-75. This is the only Runs
  cell that changed
- §16 has three columns, Store, Grain and After backfill, and carries a line under
  the table pointing at §3 and `SCHEMA.md`

### Text removed from `ARCHITECTURE.html`, verbatim

Every deletion this pass made, with the citation left at the point of change. Three
of them cite a correction-pass clause that does not name what it removed and one
cites a clause with no record anywhere in the corpus; those four are marked, and they
are the reason this list exists rather than a courtesy.

**§3, C05 description** [D-58]
> Short interest keyed on publication date, not settlement date

**§3, C05 Reads** [D-58]
> short interest,

**§3, C05 Writes** [D-61]
> flow_daily

**§3, C07 FreshnessGuard Writes** [N.3, **which does not name it**]
> run_log

**§3, C32 LocalModelClient Writes** [N.3, **which does not name it**]
> run_log

**§3, C16 ResearcherClient Writes** [N.3, **which does not name it**]
> cost_ledger

**§3, C25 PortfolioRunner Writes** [N.1]
> order

**§4, the C07 node in the nightly flow figure** [D-65]
> The latest price date must equal today and the row count must be within tolerance.
> On failure the run aborts and no orders are produced.

**§5, the S4 node in the screen figure** [D-58]
> short interest change

**§5, the screen input table, S4 row** [D-58]
> short_interest_change

**§7, the S5 mean reversion disqualifier** [D-60]
> Also no digest available while the decline exceeds 35 percent, because at that
> magnitude the absence of information is itself the reason to pass.

**§7, the note headed "The harder ceiling was not tokens"** [D-18]
> The output is BUY or PASS, a conviction integer from one to five, a stop and a
> target. That is a low-bandwidth decision, and past some point extra evidence moves
> an internal judgment from 3.4 to 3.6 while the recorded answer stays 3. The
> coarseness of what is asked for caps the usefulness of what is supplied. If richer
> input is ever wanted, the thing to widen is the conviction scale, not the block.

**§7, the candidate block metric table, Flow row** [D-58]
> short_interest_pct_float · short_interest_change

**§14, the backfill hazard table, lookahead row** [D-62]
> filing_date

**§14, the backfill hazard table, an entire row** [D-58]
> | Short interest timing | Settlement date precedes publication by days, so using
> it reads data before it existed. | Key on publication date |

**§16, the store matrix, `flow_daily` grain** [D-61]
> ticker × week

**§16, the store matrix, the Written-by column** [D-61, N.1]
> `flow_daily` read `C34, C05` with `C05` struck, and `order / fill / position` read
> `C18 C19 C20 C25` with `C25` struck. Both went with the column at D-76, along with
> every other cell in it and in Read-by

**§18, the failure table, recency row** [D-65]
> The latest price date must equal today.

**§18, the failure table, completeness row** [O.8, **which has no record anywhere in
the corpus**]
> Settled days measured 50,029 to 50,204 against part-settled sessions at 3,544 and
> 9,072, so the two populations are an order of magnitude apart and a wide floor
> separates them with no false positives.

**§18, the failure table, completeness row** [D-64, D-65]
> The populations meet: 44,708 is 11 percent below the lowest settled count and sits
> above the 40,000 abort floor and inside the 40,000 to 45,000 alert band. A
> part-settled file of that shape passes the guard. Owed to phase 1 as **D-64**, and
> the assertion that the latest price date equals today is owed as **D-65**.

### Text removed from the other three spec documents, verbatim

D-73 names four documents and only `ARCHITECTURE.html` conformed. `SCHEMA.md`,
`CONFIG_REFERENCE.md` and `RUNBOOK.md` carried eighteen removals between them,
swept under the same procedure and the same safety condition. `grep -c "~~"` returns
0 for all three.

**Four rows were removed rather than left empty**, each named below, because a strike
covering every cell of a row leaves nothing the row was for.

**Three sentences were rebuilt rather than trimmed**, because the strike carried the
subject and deleting it alone would have left prose that does not parse. Each is
named at its entry with what the sentence now says, and none of them adds a claim:
`SCHEMA.md`'s nullable column, its `report_date` sentence, and
`CONFIG_REFERENCE.md`'s "Phase P measured it".

#### `SCHEMA.md`

**The ownership preamble** [INVARIANT 10, L.3]
> and two tables are documented exceptions

**`security`, the clean gap count** [M.1]
> as `clean_gap_count`, maintained by FundamentalsIngestor

**`fundamental_snapshot`, the effective-date column** [1.4, **which does not name
it**]. The sentence was rebuilt: it opened "The column was ~~`NOT NULL`~~ and is
nullable, where null means ..." and now opens "The column is nullable, where null
means ...", with the rest of the paragraph unchanged.
> The column was `NOT NULL`

**`institutional_holding`** [D-69]. Rebuilt: the struck claim was followed by
"Measured false at 1.9", which had nothing left to refer to, so the sentence now
states the negation D-69 states, "**`report_date` does not make this backfillable**
[D-69], measured false at 1.9". The evidence after it is unchanged.
> `report_date` is what makes this backfillable, and it is the field short interest
> turned out not to have.

**`flow_daily`, grain** [D-61]
> ticker by week

**`flow_daily`, the column list** [A1.a]
> `insider_net_usd_90d`

**`flow_daily`, the column list** [D-58, D-61]
> `week_end`, `publication_date`, `short_interest_pct_float`, `short_interest_change`

**`indicator_daily`, the column list** [O.2, **which has no record anywhere in the
corpus**]
> , `median_dollar_volume_20d`

**`attribution`** [INVARIANT 10 as amended]
> **This is the only table with two writers, and it is deliberate.**

**`proposal`** [INVARIANT 10 as amended]
> Second deliberate two-writer pair

**`order / fill / position`** [N.1]
> RiskGate and PortfolioRunner both insert orders and are the one pair that shares an
> operation on a table. They are separated by portfolio: the runner writes for every
> non-research portfolio off the shared candidate set, the gate writes for the
> research portfolios after arbitration.

#### `CONFIG_REFERENCE.md`

**The fundamentals key table, an entire row removed rather than left empty** [D-62].
Every cell of it was struck, so nothing remained to keep.
> | `fundamentals.filing_date_substitution_days` | 65 | D-57 | FundamentalsIngestor | [superseded, D-62] |

**The same table, the Consumer column of
`fundamentals.min_clean_gaps_for_substitution`** [L.2, **which has no record anywhere
in the corpus**]
> , FundamentalsIngestor

**The substitution window paragraph** [D-62]
> the widest gap the probe observed, not a mean, because being late costs freshness
> while being early costs correctness

**The freshness key table, an entire row removed rather than left empty** [D-59].
Key and default were both struck and the Set by column was already a dash.
> | `freshness.row_count_tolerance` | from probe | — | FreshnessGuard | [removed, D-59] |

**The paragraph above the freshness keys** [D-59]. Rebuilt: the sentence after it
read "Phase P measured it", whose "it" was the struck row count, and now reads "Phase
P measured the bulk end-of-day row count [D-59]". The measurements after it are
unchanged.
> The freshness tolerance has no default until phase P measures a real bulk
> end-of-day row count.

**The guard-keys paragraph** [D-70]
> **The guard has three checks and only one of them has a key** [D-65]. The two keys
> above are completeness. Recency reads the exchange calendar for the most recent
> completed trading session, and settledness compares a re-fetch of a date against
> the rows already stored for it. Neither is a threshold, so neither gets a key, and
> adding one would invent a bound where the decision deliberately introduced none.

#### `RUNBOOK.md`

**The failure table, an entire row removed rather than left empty** [D-65]. Every
cell of it was struck and the three rows D-65 split it into sit directly below.
> | End-of-day file stale or short | FreshnessGuard | Abort. No orders | Check the provider. Rerun when fresh. A skipped night costs nothing |

---

## 2026-08-10, phase 2 checkpoint 2.2

Migration `0004` and its declarations. One clean edit to a spec document, recorded
here because D-73 makes this the only place the prior wording survives.

#### `SCHEMA.md`

**The monetary count, in "Columns that are not money"** [D-79]. One word, replaced
by `Eighteen`, with the paragraph naming the eighteenth added below it. The count is
exact rather than a floor precisely so that a monetary column cannot arrive without
moving it, so this is the mechanism working rather than a correction.
> Seventeen

Nothing else was removed. The rest of 2.2's changes to `SCHEMA.md` are additions:
the `sentiment_derived_daily` section, four column names on `indicator_daily`, and
thirty-seven rows declaring new `real` columns as not money.
