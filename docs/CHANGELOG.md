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

---

## 2026-08-10, phase 2 checkpoint 2.3

D-77 applied. The count of permitted splits removed from `SCHEMA.md`'s opening, the
four compute tables naming both their writers, and the percentile-columns section no
longer naming one.

#### `SCHEMA.md`

**The exception enumeration, the whole paragraph** [D-77]. Removed rather than
amended: the percentile engine takes the count from three splits over five tables to
seven over nine, and no number survives the next correct design either. The paragraph
replacing it carries the reasoning without a count and cites D-77. Its own last
sentence is why it went, and that sentence is kept in the replacement.
> The exception list was the wrong shape rather than too short. `attribution`,
> `proposal`, and `order` with `fill` and `position` each had more than one writer
> from the first draft, which is already past the two the old rule allowed. Three
> splits over five tables, and no fourth: the one candidate for it, a stored clean
> gap count on `security`, turned out to want computing rather than storing [M.1].
> Every attempt to enumerate exceptions ran out before the list was complete, because
> a rule that counts exceptions gets longer every time the design is correct.

**The `### percentile columns` opening line** [D-77]. The four table headings now
carry the fact and a third statement of it is the duplication D-73 and D-76 remove.
What replaces it states the naming convention and the type rule instead.
> Written alongside their source tables by **PercentileEngine**.

**Four writer lines, each replaced by a two-writer form** [D-77]. Recorded as one
entry because the change is identical in shape at each: `Writer: X.` becomes
`Writers: X inserts, PercentileEngine updates the percentile columns`.
> **Writer: IndicatorEngine.**

> **Writer: ValuationEngine.**

> **Writer: FlowEngine, a compute stage, not the ingest.**

> **Writer: SentimentEngine, a compute stage, not the ingest.**

---

## 2026-08-10, phase 2 checkpoint 2.14

`ARCHITECTURE.html` amended: C35 SentimentEngine gains a catalogue row.

Numbered 2.14 rather than inserted, because a checkpoint number is a plan reference
and not an execution order, which 1.9 established. The plan's Stage A runs 2.1 to
2.4 and this arrived after 2.3. An
architecture amendment rather than a removal, so nothing verbatim is recorded below
and this entry exists because the document changed.

D-78 authored the component at 2.2 and `SCHEMA.md` named it as a writer at 2.3, but
§3 did not have it. `SchemaDocument.WritersByTable` keeps only bolded words §3
catalogues, so `sentiment_derived_daily` parsed to one writer rather than two, and
2.8 registering the component would have tripped `RegistryNameTests`, which is the
check that exists for exactly this.

- §3 gains a C35 row after C34, matching that row's markup: `NEW` badge, a `muted`
  description citing D-78, Runs `Daily 18:05`, Writes `sentiment_derived_daily`.
- Its Reads cell names `sentiment_daily` and `security` in `<code>`, and says what
  the `security` read is for, because D-74 made that standing. A row written after
  that decision honours it at birth rather than becoming a fifth deviation someone
  finds later.
- §1's layer 2 list gains C35 beside C08, C09, C10, C11 and C34.

`CataloguedComponents` moves 34 to 35. The amendment as issued said 35 to 36; the
constant read 34 and §3 carried exactly 34 rows, both checked before the edit. The
figure is an observation rather than a decision, so it is corrected here rather than
carried [`CLAUDE.md` §13].

---

## 2026-08-10, phase 2 checkpoint 2.15

`ARCHITECTURE.html` §3 amended: the C10 and C11 Reads cells name tables, and C11's
Writes cell names tables rather than a column pattern. An architecture amendment, so
nothing verbatim is recorded below and this entry exists because the document changed.

**C10's cell named a store that does not exist and will not.** It read "Index and
sector prices". The benchmark is the SPY row already in `price_daily`, which C02
writes because the bulk feed carries every US ticker, and the sector series is a
composite of the universe's own members [2.6]. So the phrase was wrong as well as
unparseable, and this is not the markup fix it looks like. It now names
`price_daily`, `security` and `indicator_daily`, each in `<code>`, saying what the
read is for where that is not obvious [D-74].

**C11's cell said "All metric stores".** Enumerated: `indicator_daily`,
`valuation_daily`, `flow_daily`, `sentiment_derived_daily` and `security`. The
generalisation was doing no work, because nothing enforced it and no test could read
it. Enumerating gains a check and brings D-77's brake to bear, since a fifth metric
store now means editing an authored document rather than being silently covered by a
phrase.

**C11's Writes cell said `*_pctile columns`**, which names no table. The four tables
now name PercentileEngine in their own `SCHEMA.md` headings [D-77], so the cell names
the four tables. No test reads that cell, so this is tidiness rather than a fix, and
it is recorded as such.

---

## 2026-08-11, phase 2 sign-off

`ARCHITECTURE.html` §3 and `METRICS.md` §3, §5 and §6.1 amended, all four from findings
in the sign-off review. Removals, so the prior wording is recorded verbatim below.

**§3's C11 row stated a different rule from the one the code implements, and the two
disagree on real cells.** It said the fallback fires when a cell has fewer than 15
members. The implemented rule is fewer than 15 non-null values for the metric being
ranked, which is what `CONFIG_REFERENCE.md` has stated since 2.1 and what `METRICS.md`
§6.4 states in its fallback list. On the blessed date 21 cells fell back for
`fcf_yield`, and only 3 of them have fewer than 15 members, so 18 cells and all 110
fallen-back names are treated differently under the two wordings. The code is right and
the sentence was the loose one. Removed:

> Ranks inside size bucket by sector cells, falling back to size bucket alone when a
> cell has fewer than 15 members

**`METRICS.md` §6.1 carried the same loose wording two pages from its own §6.2 and
§6.4, which carry the strict one.** The document disagreed with itself. Removed:

> Percentiles are computed within size bucket by sector cells, falling back to size
> bucket alone when a cell has fewer than `percentile.cell_min_members` members [D-10,
> default 15].

**`METRICS.md` §3's `cash_on_hand` rule is superseded by D-81.** The coalesce to zero
ran in both directions and only one of them is a zero. Removed:

> ```
> coalesce(cash_and_equivalents, 0) + coalesce(short_term_investments, 0)
> ```
>
> falling back to `cash` when both are absent, and null when all three are absent.
>
> **The coalesce to zero here is deliberate and is the one place in this document that
> does it.** A company reporting cash and equivalents but no short-term investments line
> has no short-term investments, which is zero rather than unknown, and treating it as
> unknown would null the field for most of the universe. The fallback to `cash` covers
> the shape where the provider sends the aggregate and not the parts. `numeric`.

**`METRICS.md` §5 named regime label values the database rejects.** The proposal block
spelled them `risk-on` and `risk-off` where D-80, `SCHEMA.md` and
`0005_regime_label.sql`'s `CHECK` all use underscores, so a reader reproducing the
document's literals would have written rows Postgres refuses with 23514. Corrected to
underscores, with a pointer added naming D-80 as the authored rule. The section stays
BLOCKED, because promoting it belongs with the other eight PROPOSAL entries and is one
act rather than nine. Removed:

> ```
> risk-on  when breadth >= market.regime_breadth_high
>              and the universe composite is above its own 200-day average
> risk-off when breadth <= market.regime_breadth_low
>              and the universe composite is below its own 200-day average
> mixed    otherwise
> ```

`METRICS.md` is not one of the four documents D-73 names as kept clean. It carried no
strikes before this, so the removals are clean edits with the prior wording recorded
here rather than struck in place. If the intent is that it keeps its strikes, this entry
is what makes that reversible.

---

## 2026-08-11, D-83

`ARCHITECTURE.html` §04 and `SCHEMA.md` amended under D-83, which removes a count from
a spec rather than correcting it. Human-directed; D-83 was authored before either
document was touched [`CLAUDE.md` §13]. Clean edits under D-73, prior wording verbatim
below.

**Both documents stated how many technical columns there are and nothing had ever
checked either.** Phase 2 built fifteen against a design-time estimate of forty, so two
documents disagreed with `PROGRESS.md` about the same fact. Correcting forty to fifteen
would go stale the first time a later phase adds a column and would leave the next
reader unable to tell a checked number from an unchecked one, so both now state what
governs the set and point at the list. Removed from `ARCHITECTURE.html` §04's C08
diagram node:

> ~40 technical columns per name

Removed from `SCHEMA.md`'s `indicator_daily` section, which is followed by the list it
was counting:

> Roughly forty technical columns plus their percentiles.

**A third occurrence, in `SCHEMA.md` §Types, was a stale estimate carried inside an
argument rather than a statement about the schema.** It explains why a file exclusion
list was replaced by a declaration, and cited the forty as the scale of what was coming.
The argument is unchanged and the number is gone, because a spec citing an unchecked
count in passing is the same defect wearing a different sentence. The sentence read:

> was the previous mechanism and it had reached two entries with phase 2's forty
> technical columns still to come, at which point the guard would have been suppressed
> rather than satisfied.

Of which the removed span is "phase 2's forty technical columns", now "the whole of
phase 2's compute layer". The clause from "at which point" onward is unchanged and is
quoted here only so the sentence reads as it did.

**`PROGRESS.md`'s fifteen is untouched**, and so is its estimated-to-measured row
carrying the `~40`. That document is a record of what was built and of an estimate that
became a measurement, not a spec, and a record that loses the estimate loses the point
of having recorded the correction.

**What D-83 does not reach.** `SCHEMA.md`'s "Eighteen columns match the monetary pattern
and are `numeric`" stays exactly as it was. `guards.ps1` asserts it against the
migrations and `SchemaParityTests` asserts it against the live database, so it is a
count a test reads, which is the case the decision explicitly excludes.

---

## 2026-08-11, `SCHEMA.md` duplicate paragraph

**A removal of a duplicate, not a change of fact.** The paragraph beginning "The
eighteenth is `fundamental_snapshot.capital_expenditures`" appeared twice in §Types,
identically, and now appears once. Nothing it states has changed: capital expenditures
is still the eighteenth monetary column, the count is still eighteen, and `guards.ps1`
check 4 and `SchemaParityTests.EveryMonetaryNamedColumnInTheDatabaseIsNumeric` both
still assert that number.

This entry says duplicate rather than removed deliberately. An entry reading "removed"
against a paragraph a later reader can still find would tell them the opposite of what
happened, and the changelog was reopened at the post phase 1 reconciliation precisely so
that a removal could be trusted to mean one.

---

## 2026-08-11, D-84 to D-90, the screen lifecycle

Seven decisions authored into `DECISIONS.md` and `ARCHITECTURE.html` amended under six of
them. Human-directed, and the decisions were authored before the document was touched,
because the cells cite them [`CLAUDE.md` §13]. Clean edits under D-73, prior wording
verbatim in the entries below, one per decision.

**The document described a system with exactly five screens and no way to add or remove
one.** Registration, shadow running, promotion and retirement are new concepts and none of
them existed in it. The design they implement is `docs/SCREEN_LIFECYCLE.md`, which answers
the brief archived at `prompts/spent/design-screen-lifecycle.md`. Nothing in any of them
rests on a measurement, and none exists to rest on: phase 3 has not run, screens are phase
4 and the tuner is phase 8. That timing is the point rather than an accident of scheduling
[`CLAUDE.md` §11].

**The narrative went into §13 beside the tuner, which computes the rule, and §05 and §06
gained one sentence each pointing at it.** A new section describing all three components
would restate what three sections already open up, which is the shape D-76, D-77 and D-83
each removed; splitting the rule across three would leave a reader assembling it. One
statement of the rule, two statements of behaviour.

**Eleven occurrences of "five screens" were audited individually and all eleven are
resolved.** The brief that commissioned the work said nine, which a case-sensitive grep
confirms and a whitespace-tolerant case-insensitive sweep refutes: lines 252 and 281 opened
sentences with "Five screens". That the count was itself an unchecked count is D-83's defect
appearing inside the instruction to fix it, and it is recorded here rather than quietly
corrected. Nine changed. Two survive deliberately, inside the megacap and small-cap cards,
where the new sentence states a general property and names the configuration it generalises
from. `SCREEN_LIFECYCLE.md` §12.7 carries the audit with a sense for each.

**All three slot counts stay.** The pool of forty, the floor of four and the cap of twelve
are the three inputs to the arithmetic that makes four to ten live screens the feasible
range, so they are numbers the design rests on rather than counts of a set, and D-83 does
not reach them. Two of the three gained "among the live screens".

**What was deliberately not touched.** `SCHEMA.md` gains nothing yet. D-85's `surfaced_as`
and D-87's `screen_evaluation` are declarations `guards.ps1` and `SchemaParityTests` hold
against the migrations and the live database in both directions, so declaring either before
its migration exists turns a green check red for a table that is correctly absent. Both are
carried obligations in `BUILD_PLAN.md` against the phase that migrates them.

---

## 2026-08-11, D-84

A screen has three states. Removed from `ARCHITECTURE.html` §03's C13 row, which named a
fixed five and now names the states:

> Runs the five screens from config. Maintains each screen's trailing 250-day distribution
> for its floor

Removed from §04's figure 2 C13 node:

> Five screens, each with its own ranking and its own floor

Removed from §05's opening paragraph, which gained the state sentence and the pointer to
§13:

> Five screens, each with its own metric set, each blind to the others. Definitions live in
> `config_rows` under the `screens.*` keys, as data rather than code, so a sixth screen is a
> config row and not a deployment.

Removed from figure 3's caption:

> Figure 3 — five screens running in parallel off one percentile store

Removed from §16's note on the screen score table, which is the sizing argument and stated
the count twice in one sentence:

> Every ticker is scored by all five screens every day, because the floor is the 98th
> percentile of that screen's own trailing distribution and you cannot know the distribution
> without scoring everyone. That is five rows per ticker per day rather than one.

Removed from §16's store matrix, where the grain and the size both assumed five. The size
is now stated per registered screen, which is the rule the figure was an instance of
[D-83], and it reproduces 1.4 GB at five:

> `screen_score_daily` | ticker × screen × day | **1.4 GB**

Removed from the same table's total row, which gained what a further registered screen
costs:

> Roughly 5 GB after a five-year backfill, growing about 700 MB a year

Removed from §18's failure table, where the halt is a data-fault signal and a shadow
returning zero is not one:

> All five screens return zero

**One further removal under D-83's standing rule rather than under D-84.** §02's instrument
type row counted how many screens need fundamentals, which is illustration in a sentence
about excluding funds and moves whenever the registry does:

> Funds, trusts and SPACs excluded. Three of the five screens need company fundamentals that
> a fund does not have.

---

## 2026-08-11, D-85

Shadow screens write `attribution` and never `candidate_set`. Removed from
`ARCHITECTURE.html` §03's C14 row, which now says what it writes a row for and what the
candidate set is drawn from, those being two different populations for the first time:

> Size quota per screen, no backfill, dedup across screens, writes attribution

Removed from §06's deduplication node, which now names the live screens, a shadow not being
deduplicated into `candidate_set` at all:

> Union across the five screens

Removed from §06's attribution grain card under D-83. Two counts stated in prose that
nothing checked, and both move the moment a screen is registered:

> Around 7,000 rows a year, each tagged with its screen. Roughly 1,400 per screen, which is
> enough to say something about each within a year.

§06's "The attribution write happens here, not later" note gained two sentences and lost
nothing. §15's U4 row is quoted under D-87, which is the decision that changed its Reads
cell.

---

## 2026-08-11, D-86

Backfilled observations count toward a shadow's distribution and never toward a promotion
or a retirement.

**Nothing was removed from any document.** The rule is new prose in §13 and the decision is
new in the register. It is recorded here because a decision with no prior wording still
changes what the corpus says, and an entry a reader cannot find is the same as no entry.

The decision reads two authored documents against their surface and says so in its own
body: `VALIDITY.md` §6's "backfill is never used to evaluate the researcher, only the
screens" has the researcher as its subject, and `BUILD_PLAN.md` phase 8's "the tuner moves
slots on backfilled data" is a build verification criterion. Neither document is edited,
because neither is wrong. If either reading is wrong it is D-86 that changes.

---

## 2026-08-11, D-87

A screen is retired on sustained peer-relative underperformance, and the tuner is what
measures it. Removed from `ARCHITECTURE.html` §03's C22 row, whose Writes cell gained
`screen_evaluation`:

> Reallocates the 40 slots between screens on peer-relative hit rate and alpha, shrunk,
> floor 4 and cap 12

Removed from §13's figure 10 C22 node, which gained the allocation filter and kept the 40:

> Reallocates the 40 slots on peer-relative hit rate and alpha, shrunk 0.8 old and 0.2
> implied, floor 4 and cap 12

Removed from §15's U4 row, which cites D-84, D-86 and D-87 and whose Reads cell gained
`screen_evaluation`. The counts a shadow needs are separated rather than summed, because
§15's own first density rule is that no number appears without its comparison and its
sample size, and D-86 makes a backfilled count and a prospective count mean different
things:

> The five screens with current slot allocation, fill rate, hit rate, mean peer-relative
> alpha and current floor. History of how the tuner has moved slots. Recent candidates per
> screen and how they went.

§13 gained the lifecycle section, which is new prose and replaced nothing.

---

## 2026-08-11, D-88

A promotion or a retirement splits the primary claim, so none executes before the claim has
its sample and all due are bundled into one boundary.

**Nothing was removed.** The rule is new prose in §13 and one clause in §03's C22 row,
which now says the tuner nominates and executes neither. It is recorded here for the reason
D-86's entry gives.

---

## 2026-08-11, D-89

The slot pool stays at 40, D-7's 2/3/3 is restated as a proportion, and the feasible
live-screen range is four to ten.

Removed from `ARCHITECTURE.html` §06's megacap bound card:

> Two large slots across five screens is 10 of a possible 40, or 25 percent. Close to the
> megacap share of total US market capitalisation, so the bound is defensible rather than
> arbitrary.

Removed from §06's small cap floor card:

> Three small slots across five screens is up to 15 guaranteed places for names between
> $300M and $2B, subject only to clearing each screen's own floor.

**Both cards gained a stronger claim rather than a corrected number, and that is the whole
reason the proportion was worth restating.** Ten of forty and fifteen of forty were stated
as properties of five screens of eight. Under D-89's proportion the large share never
exceeds a quarter of any screen's slots at any count, so ten of forty holds at every
live-screen count; and the guaranteed small-cap places are minimised at exactly five
screens of eight, every other feasible allocation giving sixteen to twenty, so fifteen is a
floor across the whole reachable space. Each card therefore still names five screens of
eight, as the configuration the general property is tightest at.

§13's lifecycle section carries the four-to-ten bound.

---

## 2026-08-11, D-90

Whether post-earnings drift is registered, and on what history. `OPEN`.

**Nothing was removed and nothing in `ARCHITECTURE.html` cites it.** It is in the register's
Open section, alongside D-53, D-54 and D-69, and it is recorded here because an open
decision authored on a date is a corpus change like any other.

It exists because `events.earnings_backward_days` is 7 and `announced_date` is null for
earnings, so D-86's condition fails for that screen and only that screen. It is the third
instance of the pattern D-58 removed short interest for and D-69 is open on, and the first
found before anything was built on it.

---

## 2026-08-11, D-91

`ARCHITECTURE.html` §03's C03 row amended under D-91. Human-directed; the decision was
authored before the document was touched [`CLAUDE.md` §13]. Clean edit under D-73, prior
wording verbatim below. One cell, and `git diff docs/ARCHITECTURE.html` shows one line.

**The component gained a second write and the cell named one.** C03 writes
`fundamental_fetch_attempt` as well as `fundamental_snapshot`: the rotation had stopped
rotating once coverage completed, and the store that keeps it going is a record of the
attempt rather than of the result. Removed from the Writes cell:

> fundamental_snapshot

**`SCHEMA.md` gained the table in the same commit as its migration** and needs no entry
here, having no prior wording to quote. It is a new `### fundamental_fetch_attempt`
heading declaring FundamentalsIngestor as its writer, which is what
`WriteOwnershipConformanceTests` reads and what D-77 requires of any second writer.

**What this entry is evidence of, beyond the edit.** The Writes column has no
conformance test, where the Reads column gained one at the post phase 1 reconciliation
after four deviations had gone unnoticed. Write ownership is asserted against
`SCHEMA.md` rather than against the catalogue, so §03's Writes cells are checked by
nobody, and this one drifted at the first opportunity it had. Recorded as a finding in
`PROGRESS.md` rather than fixed here.

---

## 2026-08-11, D-92 to D-95

Phase 3's four decisions authored from the plan at
`prompts/BuildPlans/phase-3-backfill.md` §3, and `BUILD_PLAN.md` phase 3 gaining its
eighteen checkpoints, an amended fourth done-when line and eleven carried-obligation
landings. Human-directed transcription; the decisions were authored before the plan was
built against [`CLAUDE.md` §13].

**`BUILD_PLAN.md`'s fourth done-when line for phase 3, clean edit, prior wording verbatim
below.** The line was conditionally unsatisfiable and did not say so. Byte identity holds
for compute over a store whose ingest has not moved, and does not survive a re-ingest of
fundamentals, because `filing_date_effective` is computed at ingest from the ticker's
widest clean gap known at that moment and a widest gap only grows [D-62's stated
limitation]. The replacement states both halves. Removed:

> a replay of one historical date produces byte-identical output to the first run

**Nothing else was removed.** The checkpoint table is new, and the eleven obligation rows
gained a landing appended to text that is otherwise unchanged, so neither has a prior
wording to quote.

**`BUILD_PLAN.md` is not one of D-73's four clean-edit documents** and its existing
supersessions are struck in place, so this file now carries both conventions. The clean
edit was directed rather than derived, and the inconsistency is recorded here rather than
resolved, because which convention `BUILD_PLAN.md` follows is authored and this entry is
not the place to decide it.

**No code and no schema.** D-92's `security_daily`, D-95's `flow_fetch_attempt` and
D-93's `IBackfillStage` are all declared here and built at the checkpoints that name
them, so `SCHEMA.md` gains nothing yet: a declaration ahead of its column fails a parity
check for a column that is correctly absent.

---

## 2026-08-11, D-73 amended

D-73 classified by name where it had to classify by role. Human-directed. Nothing was
removed from a spec document, so there is no prior wording to quote here; the amendment
is in `DECISIONS.md`, which is the record and keeps its strikes.

**What was wrong.** `ARCHITECTURE.html`, `SCHEMA.md`, `CONFIG_REFERENCE.md` and
`RUNBOOK.md` were named individually as the documents that take clean edits, and
`DECISIONS.md` and `PROGRESS.md` as the two that keep strikes. `BUILD_PLAN.md`,
`METRICS.md` and `SCREEN_LIFECYCLE.md` were in neither list, so every document written
after the decision had to be classified by whoever next edited it, and one of them was
classified twice: `BUILD_PLAN.md`'s earlier supersessions are struck in place and phase
3's fourth done-when line is a clean edit.

**What replaces it.** The rule is now a test on what a document is read for, stated in
two sentences that name no document, so a document written later classifies itself. A
document read to know the current state takes clean edits; a document that is the record
keeps its strikes. The six names stay as examples of the test rather than as the
definition of it, and runtime prompt files remain clean deletions for their own reason.

**`BUILD_PLAN.md` is a spec under the test** and takes clean edits from here. Its eight
existing strikes stay, because D-73's condition requires confirming that each strike's
decision names what it removed before it can go, and three of the eight cite a
correction pass rather than a decision. Recorded as open item 11 in `PROGRESS.md`.

**This entry corrects the one above it.** The `D-92 to D-95` entry says "`BUILD_PLAN.md`
is not one of D-73's four clean-edit documents" and closes by leaving the convention
question open. Under the amended decision that sentence is false and the question is
answered. The earlier entry is not edited, this file being appended to and never
rewritten, and the correction lives here where a reader reaching the end finds it.

**`CLAUDE.md` §13 carries the same list and now disagrees with the register.** It is
human-edited only [`CLAUDE.md` §13], so it is reported rather than changed. Its
"Supersede visibly, never silently" paragraph names the same four and the same two as
the definition, and it needs the same amendment for the corpus to state one rule.

---

## 2026-08-11, checkpoint 3.2, `security_daily`

`SCHEMA.md` is a spec under D-73, so `security`'s section takes a clean edit and the
prior wording is recorded here. D-92 moves four columns to a new per-date table.

**`SCHEMA.md` §Reference, `security`'s column list and the sentence under it.**
Removed:

> `ticker`, `name`, `sector`, `size_bucket`, `market_cap`, `first_seen`, `last_seen`,
> `delisted_date`, `is_active`.
>
> Size buckets: large-and-above at $10B or more, mid $2B to $10B, small $300M to $2B.

The list is replaced by `ticker`, `name`, `first_seen`, `last_seen`, `delisted_date`,
which is identity and lifespan. The size-bucket sentence is not deleted but moved: it
belongs to `size_bucket` and `size_bucket` is now a `security_daily` column, so it is
restated verbatim in that section. The `delisted_date` sentence and the whole clean-gap
paragraph are unchanged and stay where they were.

**Nothing else was removed.** `security_daily`'s section is new, so it has no prior
wording to quote.

**The four columns are still physically on `security` and 0007 drops nothing.**
Checkpoint 3.2 enumerates what the migration does and a drop is not in it, and its
done-when takes `ExpectedMonetary` from 18 to 19, which holds only while
`security.market_cap` is still counted. A clean edit to this document is not a claim
that a column is gone: `SCHEMA.md` says in its own opening that its column lists are
the load-bearing ones rather than exhaustive. What is left is four columns nothing
writes after 3.11 and nothing reads after 3.12, recorded as a finding in `PROGRESS.md`
rather than dropped here, because dropping them moves two asserted counts.

---

## 2026-08-12, §3's C05 Writes cell

`ARCHITECTURE.html` is a spec under D-73, so this is a clean edit and the prior wording
is recorded here. Human-directed.

**§3's catalogue, the C05 Writes cell.** D-95 and migration 0008 give FlowIngestor a
third write and the cell named two. Removed:

> insider_transaction, institutional_holding [D-61]

It now names `flow_fetch_attempt` as well, with D-61 kept beside the two tables it
decided and D-95 at the point of change. `git diff docs/ARCHITECTURE.html` touches that
cell and nothing else.

**Nothing else in the row moved.** The Runs cell still reads `Daily 17:45` [D-75] and
the Reads cell still names the insider and ownership endpoints and `security` [D-58,
D-74]. `flow_fetch_attempt` is deliberately absent from the Reads cell: a stage may read
what it writes, which is what `DeclaredAccess.CanRead` says and what C03's cell already
relies on.

**This is the second Writes-cell drift in two days.** C03's was corrected under D-91 on
2026-08-11 and C05's is corrected here. The finding underneath both is that the Writes
column has no conformance test, where the Reads column gained one after four deviations
went unnoticed. That finding predicted the next instances would be C14 in phase 4 and
C22 in phase 8; it has been confirmed twice before either, which moves the test from
worth building to overdue [`PROGRESS.md`, the fundamentals rotation].

---

## 2026-08-12, §3's C01 Writes cell and §16's store list

`ARCHITECTURE.html` is a spec under D-73, so the removals below are clean edits and the
prior wording is here. Human-directed. Every store named is already in a migration and
in `SCHEMA.md`; the architecture was behind them.

**§3's catalogue, the C01 Writes cell.** D-92 and migration 0007 give UniverseBuilder a
second write and the cell named one. Removed:

> security

It now reads `security, security_daily` with D-92 at the point of change. **This is the
third component to drift at the moment it gained a second write**, after C03 under D-91
and C05 under D-95, and it is recorded in the existing Writes-conformance finding rather
than as a new one.

**§16's store matrix gains four rows, and they supersede nothing.** A purely new row has
no prior wording to quote, so what is recorded here is that they are additions and which
they are:

- `security_daily`, ticker × date, 100 MB [D-92, migration 0007]
- `fundamental_fetch_attempt`, one per ticker, tiny [D-91, migration 0006]
- `sentiment_derived_daily`, ticker × day, 200 MB [D-78, migration 0004]
- `flow_fetch_attempt`, one per ticker, tiny [D-95, migration 0008]

**The sizes are derived from the grain and are estimates**, as every figure in that
column is. The two attempt records are one row per ticker and are tiny by construction.
`security_daily` is ticker by weekly evaluation date, roughly 741,000 rows over the
window, and 100 MB is the figure `SCHEMA.md` already carries for it.
`sentiment_derived_daily` is ticker by day with six real columns, so it sits between
`flow_daily` at 52 MB and `valuation_daily` at 540 MB on column count and lands near
200 MB. `SCHEMA.md` calls that store "Small", which is a word §16 uses for
low-cardinality stores and not for ticker-by-day ones; the divergence is recorded in
`PROGRESS.md` rather than resolved by changing a figure neither document measured.

**§16's `screen_evaluation` row gains a marker rather than losing the row.** It is the
one store §16 names that `SCHEMA.md` does not declare, its migration being phase 4's.
Removed from the Store cell:

> <b>screen_evaluation</b> <span class="tag new">NEW</span>

It now carries `NOT YET IN SCHEMA` beside the existing tag. The new test reads that
marker rather than holding an exclusion list of its own, which is where D-83 and D-76
put a rule like this, and it fails if a store carrying the marker turns out to be
declared after all, so the marker cannot outlive the condition it records.

**Two paragraphs are added under the matrix**, stating that the list is now held against
`SCHEMA.md` in both directions and what the marker means. Neither replaces existing
prose; the D-76 paragraph above them is unchanged.

---

## 2026-08-12, size leaves `SCHEMA.md`

`SCHEMA.md` is a spec under D-73, so these are clean edits and the prior wording is
here. Human-directed.

**Size after backfill is §16's column.** A size word in a `SCHEMA.md` heading was a
third statement of a fact already held in §16 and derivable from the grain the same
heading states, which is the duplication D-76 exists to remove. The trigger was one
disagreement, `sentiment_derived_daily` reading "Small" against §16's 200 MB, and the
answer is the rule rather than the correction: **33 of the 35 table headings carried
one and all 33 are gone.** `security` and `local_model_config` never had one.

**§16 is unchanged by this.** Its figures stay the estimates they are, including the
200 MB that started this.

**The preamble sentence goes with them.** Removed:

> **Sizes are estimates after a five-year backfill, not measurements.**

It qualified figures this document no longer states, and a rule about a thing that is
not there is the same drift in miniature. What replaces it says where size lives and
that the §16 test holds the store list and not the size column.

**The 33 removals, verbatim.** Each was the trailing sentence of a heading's opening
paragraph, after the writer declaration. Thirty were at the end of a line; three had
wrapped onto a line of their own and that line is gone, `insider_transaction`,
`institutional_holding` and `dossier`.

| Table | Removed |
|---|---|
| `security_daily` | ~100 MB. |
| `price_daily` | ~400 MB. |
| `fundamental_snapshot` | ~60 MB. |
| `fundamental_fetch_attempt` | Tiny. |
| `flow_fetch_attempt` | Tiny. |
| `sentiment_daily` | ~380 MB. |
| `sentiment_derived_daily` | Small. |
| `headline` | Small. |
| `insider_transaction` | Small. |
| `institutional_holding` | Small. |
| `flow_daily` | ~52 MB. |
| `events` | Small. |
| `indicator_daily` | ~1.2 GB, the second largest table. |
| `valuation_daily` | ~540 MB. |
| `market_context_daily` | Small. |
| `gate_result` | ~120 MB. |
| `screen_score_daily` | ~1.4 GB, the largest table. |
| `screen_history` | Small. |
| `candidate_set` | ~9 MB. |
| `attribution` | ~9 MB. |
| `news_digest` | Grows ~10 MB/yr. |
| `dossier` | Grows ~40 MB/yr. |
| `proposal` | Grows ~30 MB/yr. |
| `portfolio` | Tiny. |
| `portfolio_selection` | Small. |
| `order / fill / position` | Small. |
| `trade_outcome` | Small. |
| `config_rows` | Tiny. |
| `researcher_memory` | Small. |
| `calibration` | Small. |
| `run_log` | Small. |
| `cost_ledger` | Small. |
| `alert` | Small. |

**Two of them said something §16 does not.** `indicator_daily` was "the second largest
table" and `screen_score_daily` "the largest", which is a ranking rather than a size and
is not carried across: §16 states both figures and the ordering is read off them. D-84's
note under the matrix already makes the point about `screen_score_daily` at length.

**Nothing else in the paragraphs moved.** The grain, the writer declaration and every
decision citation are untouched, which is what `SchemaDocument.WritersByTable` and
`SchemaDocument.Tables` parse; both still read 37 tables and the five conformance tests
over this document pass unchanged.

---

## 2026-08-12, D-96 and D-97, the earnings store and the sector column

Two authored decisions and the store they create. `ARCHITECTURE.html` and `SCHEMA.md`
are specs under D-73, so the two catalogue cells below are clean edits and the prior
wording is here. Human-directed.

**§3's C03 Writes cell.** D-96 gives FundamentalsIngestor a third write. Removed:

> fundamental_snapshot, fundamental_fetch_attempt [D-91]

It now names `earnings_history` as well, with D-91 kept beside the two tables it decided
and D-96 at the point of change.

**§3's C09 Reads cell.** D-96 gives ValuationEngine the store that feeds
`last_two_earnings_surprises`, a column it has written null since 0001. Removed:

> `price_daily`, `fundamental_snapshot`

**§16's store matrix gains one row**, `earnings_history` at ticker by fiscal period and
30 MB. A purely new row supersedes nothing, so it has no prior wording and is recorded
here as an addition. The store-list conformance test's count moves from 38 to 39.

**`SCHEMA.md` gains `earnings_history`'s section and a `sector` column on
`fundamental_snapshot`.** Both are additions. Three `real` columns are declared as not
money: `eps_actual` and `eps_estimate` are per-share figures rather than monetary
totals, and `surprise_fraction` is a ratio. The monetary count is unchanged at 19,
`sector` being text.

**`BUILD_PLAN.md`'s `1 → 3` carried obligation, closed at 3.7.** A clean edit under
D-73, `BUILD_PLAN.md` being a spec under that test. Removed:

> | 1 | 3 | `FundamentalsIngestor` does not read `events`, so earnings do not jump the
> rotation queue. `ARCHITECTURE.html` §3 gives C03 `events` in Reads and the code
> declares `price_daily` and `security` only. The read conformance test added at the
> post phase 1 reconciliation carries this as its one recorded deviation and fails the
> moment it stops being one, so it cannot go stale. The catalogue is right and the code
> is incomplete [D-74], which rules out both available shortcuts: declaring `events`
> without reading it makes the declaration mean nothing, and removing it from the
> catalogue is editing an authored document to match what was built. `events` has been
> built since 1.8, so nothing blocks the work. Owed to phase 3 because that is the next
> phase to run ingest, and a fixed rotation head leaves every name outside it unread
> indefinitely, which is a coverage property backfill depends on. **Lands at 3.7**,
> with the full pool sweep |

C03 now reads `events` for the names that reported inside
`events.earnings_backward_days` and ranks them above staleness in the shared rotation.
`ReadDeclarationConformanceTests` carried it as its one recorded deviation and **failed
the moment it stopped being one**, which is how the closure was found rather than
remembered; that list is now empty.

**Why the sector column is not on `security`**, recorded because it is the option a
reader would expect. It would make C03 a second writer on the table C01 owns, requiring
a split declaration [INVARIANT 10, D-77]; it would carry one sector per ticker, which is
today's sector applied to every historical date and the exact defect `security_daily`
was created to remove; and it would put a weekly stage's read behind a nightly stage's
write. The full reasoning is D-97.

---

## 2026-08-12, D-98, the holders capture moves to C03

`ARCHITECTURE.html` and `SCHEMA.md` are specs under D-73, so the three edits below are
clean and the prior wordings are here. Implementing an authored decision.

**§3's catalogue, the C03 Writes cell.** D-98 gives FundamentalsIngestor a fourth
write. Removed:

> fundamental_snapshot, fundamental_fetch_attempt [D-91], earnings_history [D-96]

It now names `institutional_holding` as well, with D-91 and D-96 kept beside the tables
they decided and D-98 at the point of change.

**§3's catalogue, the C05 Writes cell.** The same table leaves it. Removed:

> insider_transaction, institutional_holding [D-61], flow_fetch_attempt [D-95]

D-61 decided both source tables at their natural grain and stays beside the one that
remains here, with D-98 alongside it as the decision that made form 4 the only source
this component ingests. `git diff docs/ARCHITECTURE.html` touches those two cells and
nothing else, two lines of a 1,000-line file.

**`SCHEMA.md`'s `institutional_holding` writer declaration.** Removed:

> Grain: ticker by holder by report date, the source's own. **Writer: FlowIngestor.**

The grain is unchanged and the writer is now `FundamentalsIngestor`, which is what
`WriteOwnershipConformanceTests.EveryWritingComponentIsNamedAsAWriterInSchemaDocument`
reads: it failed on the code change alone and passes on this one, so the two statements
are held against each other rather than maintained in parallel. A sentence is added
below the column list saying the block rides C03's call and that no sweep re-fetches
it, which is what stops a reader looking for the endpoint that populates this table.

**§16's store matrix does not change.** No table is added or removed and the Written-by
column left that table at D-76, so write ownership is stated in §3 and in `SCHEMA.md`
and nowhere else. The store-list conformance test's count stays at 39.

**The Reads cells did not move and one of them is now stale.** C05's still names the
ownership endpoint, which the component no longer calls. That is an endpoint rather
than a table, so `ReadDeclarationConformanceTests` neither sees it nor could: it
intersects the cell against `SCHEMA.md`'s table list and drops everything else, exactly
as it drops `digest_provider` from C29's. Recorded as open item 20 rather than
corrected here, the file being human-edited only and this change's scope being the two
Writes cells [`CLAUDE.md` §13].

**No provider call was made and no units were spent.** The measurement D-98 rests on
was taken on 2026-08-12 at 40 units and is not repeated; the regression test added with
this change reads the captured block off disk and covers our parse rather than the
provider's response [`FIXTURES.md`, the unfiltered `Holders` block].

---

## 2026-08-12, D-98's two open items closed

`ARCHITECTURE.html` is a spec under D-73, so the edit below is clean and the prior
wording is here.

**§3's catalogue, the C05 Reads cell.** D-98 stopped the component calling the
ownership endpoint and the cell went on naming it. Removed:

> Insider, ownership [D-58], `security` for the universe it iterates [D-74]

D-58 dropped short interest from the flow screen and stays beside the endpoint that
remains, with D-98 alongside it as the decision that removed the other. `git diff
docs/ARCHITECTURE.html` touches that cell and nothing else, one line.

**Nothing else in the row moved.** The Runs cell still reads `Daily 17:45` [D-75] and
the Writes cell still names `insider_transaction` and `flow_fetch_attempt` [D-61, D-95,
D-98].

**Why no test caught it, recorded rather than built.**
`ReadDeclarationConformanceTests` intersects the Reads cell against `SCHEMA.md`'s table
list and drops whatever does not match, which is what lets it read `security` out of
"for the universe it iterates" and what makes it drop `digest_provider` from C29's. The
consequence is that the half of every Reads cell naming endpoints is checked in neither
direction, inside the test that has caught two things this week. That is the same
unchecked-column shape as the Writes column and it is recorded beside it in
`PROGRESS.md`, so whoever builds one sees the other.

**`institutional_holding` gains no schema change and no document change.** The
duplicate-key guard landing with this is code and tests only: the grain, the writer
declaration and the column list are what they already were.

---

## 2026-08-12, the backfill driver

No spec document changed. `CONFIG_REFERENCE.md`'s Consumer column moved from NOT BOUND
to a named component for six backfill keys, which is verified content rather than a
supersession: each was confirmed by reading the line that consumes it, at
`PriceIngestor.cs:108-111`, `FundamentalsIngestor.cs:88-94` and `Program.cs:175`. The
four still reading NOT BOUND are sweep weights for checkpoints not yet built.

`ARCHITECTURE.html`, `SCHEMA.md` and `RUNBOOK.md` are untouched. The driver is a Worker
command over `BackfillRun`, which §3 already accounts for, and it adds no component, no
table and no config key.

---

## 2026-08-12, range-scoped resumption

`RUNBOOK.md` is a spec under D-73 and this is an addition rather than a supersession, so
nothing is removed. Its Backfill section gains a "Running an ingest sweep" subsection
before the existing two-pass text, which is now under "The two-pass screen build". The
new text says to pass both dates for a multi-day sweep, what each exit code means, and
what to do when a refusal names a row. The reason it has to be stated is that `to`
defaults to today and today moves at midnight.

**No other document changed.** `ARCHITECTURE.html`, `SCHEMA.md` and `CONFIG_REFERENCE.md`
are untouched: no component, table, column or config key moved. The rule is a change to
how `BackfillRun` reads a `run_log` row it already read.

**One `run_log` row was deleted from the developer database**, id 1311, and its text and
consequence are in `PROGRESS.md` rather than here, because it was data rather than a
document and the record has to sit where the finding does.

---

## 2026-08-12, the failed sweep, the frontier position and the connection retry

`RUNBOOK.md` is a spec under D-73 and this is an addition, so nothing is removed. Its
"Running an ingest sweep" subsection gains three paragraphs, on what a failure records,
on the connection retry and its count, and on why a per-ticker write failure is not
tolerated. The last of those sits beside the two exceptions the failure table already
enumerates, so the absent third reads as deliberate rather than forgotten.

**No other document changed.** No component, table, column or config key moved. The
retry's attempt count and backoff are constants in the data layer rather than config
keys, on the precedent the transport already sets: the rate limiter's 1,000 a minute
and the HTTP handler's pooled-connection lifetime are constants too, and config in this
system is for values the experiment turns on [`CLAUDE.md` §8].

**A hypothesis was refuted rather than acted on, and the measurement is in
`PROGRESS.md`.** The instruction to establish why a physical connection was being
established per ticker rested on a sound reading of the stack. It is not what happens:
7 physical opens over 402 tickers at a worker count of 8. Nothing was changed about
pooling, because there was nothing wrong with it.
