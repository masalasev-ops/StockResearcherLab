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
