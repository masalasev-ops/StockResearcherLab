# Phase 0 — Rails

    Target:    Claude Code
    Issued:    2026-08-06
    Status:    SPENT
    Produced:  six source projects plus tests, the Postgres schema and
               migrations, the stage abstraction and registry, run logging and
               the write-ownership conformance test, the Api isolation test, the
               bare run viewer, one no-op stage end to end, CI, branch
               phase-0-rails, pull request 1.
               SPENT means executed, not signed off.

Record of what was asked. Never edited once run.

---

PHASE 0 — RAILS

Build it. Do not review the corpus, do not sweep for consistency, do not produce
a plan document, and do not report that phase 0 is next. Work checkpoint by
checkpoint, one commit each, message opening with the number. Branch phase-0-rails.

Read first: CLAUDE.md in full, BUILD_PLAN.md phase 0, SCHEMA.md, and
CONFIG_REFERENCE.md. ARCHITECTURE.html §3 and §16 for component names and write
ownership. Nothing else.

Authored content is not yours to write. If a document is wrong, missing or
self-contradicting, report it in the commit body and keep building. Do not
close it, do not amend it, and do not stop for it unless the checkpoint is
genuinely unbuildable without a ruling, in which case stop and ask.

--- 0.1  Solution layout

.NET 10, `.slnx` solution format. Project layout per CLAUDE.md §4 and D-56.
`Directory.Build.props` and `Directory.Packages.props` for central package
management, with `TreatWarningsAsErrors` on. `.gitattributes`. `guards.ps1` as a
stub that exits zero and carries a comment naming the greps it will run.
`migrate.ps1` and `seed.ps1` at the root, stubs for now.

The existing `tools/probe` is phase P only and is deleted or rewritten afterwards
[CLAUDE.md:213]. Delete it in this checkpoint. Its measurements live in
PROGRESS.md and nothing depends on its code.

Test / DoD: `dotnet build StockResearcherLab.slnx` is green from a clean clone.
Every project named in CLAUDE.md §4 exists and is in the solution.

--- 0.2  Schema and migrations

Postgres schema and migrations generated from SCHEMA.md, snapshot-first. Every
table declared there, at the grain stated, with money as native `numeric` and
never float or double [INVARIANT 16]. Includes `portfolio_selection`.

Test / DoD: `migrate.ps1` runs clean against an empty database and is idempotent
on a second run. A test asserts every table in SCHEMA.md exists and every table
in the database is in SCHEMA.md, in both directions.

--- 0.3  Stage abstraction and registry

A stage declares its read set, its write set per operation, and takes a date and
a config version. Stages are pure: same inputs, same outputs, no ambient clock
[INVARIANT 11]. The registry is the single declaration of who touches what.

Test / DoD: a stage that reads a table it did not declare fails at runtime with a
named error rather than silently succeeding. Write the test that proves it.

--- 0.4  Run logging and the conformance test

Run logging into `run_log` per SCHEMA.md. Then the conformance test: no two
components claim the same component-table-operation triple, asserted against the
per-operation declarations in SCHEMA.md [INVARIANT 10 as amended].

The three declared splits are `attribution`, `proposal`, and the
`order`/`fill`/`position` group. The test permits those and nothing else.

Test / DoD: the test passes over the real registry. Then add a deliberately
conflicting stage in a test fixture and prove the test fails on it. A conformance
test that has never failed has not been tested.

--- 0.5  The read-only guarantee, structurally

A test asserting the `Api` project does not reference `Pipeline`, by inspecting
the project references rather than by convention.

Test / DoD: the test fails if the reference is added. Prove it by adding it
temporarily and showing red, then removing it.

--- 0.6  Bare run viewer

Stages, durations, row counts. Not the run health screen. Enough to debug a
thirty-four component pipeline without raw SQL. An afternoon, not a week.

Test / DoD: a run appears in it with per-stage durations and row counts.

--- 0.7  One no-op stage end to end

A single stage that declares a read set and a write set, does nothing
meaningful, runs, logs, and appears in the viewer.

Test / DoD: the stage runs from the command line, writes its `run_log` row, is
visible in the viewer, and passes the 0.4 conformance test.

--- 0.8  Open the two documents and bump

Open `FIXTURES.md` for real by registering any fixture this phase created, and
append the corpus version bump to `CHANGELOG.md` per the sign-off procedure.

Test / DoD: the corpus version matches in PROGRESS.md and the newest CHANGELOG
entry, and README still names none [N.5].

--- AFTER

Print HEAD and the branch commits. State phase 0's Done when against what was
built: a no-op stage runs, logs, and appears in the viewer; the write-ownership
test reads the registry and passes; migrations run clean from empty. Do not sign
off. Do not merge unless asked.
