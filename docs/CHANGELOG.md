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
