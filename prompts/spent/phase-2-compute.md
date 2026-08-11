# Phase 2 — Compute, the prompt issued to the second build session

    Target:    Claude Code
    Issued:    2026-08-11
    Status:    SPENT
    Archived:  after the build, which is the wrong time and is recorded as such.

**Archived at the end, not before, and that is a repeat of phase 1's finding rather
than a new one.** `CLAUDE.md` §3 says a prompt is archived before code, and that a
prompt archived at the end is archived after the session has already learned things.
This session wrote 2.7, 2.8, 2.10 through 2.13 and two corrections before archiving
anything, and only did so when asked where the phase 2 spent prompt was. It is
verbatim, but it is late, and a reader should weigh it knowing the session that filed
it is the session it describes. `PROGRESS.md` records the drift against phase 2.

**This is one of at least two prompts phase 2 was issued and the other is not here.**
Checkpoints 2.1 through 2.6, 2.9, 2.14 and 2.15 were built by an earlier session
whose prompt was never archived and is not recoverable from this one: no transcript of
it exists in the repository and reconstructing it would be a paraphrase, which is the
one thing a spent prompt must not be [D-63]. What survives of that session's reasoning
is its commit bodies and `prompts/BuildPlans/phase-2-compute.md`, and neither is the
prompt.

**`prompts/BuildPlans/phase-2-compute.md` is not a spent prompt and does not close
this gap.** Its own commit body says so: it was archived "under prompts/BuildPlans
rather than prompts/spent: a spent prompt records what was issued, and this is the
plan derived from it".

**Verbatim.** Nothing below is summarised, reordered or corrected. The prompt as
issued, including its formatting.

---

# Phase 2 — Compute, continuing from 2.6

You are picking up phase 2 mid-flight. Nothing here is blocked.

## Read first, in this order

1. `CLAUDE.md`.
2. `prompts/BuildPlans/phase-2-compute.md` — the plan, its §3 blockers now all closed.
3. `docs/METRICS.md` — every formula, window, warm-up and null rule. Checkpoint 2.1
   produced it and it is still a draft awaiting authoring; the PROPOSAL entries mark
   every reading taken where no document decides.
4. `git log --oneline origin/main..phase-2-compute` and the bodies of those commits.
   **`PROGRESS.md` has not been touched yet — it still records phase 2 as NOT STARTED.
   That is checkpoint 2.13's scope. Until it is done, the commit bodies are the record,
   not that file.**
5. `docs/DECISIONS.md`, D-77 through D-80, all authored during this phase.

## State

Branch `phase-2-compute`, pushed. `ci.ps1` green at `d66595c`, 173 tests. Migrations
`0004` and `0005` applied. `ConfigSeeder.Keys` holds 32.

Done: 2.1 metric reference, 2.2 migration `0004` and its declarations, 2.3 D-77 applied,
2.4 config keys, 2.5 and 2.6 C08 IndicatorEngine complete at fifteen columns, 2.9 C10
MarketContextEngine, 2.14 the C35 catalogue row, 2.15 the C10 and C11 Reads cells.

Remaining: **2.7 C09 ValuationEngine**, **2.8 C35 SentimentEngine**, **2.10 C11
PercentileEngine**, then 2.11 conformance, 2.12 the nightly run, 2.13 recording.

## Three obligations that are already written down and must not be dropped

**2.7 closes D-79's open item.** `capital_expenditures` is ingested and its sign
convention is not known from anything in this repository — the phase P transcripts never
printed the field, checked by grep. `METRICS.md` assumes a positive magnitude, so
`fcf_yield` is `TTM CFO - TTM capex`. **Confirm the sign against real rows in
`fundamental_snapshot` before fixing the formula.** Backwards it doubles free cash flow
rather than halving it and nothing errors.

**2.10 carries D-77's fifth done-when line.** Re-running a metric engine after
PercentileEngine must leave every `_pctile` value byte-identical, with the test failing
if the staging table is ever widened to the full column set. This is the split's
characteristic failure and the registry does not catch it: it is safe today only because
`BulkUpsertSql.CreateStaging` builds from the written columns alone. Nothing asserts it.

**2.13 owes `PROGRESS.md` the whole phase**, including the indicator column count
actually built against the "roughly forty" estimate, and the measured fallback rate per
metric from 2.10's run log.

## Conventions this phase has established, so you do not re-derive them

- **Ticker-partitioned stages read once, compute in C#, write once** — C01's and C08's
  shape. **Date-partitioned stages are one set-based statement** — C34's. §19 decides
  which, and getting it backwards produces wrong numbers rather than slow ones. C11 is
  date-partitioned.
- **References are closed forms, not a second implementation.** See
  `IndicatorEngineTests`: Wilder's smoothing of a constant is that constant, so a flat
  series pins ATR exactly. Reproducing the algorithm in the test asserts only that two
  copies agree.
- **Every ratio is stored as a fraction**, one rule for all columns [`METRICS.md` §1.2].
- **Prices compared across dates use the adjusted series**, volume through the same
  factor. This is the failure most likely to pass silently.
- Register the stage in `PipelineComposition` unconditionally (compute stages call no
  provider), append to `NightlyRun.EveningOrder`, and move `ExpectedOwners` and
  `ExpectedStages`. They are 11 and 10 now.
- Commit per checkpoint as `Phase 2 / 2.n - what it did`. Run `ci.ps1` after committing,
  since it checks HEAD.
- New fixtures go in `FIXTURES.md` and nowhere else.

## Four traps that cost time this session

1. **Running the test suite seeds the dev database.** `ConfigResolutionTests` constructs
   a `ConfigSeeder` against `TestDatabase.ConnectionString`, which locally is the same
   database the Worker uses. So `seed` reports "nothing to seed" after any local test
   run, and a first seed after adding keys can report no work when work was done.
2. **`guards.ps1` reads `git ls-files`.** A new migration must be `git add`ed before
   running it, or the check reads a file CI will never see. It names the file when this
   happens.
3. **`dotnet test --no-build` runs against a stale binary after a failed build** and
   reports the previous pass count. Use `ci.ps1`, which cannot reach the test step after
   a build failure.
4. **The local dev database needs `migrate` after any new migration**, or
   `SchemaParityTests` fails in a way that looks exactly like the change under test
   breaking the parse.

## What is still authored and not yours

`ARCHITECTURE.html` and `CLAUDE.md` are human-edited only. If a build reveals one is
wrong, report it and continue — do not close it. Nothing currently outstanding blocks
2.7, 2.8 or 2.10.
