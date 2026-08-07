# FIXTURES.md

The single registry of test fixtures. Every fixture is registered here and referenced
from here.

~~**Do not enumerate fixture names in build plan detail or in `CLAUDE.md`.** A list
kept in two places goes silently incomplete the moment one is registered in the other,
and the copy that goes stale is the one nobody is looking at. A spent prompt may name
a fixture, because a prompt records what was asked rather than what is true.~~
[moved, N.10] What other documents may say about fixtures is stated once, in
`CLAUDE.md` §9. It was stated here as well, and the two copies diverged the moment one
of them was narrowed.

~~Nothing is registered yet. Phase 0 opens this file.~~ [opened, 0.8] Three fixtures
are registered below, all of them from phase 0's rails. Each exists to make a test
fail on purpose, because a conformance test that has never failed has not been tested.

## Registry

| Fixture | Registered | Purpose | Used by |
|---|---|---|---|
| Conflicting registry | 0.4 | Two components both claiming `run_log.Insert`, which is the case INVARIANT 10 exists to catch | `WriteOwnershipConformanceTests.TheConformanceTestFailsOnADeliberatelyConflictingRegistry` |
| Attribution split registry | 0.4 | `CandidateAllocator` inserting `attribution` and `ForwardReturnFiller` updating it, asserted NOT to be a conflict. The case a per-table rule gets wrong and a per-operation rule gets right | `WriteOwnershipConformanceTests.TwoComponentsOnTheSameTableWithDifferentOperationsIsNotAConflict` |
| Trespassing stage | 0.7 | A stage declaring `run_log` and reading `security`. Both tables exist, so without the guard it succeeds and returns rows | `StageRunnerTests.AStageThatReachesOutsideItsDeclaredSetFailsTheRunAndSaysSo` |
| Uncatalogued component | 1.11 | A registry naming `NoOpStage`, which `ARCHITECTURE.html` §3 does not have. The exact name that was registered and uncatalogued through all of phase 0, so the check is exercised against the case it was written for | `RegistryNameTests.AComponentTheCatalogueDoesNotNameIsCaught` |
| Column outside the declared set | 1.12 | A stage declaring three columns of `price_daily` and bulk-loading four. `TableWrite.Columns` was declared and asserted by nothing from phase 0 until this | `BulkUpsertTests.AStageWritingAColumnItDidNotDeclareThrowsBeforeAnythingOpens` |
| Conflict target the write does not supply | 1.12 | `ON CONFLICT` on a column the bulk load omits. Valid SQL that never fires, so every re-run inserts a duplicate while succeeding, which is D-68 silently not holding | `BulkUpsertTests.AConflictTargetOutsideTheWrittenColumnsIsRefused` |
| Paged set an exact multiple of the page size | 1.1 | 100 rows in four pages of 25, where the last full page is indistinguishable from a middle one. A loop terminating on a short page asks for one more; termination keys on `links.next` | `EodhdClientTests.PagingTerminatesOnTheAbsentNextLinkWhenTheTotalIsAnExactMultiple` |
| Paged read short of its reported total | 1.1 | An endpoint claiming 100 and stopping at 50. A short read that returns successfully looks identical to a complete one | `EodhdClientTests.APagedReadShortOfItsReportedTotalFailsRatherThanReturning` |
| Config date before every version | 1.13 | A resolution date earlier than every row's `set_at`. A resolver falling back to `MAX(version)` returns today's config for a historical date and passes every test that stays inside the configured range [INVARIANT 13] | `ConfigResolutionTests.ADateBeforeEveryVersionResolvesToNothingRatherThanTheNewest` |
| Bulk row dated other than requested | 1.2 | A feed returning a row for a different day than the one asked for. Writing it would file bars under a day they did not belong to, and no later stage could detect it | `PriceIngestorTests.ARowDatedOtherThanTheDateRequestedFailsTheStage` |

## Fixtures the design already calls for

These are named in other documents as things that must be provable. They become rows
above when they are written.

- The worked arbitration example in `ARCHITECTURE.html` §10, which must reproduce
  exactly [BUILD_PLAN phase 7]
- A prefix snapshot, asserting one hash across a full night of calls [phase 6]
- A dossier with a deliberately corrupted citation, which the validator must reject
  [phase 6]
- A proposal whose probability contradicts its own stop and target [phase 6]
- A stale end-of-day file, which the freshness guard must abort on [phase 1]
- A fundamental whose filing date is later than its period end, asserting no read
  before the filing date [phase 1]
- A fundamental whose filing date equals its period end [phase 1]
- A fundamental whose filing date is null [phase 1]
- A fundamental whose filing date precedes its own period end [phase 1]
- A ticker with fewer than four clean gaps, which the universe must exclude
  [phase 1]
- A night where the primary digest provider is unavailable and the chain falls
  through [phase 5]
- A night where no digest provider is healthy and the run halts [phase 5]
- A candidate surfaced by two screens, receiving two screen blocks [phase 6]
- An acquired candidate with no 63-day price series [phase 8]
- A delisted candidate [phase 8]
- A full-abstention night, asserting exposure matching holds at zero [phase 7]
