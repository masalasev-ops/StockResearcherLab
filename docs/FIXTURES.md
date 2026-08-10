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
| Two form 4 lines identical on every attribute | 1.7 | One filing, one owner, one code, one date, one share count and two genuinely different lines, reduced from the measured collision: an option exercise reported as Common Stock acquired and as Restricted Stock Units disposed. A7's proposed key collapses them into one and the row count still looks right, which is how D-68 fails silently | `FlowIngestorTests.TwoLinesIdenticalOnEveryAttributeAreStillTwoRows` |
| Global earnings calendar with an off-exchange row | 1.8 | A `.BSE` symbol beside the US ones. Nothing in the request narrows `calendar/earnings` to an exchange, so a stage that does not narrow writes 22,286 rows across every market the provider carries and every one of them looks like a real event | `EventsIngestorTests.AnExchangeOutsideTheUniverseIsDropped` |
| Bulk event row keyed by code and exchange separately | 1.8 | `{"code":"NVDA","exchange":"US"}` where the calendar sends `NVDA.US`. A parser reading `code` alone matches nothing against the universe and writes no rows while the stage succeeds | `EventsIngestorTests.TheBulkFeedsTickerIsAssembledFromCodeAndExchange` |
| Dividend payload carrying four dates | 1.8 | Ex-date, declaration, record and payment, of which exactly one is the event and one is the announcement. The other two are the ones a reader reaches for by name | `EventsIngestorTests.ADividendKeepsTheExDateAndTheDeclarationDateAndDiscardsTheOtherTwo` |
| Six insider filings of which three count | 1.8 | One ticker with a purchase inside the window, a sale inside it, a purchase before it, a purchase filed after the run date, and an option exercise. The reference answer is hand-computed in the test, so a change to the statement that alters a number fails rather than passing with a different one | `FlowEngineTests.TheThreeMetricsReproduceTheHandComputedReference` |
| Open-market row with no dollar value | 1.8 | A sale with neither a total nor a price. Dropping it from the sum is adding zero, and zero means insiders traded and netted out | `FlowEngineTests.ATickerWithAnUnpricedOpenMarketRowCarriesNullRatherThanAnUnderstatedTotal` |
| Ticker with no ingested flow at all | 1.8 | Never through the rotation, against one with holdings but no filings and one with filings but no purchases. Three different facts that a zeroed row would render identical | `FlowEngineTests.ATickerWithNoIngestedFlowGetsNoRowRatherThanZeros` |
| Institutional report inside its filing lag | 1.8 | A report dated 2026-06-30 read on the 13th and 14th of August, either side of the forty-five day 13F deadline. Reading on `report_date` alone is INVARIANT 12's mistake arriving through the other table with the same shape | `FlowEngineTests.AnInstitutionalReportIsNotReadableUntilTheFilingLagHasPassed` |
| Paged read that stops while the endpoint offers more | 1.8 | An endpoint claiming 100, offering a next link throughout, and serving nothing from page three. The client stopping with a successor in hand is the client failing to ask, so it stays fatal [D-71] | `EodhdClientTests.APagedReadThatStopsWhileTheEndpointStillOffersMoreFails` |
| Paged read the server ran out of | 1.8 | The same fixture that used to prove a throw, now proving a recorded shortfall. The endpoint stops offering a next link with rows still owing, which is the provider disagreeing with itself and cannot be recovered by asking again [D-71] | `EodhdClientTests.APagedReadTheServerRanOutOfIsRecordedRatherThanThrown` |
| Short page before the last | 1.8 | AAON.US reduced: a page that comes back short mid-sequence puts the missing rows inside the history where a trailing-90-day metric reaches them, where a short final page puts them at the oldest end [D-71] | `EodhdClientTests.AShortPageBeforeTheLastPutsTheMissingRowsInsideTheHistory` |
| Config store where the key that changed is not the highest-versioned one | sign-off | Three keys at versions 3, 1, 1, then the key at 1 moves to 2. A store-wide version taken as `MAX(version)` answers 3 both times, so two different configurations carry one stamp and the tuner pools what it exists to separate. The test asserts the version moves and, alongside it, that the maximum does not [D-72] | `ConfigResolutionTests.ChangingAKeyOtherThanTheHighestVersionedOneStillMovesTheStoreWideVersion` |
| Key seeded by a later phase, backdated as the seeder backdates | sign-off | A new key at version 1 stamped `2020-01-01`, which is what every phase's seeding does. Counting rows rather than revisions would raise the store-wide version for every date from the window start onward, and a backfill re-run would stamp a different version on identical data. The test asserts four prior dates are unmoved and, alongside it, that a row count would have moved [D-72 as amended] | `ConfigResolutionTests.SeedingAnAdditionalKeyLeavesEveryPriorDatesVersionUnchanged` |
| Stage disagreeing with its Reads cell, both ways | reconciliation | Two fabricated read sets against the real catalogue: `PriceIngestor` declaring `price_daily` where its cell is one bulk endpoint, and `FlowEngine` declaring nothing where its cell names two tables. Each direction is asserted to fire and the other to stay silent, because both would pass vacuously if the catalogue parse returned an empty map and neither failure would look like one [D-74] | `ReadDeclarationConformanceTests.BothDirectionsFailOnAStageThatDisagreesWithTheCatalogue` |
| Rotation pool name that returns no rows | sign-off | A selected ticker that writes nothing, standing for the 14 of 250 answering `404 Symbol not found`. It stays never-fetched and is offered again on the next pass, which is the residue a high-water mark would close and is asserted so it is a known property rather than a surprise | `FlowIngestorTests.ANameThatReturnedNoRowsIsOfferedAgainRatherThanSkipped` |

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
- An `events` row whose `event_date` was not yet public on the date it is read
  back for, which is the earnings calendar's point-in-time gap and has no fixture
  because the source carries no date on which a schedule was announced
  [BUILD_PLAN carried obligations, 1 owed to 5]
- A night where the primary digest provider is unavailable and the chain falls
  through [phase 5]
- A night where no digest provider is healthy and the run halts [phase 5]
- A candidate surfaced by two screens, receiving two screen blocks [phase 6]
- An acquired candidate with no 63-day price series [phase 8]
- A delisted candidate [phase 8]
- A full-abstention night, asserting exposure matching holds at zero [phase 7]
