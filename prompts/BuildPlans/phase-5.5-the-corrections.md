# Phase 5.5 — The corrections

**Target** phase 5.5, the corrective pass between phase 5's sign-off and phase 6's first
checkpoint: two metric definitions the code contradicts, three guards that fail open, two
done-when lines that score a pass on no data, the enforcement that was named in an
invariant and is not in the file, and the records that stopped being true.

**Authored** 2026-09-23, after a sixteen-dimension conformance audit of `HEAD` `67cdc77`
against `ARCHITECTURE.html` and `CLAUDE.md` §2, every finding adversarially verified
against the lines a second time and against the record a third.

**Read against** `ARCHITECTURE.html` §02, §03, §04, §05, §06, §12, §13, §16, §18 and §19,
`METRICS.md` §1, §2, §5 and §6, `SCHEMA.md`, `RUNBOOK.md`, `CONFIG_REFERENCE.md`,
`DECISIONS.md` D-6, D-7, D-9, D-13, D-14, D-43, D-56, D-62, D-71, D-73, D-74, D-77, D-80,
D-83, D-84, D-93, D-95, D-100, D-115, D-117, D-120, D-129, D-136, D-137, D-138, D-139,
D-143, D-144 and D-145, `FIXTURES.md`, `VALIDITY.md` §3 and §4, `SCREEN_LIFECYCLE.md` §7.2
and §8.1, `WORKED_EXAMPLE.md` and `CLAUDE.md` §2, §3, §5, §6, §7, §9, §11, §12, §13 and
§14, at `HEAD` `67cdc77`.

**Nineteen claims below rest on the code, on `guards.ps1` or on a run rather than on a
document, and were read or taken there** [`CLAUDE.md` §7]: `ScreenConfigFacade`'s
`ScreenScoped` regex at `:54-55` and `CanRead`'s `!match.Success` return at `:79-83`, with
the pattern run through .NET against all eight registered ids rather than reasoned about;
`MarketContextEngine.SectorRelativeStrengthAsync`'s per-sector `list.Add(mean)` at `:413`,
its `universeLevel *= 1 + byDate[date].Average()` at `:420` and its emitted
`((level / universeLevel) - 1)` at `:445`; `IndicatorEngine.Wilder`'s `smoothedTr[t] <= 0`
guard at `:880-885` carrying the document's own justification and its `sum <= 0 ? 0` at
`:891`; `IndicatorEngineTests.TheFlatSeriesReproducesItsClosedForm`'s
`Assert.Equal(0f, row.Adx14!.Value, 5)` at `:46`; `ScreenRangeRun.FloorAsync`'s two
`RequireAsync(..., to, ct)` calls at `:169` and `:172` standing outside the session loop
that opens at `:177`; `ScreenEngine.RankSql` at `:368-384` carrying no arm that nulls a
rank; `ConfigStore.cs:583` seeding `digest.chain` v1 as `["local","haiku"]` and
`SeedChainAsync` inserting both rows with a literal `TRUE`, both under
`ON CONFLICT DO NOTHING`; `guards.ps1`'s five checks, its `INVARIANT 11` pattern at `:84`,
its `INVARIANT 6` pattern at `:100` and its `INVARIANT 16` entry at `:105-125` scoped to
`.sql`; `StageData.WriteAsync` at `:168` checking table and operation and not columns
against `BulkUpsertAsync` at `:217`; `StatementColumnConformanceTests.Statements()`
enumerating two components at `:41-50`; `NightlyRunTests` asserting `EveningOrder`
containment in one direction only at `:196-211`; `PersistenceMeasure.Number` at `:243-244`
and `ScreenPersistence`'s non-nullable lag fields; `SelectionDistributions`'
`NULLIF(count(*), 0)` at `:178`; `FIXTURES.md:55`'s citation of a method no longer in the
suite; `ci.ps1` green at `67cdc77` with **895 tests**, 23 migrations and 5 guard checks
over 213 files, run 2026-09-22; `dotnet build` at 0 warnings; and
`git rev-parse HEAD` at `67cdc77` with `git log --oneline -40`.

**Status** `DRAFT`. **The numbered checkpoints in §5 are phase scope and the clauses in §4
are decisions; both are authored into `BUILD_PLAN.md` and `DECISIONS.md` by a human rather
than from here** [`CLAUDE.md` §13]. Nothing below is authored by this document.

**This document is archived to `prompts/spent/phase-5.5-the-corrections.md` before any
code is written, and that archive is checkpoint 5.5.1's first act.** Phase 6's plan states
why in terms: phase 2 archived half of what was issued, phase 4 archived nothing, and
phase 5 archived only after fourteen checkpoints had landed. Three phases, three gaps.

---

## Context

**This pass exists because of when it is, not because of what it found.** Every item below
except two has been reachable since phase 4 and none of them stopped a run. What changes on
the far side of phase 6 is the cost of fixing them.

Two of the items are wrong numbers in the compute layer. `CLAUDE.md` §12 says a change to a
screen definition splits accumulated history into halves that cannot be pooled, and
`RUNBOOK.md` forbids re-running the attribution write. Today the only history is a backfill,
there is no researcher, no proposal and no order, and no live night has produced anything a
model judged. **This is the last moment at which a metric definition can be corrected for
free.** After phase 6's first night it costs a segmented analysis forever.

Three more are guards that return true where they were built to throw, or patterns that
match one spelling of a breach while their label states the whole rule. None is being
violated at `HEAD`. All three are latent in exactly the direction the next two phases move:
phase 6 is the first phase that writes monetary C#, phase 7 brings the arbitration
tie-break that `CLAUDE.md` §6 requires to be seeded from the run date, and
`SCREEN_LIFECYCLE.md` grows the shadow family that one of the guards cannot see.

**The pass corrects nothing about the digest chain, which is the thing the operator spent
the hours on.** The audit read C29, C32, C33, the chain, the rotation and the gate against
§11, §12 and the phase 5 plan and found the mechanism sound: invariant 7 structural at the
read set, invariant 15's halt traced and tested four ways, D-137's two-attempt latch exact,
and no code path in the chain naming either link. One digest item appears below and it is
not about the chain's behaviour.

**`ARCHITECTURE.html` is not edited by this pass and the largest single finding is one a
build session may not close.** §7 lists it first.

---

## 1. What this pass is, and what it is not

**It is a corrective pass with a fractional number rather than a letter, and that is
deliberate.** `CLAUDE.md` §3 gives corrective passes a letter and this corpus has used O,
Q, K, L, M, N and R that way, each one a correction inside a phase that was running. This
is not inside a phase: phase 5 is signed off and phase 6 has not started. `phase-3.5` is
the precedent for a small numbered phase between two large ones, and
`prompts/README.md`'s `phase-<n>-<slug>.md` accepts the name without amendment.

**It adds no measurement to its own scope** [`CLAUDE.md` §3]. Two checkpoints below name a
query whose answer decides how a done-when line reads. Both are queries against tables that
already exist, read at sign-off by whoever writes the verdict, which is the rule rather
than an exception to it.

**It touches no authored document except where §4 has been authored first.** The list of
authored edits this pass is waiting on is §4. The list it must not make is §7.

**It does not re-run the attribution write** [`CLAUDE.md` §12, `RUNBOOK.md`]. Checkpoint
5.5.3 recomputes two metric columns and therefore changes what a future night would score.
What it does about the 74,767 rows already frozen on the old definitions is D-158's to
decide and is not assumed here.

---

## 2. What the audit found, in one table

Twenty-two findings survived adversarial verification. Six are not in this pass because
only a human may close them and they are in §7. Two were refuted or reframed to the point
of not being defects and are noted in §8 rather than silently dropped.

| # | Item | Fails | Closed by |
|---|---|---|---|
| 1 | `adx14` is written 0 where `METRICS.md` says null, and a test asserts the 0 | Silently, into the frozen record | 5.5.3 + D-158 |
| 2 | `sector_relative_strength` equal-weights sectors and emits a ratio | Silently, into the cached prefix and attribution | 5.5.3 + D-158 |
| 3 | `ScreenConfigFacade` returns true for every `screens.X-*.` key | Open, silently | 5.5.4 |
| 4 | `RankSql` never nulls a rank, so a raised floor leaves stale ranks | Silently, admitting names that cleared no floor | 5.5.5 |
| 5 | `FloorAsync` resolves two config keys at the range end | Silently, on the first revision | 5.5.6 |
| 6 | `PersistenceMeasure` prints 0.0000 for an unmeasurable lag | Silently, in the flattering direction | 5.5.7 |
| 7 | Two done-when lines score `holds` on an empty range | Silently, as a manufactured pass | 5.5.7 |
| 8 | `guards.ps1` matches one spelling each for invariants 6 and 11 | Green over an unmatchable breach | 5.5.8 |
| 9 | Invariant 16's C# half has no enforcement at all | Green, and phase 6 writes the first monetary C# | 5.5.8 |
| 10 | `DeclaredAccess` checks the table named, never the SQL run | Open, with plausible output | 5.5.9 |
| 11 | Column ownership unenforced for six of eight statement writers | Silently; `BUILD_PLAN` calls it closed | 5.5.10 |
| 12 | The no-paid-provider deferral exists only as two database rows | Loudly, but only after money is spent | 5.5.11 |
| 13 | `CONFIG_REFERENCE` Consumer column misses four real consumers | An audit draws the wrong blast radius | 5.5.12 |
| 14 | `FIXTURES.md` cites a deleted method; four passing fixtures unregistered | The one registry `CLAUDE.md` §9 trusts | 5.5.13 |
| 15 | `RUNBOOK` enumerates two tolerated failures; three ship | At review time, which is the point of the rule | 5.5.14 + D-159 |
| 16 | `RUNBOOK`'s cycle table names three 18:05 engines; the code runs five | At 18:40 on a night that stopped | 5.5.14 + D-159 |
| 17 | The evening-order test asserts one direction of two | A night runs short and reports completed | 5.5.15 |
| 18 | `digest.chain` position-pairing claim is false of both links | An operator reorder probes the wrong address | 5.5.12 |

---

## 3. The two wrong numbers, and why they are one boundary

**Items 1 and 2 are the only findings in this pass that change a stored value, and they
must land in the same commit or not at all** [`CLAUDE.md` §12, "Bundle those"].

`adx14` is ranked by `screens.S2.metrics` [`ConfigStore.cs:423`]. `sector_relative_strength`
reaches `market_context_daily`, every `attribution` row and screen U1. Both are percentiled
by C11, so a change to either moves every other name's percentile in its cell. Correcting
one and not the other draws two boundaries through the history where one would do.

**`adx14` is the worse of the two, and it is worse than an untested gap.**
`METRICS.md:205-206` reads "Null when `+DI + -DI` is zero, which is a name that has not
moved at all across the window." `IndicatorEngine.cs:891` is
`dx.Add(sum <= 0 ? 0 : ...)`, which writes zero for exactly that condition. The guard
seven lines above it at `:880-885` does return null and carries the document's own
justification in its comment, but it fires on `smoothedTr[t] <= 0`, which is a different
condition. **And the repository's own reference test constructs the reachable case and
asserts the wrong answer**: `TheFlatSeriesReproducesItsClosedForm` feeds close 100, high
101, low 99 on every bar, so TR is 2 throughout and the `:880` guard never fires, while
+DM and -DM are zero throughout, and `IndicatorEngineTests.cs:46` asserts
`Assert.Equal(0f, row.Adx14!.Value, 5)`. The artifact that was supposed to be the
reference encodes the defect, which is why this is not a gap in coverage but coverage
pointed at the wrong answer.

**The reach is not theoretical.** `prompts/rubrics.md:111` reads "ADX below 15. That is
noise, not a trend", and `prompts/candidate-block.md:51` puts `adx14` in the technical
block. A fabricated 0 reads to the researcher as disqualifying evidence about a name whose
trend strength is in fact unknown. Phase 6 is the first phase that shows a model this
column.

**`sector_relative_strength` disagrees with its definition in both halves.**
`METRICS.md:714-721` defines it as the sector composite's 63-trading-date return **minus**
a universe composite built "over every active member". `MarketContextEngine` builds
`byDate[date]` with one entry per sector [`:413`], averages those [`:420`], and emits
`((level / universeLevel) - 1)` [`:445`]. So an eight-member sector counts as much as a
four-hundred-member one, names with a null sector or in a sector below
`market.sector_composite_min_members` are excluded from the benchmark entirely, and the
emitted quantity is a ratio rather than a difference. The code comment at `:416-419`
states the substitution as though it were the specification, which is what stopped it
being noticed. No test asserts the arithmetic: `MarketContextEngineTests.cs:170-208` seeds
a literal jsonb object and asserts only that it reads back as an object.

**`METRICS.md` declares itself unauthored at `:7`**, which is why §4 asks for a decision
rather than assuming the document wins. It is nonetheless the only written definition of
either quantity, and a build session may not decide that the code's reading is the right
one [`CLAUDE.md` §13].

---

## 4. Decisions that need authoring before code

Three, and the first blocks checkpoint 5.5.3.

### D-158, the two metric corrections and what happens to the history scored on them

**What it must settle.** Whether `adx14` and `sector_relative_strength` are corrected to
`METRICS.md`'s definitions or `METRICS.md` is corrected to the code, in each case; and, if
either value changes, what is done about `indicator_daily`, `market_context_daily`, the
percentile columns derived from them, `screen_score_daily`, and the 74,767 `attribution`
rows frozen at 4.14 under the old readings.

**What the pass assumes if nothing is authored.** Nothing. 5.5.3 does not run.

**The three options, stated so the decision is a choice rather than a default.** Recompute
the compute layer over the backfill window and leave `attribution` frozen, accepting that
attribution's scores and the store disagree before the boundary and recording the boundary
date. Recompute nothing and correct only the forward path, accepting two definitions inside
one backfill. Or correct `METRICS.md` to the code, which costs nothing today and makes the
document describe a universe composite that excludes small sectors and a quantity that is a
ratio.

**What makes this cheap now and expensive later.** There is no live history. `PROGRESS.md`
records phases 6 to 10 as NOT STARTED, no `proposal` or `order` row exists, and
`VALIDITY.md` §3's sample has not begun. After phase 6's first night the same decision
splits a record the experiment is read from.

**Blocked on nothing.** Both readings are established from the lines above.

### D-159, whether `RUNBOOK`'s failure table is the enumeration or a sample

**What it must settle.** `CLAUDE.md` §6 says two exceptions to fail-closed exist and both
are enumerated in `RUNBOOK.md`. `RUNBOOK.md:224-225` says the per-ticker 404 and D-71's
short page "are enumerated above in the failure table" and neither is in that table.
Three tolerances ship: the 404 across four ingest stages [D-100], the short page [D-71],
and the digest fall-through [D-137]. `CLAUDE.md`'s pair names the digest fall-through and
a per-candidate model PASS that is phase 7 and does not exist.

**The shape the audit recommends and does not adopt.** `CLAUDE.md` §6 stops carrying a
count and delegates it, so the rule reads that every tolerance is enumerated in
`RUNBOOK.md`'s failure table rather than being general tolerance; `RUNBOOK`'s table gains
the two missing rows. That keeps one list rather than two, which is the same reasoning
`CLAUDE.md` §9 applies to fixtures.

**Blocked on nothing.** `CLAUDE.md` is human-edited only [`CLAUDE.md` §13], so the
enumeration cannot be corrected from a build session either way.

### D-160, the tie-break at the last slot

**What it must settle.** C13 ranks with `dense_rank`, so names can share a rank, and C14
orders seats by rank then ticker ascending [`CandidateAllocator.cs:313-316`]. At a
boundary the alphabetically earlier ticker takes the slot, permanently and systematically.
§10 argues at length that a tie at a cutoff must be broken by a date-seeded coin flip
because every alternative carries a preference and the comparison would then measure the
bias rather than the rule.

**Why it is a decision and not a defect.** §10's reasoning is about arbitration in C18,
not allocation in C14, and no authored document states a tie-break rule for slot
allocation. The audit could not size how often it fires without querying the store.

**The measurement that sizes it, which is a query rather than new work.** Count, over
`screen_score_daily` joined to `candidate_set` on the frozen range, the name-dates where
the last seated rank equals the first unseated rank. If the count is small this is an
observation; if it is not, §10's argument applies to a population `VALIDITY.md` §3 reads
the per-screen result off.

---

## 5. Checkpoints

Fifteen, one commit each, `Phase 5.5 / 5.5.n - what it did`. Run `ci.ps1` per checkpoint.

**The order follows four facts, in decreasing force.**

**A value that reaches the record is corrected before anything that only reads it.** 5.5.3
is as early as an authored decision allows, because every checkpoint after it that touches
`screen_score_daily` would otherwise be built against numbers about to move.

**A guard that fails open is corrected before the enforcement that would have caught it is
widened.** 5.5.4 to 5.5.7 close the holes; 5.5.8 to 5.5.10 widen the nets. Widening first
would turn the suite red on defects and make one checkpoint carry two arguments.

**Every checkpoint that widens a net must go red before it goes green.** Each of 5.5.8 to
5.5.10 and 5.5.13 names the assertion that fails at `HEAD`, because a conformance test
that passes the moment it is written has not been shown to bind. This is the repository's
own habit and is restated rather than assumed.

**And the record is corrected last**, because five of its rows describe what the earlier
checkpoints change.

| # | Scope |
|---|---|
| 5.5.1 | This plan archived verbatim to `prompts/spent/`, before any code. `PROGRESS.md` gains the phase 5.5 section head and the audit's provenance: the sixteen dimensions, the sha audited, and that twenty-two findings survived verification of twenty-nine. **No finding is closed in this checkpoint**; it records what was found so the closures below have something to be read against |
| 5.5.2 | `guards.ps1`'s two determinism patterns widened, alone and first, because it is the cheapest checkpoint that can be shown to bind and it sets the red-before-green pattern the later ones follow. `:84` becomes `DateTime(Offset)?\s*\.\s*(Today\|(Utc)?Now)` and a sixth check matches `TimeProvider`, both `.cs`/`.razor` with the `SystemClock.cs` exclusion; `:100` becomes `new\s+Random\s*\(\|Random\s*\.\s*Shared`, dropping the empty-parens requirement so a seeded-looking construction is surfaced for reading, and its empty `Why` field is filled. **Do not widen to a bare `\.\s*Today`**: `SystemClockTests.cs:38`'s `new SystemClock().Today` is the injected surface and would turn the guard red. **Done when** both checks still read `expected: 0    found: 0` at `HEAD` and the summary line reads six checks, and a scratch file containing `DateTime.Today` and one containing `Random.Shared.Next()` each make `guards.ps1` exit 1 naming file and line |
| 5.5.3 | **The two metric corrections, in one commit.** `IndicatorEngine.cs:891` returns `(smoothedTr[^1], null)` on `sum <= 0`, folding in the now-redundant `:880` guard; `IndicatorEngineTests.cs:46` becomes `Assert.Null(row.Adx14)` with its comment rewritten, since the current derivation divides zero by zero and calls the answer zero. `MarketContextEngine`'s universe composite is rebuilt over members rather than sectors and the emitted value becomes a difference, per whichever reading D-158 authored. A new fact asserts `Adx14` null for a series with a nonzero bar range and constant high and low, which is the only test reaching the null path by the `+DI + -DI` route. A new fact asserts `sector_relative_strength` against a hand-computed two-sector fixture with unequal membership, which is the first test of that arithmetic. **Blocked on D-158.** **Done when** both facts pass, the flat-series test asserts null, and whatever recompute D-158 directs has run with its row counts and its boundary date in `PROGRESS.md` |
| 5.5.4 | `ScreenConfigFacade` stops deciding scope by id shape. `screens.<id>.<suffix>` is scoped to `<id>` for any id segment, keyed on segment count so `screens.floor_percentile` stays shared; the legacy `^[Ss]\d+\.` branch is kept for the four `s5.*` keys, which are the only top-level screen-scoped form. **Do not widen the id to `[^.]+` without `screens.` being mandatory**, or `screens.floor_percentile` binds id "screens" and becomes unreadable by every screen. **Done when** a Theory driven off `SeededScreens.Ids()` rather than a literal list asserts `For(a).CanRead(b's key)` is false and `EnsureCanRead` throws for every ordered pair of distinct registered ids, that each id reads its own four keys, and that both shared floor keys stay readable by all. That theory fails at `HEAD` on the three `X-` ids. Driving it off the seeder is what stops a fourth shadow reopening it. The three sentences that overclaim today are corrected with it: `ScreenRegistry.cs:14-15`, `ScreenRegistry.cs:152-155` and `PROGRESS.md:10520` |
| 5.5.5 | `ScreenEngine.RankSql` restates the whole `(screen_id, date)` slice rather than only the qualifying rows, so a name below the floor is written null rather than left standing, and a `ClearRanksSql` is called by both null-floor early returns [`ScreenEngine.cs:288-291`, `ScreenRangeRun.cs:203`] so a date that loses its floor loses its ranks. **Done when** a unit test ranks at one floor, re-ranks at a higher floor with no intervening score write, and asserts the name below the new floor reads null; and a range test bumps `screens.floor_percentile` to a new version, runs `FloorAsync` alone, and asserts `SELECT count(*) FROM screen_score_daily s JOIN screen_history h USING (screen_id, date) WHERE s.rank_within_screen IS NOT NULL AND (h.floor_score IS NULL OR s.score < h.floor_score)` is zero. **That query is §06's "floors already applied" expressed as a query and is run once against the live store as well**, its answer recorded whatever it is |
| 5.5.6 | `ScreenRangeRun.FloorAsync`'s two `RequireAsync` calls move inside the session loop, resolving against `date` beside the existing per-date `LoadScoredAsync`, caching on the resolved config version so a range whose config never moved still reads them once. `SelectionRangeRun.cs:114-118`'s comment citing D-93 and INVARIANT 13 comes across. **Done when** a test seeds version 2 of `screens.floor_percentile` stamped mid-range, runs `FloorAsync` over the whole range, and asserts `screen_history.floor_score` before the stamp equals what a nightly `ScreenEngine` run over that same date writes. It fails at `HEAD`, where the whole range takes the version-2 value |
| 5.5.7 | The two lines that score a pass on no data. `ScreenPersistence`'s three lag fields become `double?`, read through a nullable sibling of `Number` that returns null on `null or DBNull`, and the printed table shows a non-numeric token; `pairs_5` and `pairs_21` are added to the `overlap` CTE and the table so the per-lag denominator is disclosed the way lag 1's already is. `SelectionDistributions` returns `DoneWhenLine(..., null)` for line three when no candidate row carries a size bucket and for line six when the candidate count is zero, mirroring the shape already used at `:232-234`. **Done when** `Worker distributions` over an empty range prints no bound rather than `holds` for both lines, and a persistence run over a screen with fewer than 22 ranked sessions prints the token at D-21 and a real figure at D-1. **`TheMegacapLineScoresAgainstTheConfiguredBound` must be reworked in this checkpoint**: it currently proves the bound is configurable by flipping `Holds` on an empty range and therefore passes only because of the defect |
| 5.5.8 | Invariant 16's C# half gains an enforcement, which it has not had since D-83. A sixth `guards.ps1` check, or a test beside `SchemaParityTests`, asserts no `\b(double\|float)\b` in the tracked `.cs` of the components that own monetary tables, **scoped from the stage registry's declared write columns rather than from a file exclusion list**, which is the shape that strengthens as the system grows rather than the one that needed two exclusions by 1.6. **Done when** introducing `double positionSize` in a monetary-owning component makes the check exit 1 naming file and line, and removing it returns green. **The operator-authored half is in §7 and this checkpoint does not wait on it** |
| 5.5.9 | The declared-access gate learns to read the statement. A `StatementTableConformanceTests` extracts identifiers following `FROM`, `JOIN`, `INTO` and `UPDATE` from the SQL each component issues, keeps only those appearing as a table or view in `information_schema` so CTE and subquery aliases drop out, and asserts each survivor is in that component's declared read or write set. **Done when** it passes over today's registry and a negative fixture removing `screen_score_daily` from a copy of `SelectionRangeRun.cs:240`'s declared list makes it fail, which it does not today although the join at `:250` still runs |
| 5.5.10 | Column ownership on the statement route. Preferred: `IStageData.WriteAsync` takes the columns and calls `EnsureColumnsDeclared` immediately after `EnsureCanWrite`, mirroring `BulkUpsertAsync`, with each stage passing its own declared array at all fourteen call sites. If that is too wide for one checkpoint, `Statements()` is extended to all six uncovered stages **and** gains the missing reverse assertion, a fact walking `AllOwnersForConformance` and failing for any component with a non-empty `TableWrite.Columns` that is on neither list. **Done when** that reverse fact fails at `HEAD` on six stages and passes after, and `TheCheckFailsWhenADeclaredColumnLeavesTheStatement` covers `ScreenEngine.RankSql` and `CandidateAllocator.AttributionSql` so a partial declaration is discriminated as well as a percentile one |
| 5.5.11 | The operator's standing deferral gains a home in the repository. `digest.chain` is seeded at the value in force rather than at `["local","haiku"]`, or the seeder gains the second chain row disabled, or a startup assertion refuses to construct a paid link unless a named setting says so. **The exposure is a fresh database only**: `ON CONFLICT DO NOTHING` means a re-seed against the existing store changes nothing and the operator's two edits survive, which is why this is a gap rather than a defect. **Done when** `seed.ps1` against an empty database produces a composition that constructs no paid link, asserted, and the run log names the chain in force on the first night. **This checkpoint does not enable the secondary and does not spend money** |
| 5.5.12 | `CONFIG_REFERENCE.md`'s Consumer column made true, and kept true. Four rows gain a real second consumer read from the composition rather than inferred: `events.earnings_backward_days` gains `FundamentalsIngestor`, where it orders the rotation queue rather than filling a calendar; `monitor.megacap_share_max` gains the Worker's `distributions` route, **where it is the bound a definition-of-done line is scored against**; `screens.floor_percentile` and `screens.floor_lookback_days` gain `ScreenRangeRun.FloorAsync`. `fundamentals.widest_gap_alert_days` gains the row it has never had, the pointer at `:35` sending the reader to a cadence table that does not contain it. The `digest.chain` note at `:632-638` is corrected: `BuildAsync` pairs a name with an order number and never an address, and the local link resolves its own endpoint at `provider_order = 1` regardless of where `local` sits in the chain, so the property the note claims holds for neither link. The stale key-count narration at `ConfigStore.cs:173-176` and `ConfigResolutionTests.cs:377-384` is carried to 106. **Done when** a `ConfigReferenceDocumentTests.EveryConfigReadSiteIsNamedInItsConsumerCell` scans `src/` for `RequireAsync("<key>"` literals, maps each to its declaring type, and asserts that type is named in that key's Consumer cell. It must fail at `HEAD` on these rows and pass after |
| 5.5.13 | `FIXTURES.md` made true, and kept true. `:55` is struck in place on `:29`'s own pattern, its cited method having left the suite and its stated behaviour having been reversed by D-95, with the replacement named inline. Five registry rows are added for the fixtures that exist and pass and are registered nowhere: the four `FilingDateRuleTests` filing-date cases with `NoPeriodIsEverReadableOnOrBeforeItsOwnPeriodEnd` beside them as INVARIANT 12's property form, and `FreshnessGuardTests.ANewestDateOlderThanTheLastSessionAborts`. The six `[phase 1]` lines are deleted from the forward list, the clean-gaps one because `:129` already covers it. **`BUILD_PLAN.md:185` makes registering these a done-when line of 1.10 and `PROGRESS.md:351` records 1.10 met, so a definition-of-done line is recorded met against a registry that has no such row**; that is recorded at 5.5.15 rather than quietly repaired. **Done when** a `FixtureRegistryConformanceTests` parses `FIXTURES.md`, skips rows whose cells are struck, and asserts each `Class.Method` citation resolves by reflection against the test assembly. It fails at `HEAD` on one live row and passes after |
| 5.5.14 | `RUNBOOK.md` made to describe the night that runs. The cycle table's 18:05 row names all five stages rather than three, `FlowEngine` and `SentimentEngine` appearing nowhere in the document today, failure table included. The failure table gains the two tolerated per-ticker rows D-159 settles. **Blocked on D-159** for the `CLAUDE.md` half only; the cycle table is a spec correction under D-73 and is not. **Done when** a `RunbookDocument` in `Corpus/` parses the cycle table and a test asserts every name in `NightlyRun.EveningOrder` is reachable from a row at the clock time its comment gives. It is red at `HEAD` on two engines |
| 5.5.15 | The evening-order test gains its second direction, and the record is corrected. `TheEveningOrderMatchesTheArchitectureAndNamesOnlyCatalogueComponents` asserts only that every name in `EveningOrder` is a catalogue component; the converse, that every component the catalogue gives a nightly cadence appears in the order, is what would catch a stage never added. §03 carries a Cadence column, so it is derivable. `BUILD_PLAN.md:624`'s `TableWrite.Columns` row stops reading "Closed at 1.12" and reads closed on the staged route with the statement route open, citing phase 1's finding G. `PROGRESS.md` records what 5.5.3 moved, what 5.5.5's live query answered, the 1.10 done-when line that was recorded met against an empty registry, and the two metric boundaries with their dates. **Done when** the converse assertion is green with the six phase 6 and 7 components excluded by name and cadence rather than by omission |

---

## 6. Definition of done

`BUILD_PLAN.md` carries no phase 5.5 block yet, so these seven lines are what this plan
proposes for one and are authored rather than adopted from here.

1. **Neither `adx14` nor `sector_relative_strength` can be written at a value its
   definition forbids**, with a test for each that fails on the pre-5.5.3 code, and the
   boundary date recorded whatever D-158 directed about the history.
2. **No registered screen can read another registered screen's config**, asserted over
   every ordered pair of ids read from the seeder rather than from a list, so a ninth
   screen cannot reopen it.
3. **No `rank_within_screen` survives a floor it does not clear**, asserted both as a unit
   property and as a store-wide count that is run against the live database once.
4. **No done-when line reports `holds` on a range with no data**, asserted for both lines
   over an empty range, and `TheMegacapLineScoresAgainstTheConfiguredBound` no longer
   depends on the defect for its own proof.
5. **Every net widened in 5.5.8 to 5.5.10 and 5.5.13 is shown red before it is shown
   green**, each naming the fixture or scratch file that makes it fail. A conformance test
   that has never failed has not been tested [`Corpus/TestDatabase.cs:44-46`].
6. **A database built from `seed.ps1` alone constructs no paid link**, so the standing
   deferral survives a machine.
7. **`CONFIG_REFERENCE.md` and `FIXTURES.md` each gain the conformance test that keeps
   them true**, both failing at `HEAD`, because both documents drifted while being the
   thing an audit is told to trust.

**Invariants at risk:** **2**, which 5.5.4 restores rather than protects. **6 and 11**,
whose only mechanical enforcement is the two patterns 5.5.2 widens. **10**, in both the
column quarter 5.5.10 addresses and the table check 5.5.9 does. **12**, which 5.5.13
registers and does not change. **13**, which 5.5.6 restores at the one site that leaked
it. **16**, which gains a C# enforcement for the first time since D-83. **1 is asserted
unchanged** by every checkpoint: nothing here narrows by rank, score or count.

---

## 7. What only a human may close, and this pass must not touch

**Six findings survived verification and are not in §5**, because `ARCHITECTURE.html`,
`CLAUDE.md` and `METRICS.md`'s definitions are human-edited only [`CLAUDE.md` §13]. They
are listed with the exact sentence and the disagreement, and with nothing proposed as
though it were authored.

**1. `ARCHITECTURE.html` carries no build-state marker anywhere, and 13 of its 36
components have no code.** `CLAUDE.md:473` requires one on every section describing a
component, and gives the reason: without one, shipped and aspirational prose read
identically. A whitespace-tolerant sweep of the tag-stripped document finds none; the only
tags are `NEW`, `CHANGED` and `NOT YET IN SCHEMA`, which mark document revision against
v0.3. §08 describes `C16 ResearcherClient` in the present tense across thirty lines and no
such type exists under `src/`. **This is the single largest finding of the audit and it is
the direct cause of not being able to tell shipped from designed by reading.** It is filed
nowhere: no `PROGRESS.md` entry, no carried obligation, no decision. There is a real
tension for a human to settle rather than a build session: D-66 puts phase status in
`PROGRESS.md` and nowhere else, which can be read as forbidding per-component markers in
the architecture. If that is the reading, `CLAUDE.md` §13 is the line that moves. Either
way one of two authored documents is currently wrong.

**2. `CLAUDE.md` §13 restates D-73 as a closed list of four documents and two, which D-73
names as the misreading.** D-73 at `DECISIONS.md:796-817` says the rule is a test on what a
document is read for, that it deliberately names no document "so a document written later
classifies itself instead of waiting to be added to a list", and that reading the list as a
definition "is what left three documents unclassified and one carrying both conventions".
D-73 also classifies `BUILD_PLAN.md` as a spec, which `CLAUDE.md` does not mention. This is
the one case the audit found of a decision cited with a meaning different from what the
register records, and it is self-reproducing, because `CLAUDE.md` is the file every session
loads. Already noted at `CHANGELOG.md:963-966` as reported rather than changed.

**3. `CLAUDE.md` invariant 16's second sentence names an enforcement that is gone.** "`guards.ps1`
greps for it" was true until D-83 replaced the `float|double` source grep with a schema
assertion, which is genuinely stronger on the column side and covers nothing on the C#
side. No decision amends the sentence. 5.5.8 builds the missing half; the sentence still
describes a mechanism the file does not have.

**4. `CLAUDE.md` invariant 10 enumerates three multi-writer tables; `SCHEMA.md` declares
seven over nine.** D-77 added four compute splits and the invariant text never gained them.
Nothing in CI reads the enumeration, so nothing fails; a session consulting invariant 10
before touching a shared table reads four of the compute tables as violations rather than
as the design. The same sentence says "enumerating exceptions kept failing because the rule
was stated wrongly", which is this enumeration having gone stale again three phases later.
`WriteOwnershipConformanceTests.cs:13-15`'s header repeats the stale count and is a build
artifact, so that half can be corrected at 5.5.10.

**5. `ARCHITECTURE.html` §18's halt row is unconditional where D-115 authorised a second
carve-out.** The row says every live screen returning zero halts before the model is
called, with one exception for shadows. `ScreenEngine.cs:229` also does not halt when no
live screen has a floor yet, which D-115 authorises and the code reports correctly. The
4.12 warm-up night, 2,819 members and 14,095 scores and 0 candidates, satisfies the row as
written and did not halt. §18 is the table an operator consults when a night produces
nothing.

**6. §18's sentiment row and D-14 give different answers, and the code follows D-14.**
The row says mean reversion news gates pass when sentiment data is missing. D-14 and §05
say the gates fail open below three articles in seven days, which is what
`ScreenEngine.NewsSettled` implements. The uncovered population is a name at or above the
threshold whose sentiment metrics are null: it is excluded from S5 with a null score.
`PROGRESS.md:8957` measures 55.2 percent of `sentiment_derived_daily` rows null on all
three metrics, so the population is not small, and `PROGRESS.md:10383`'s three-cause table
for S5's 68-of-35,108 fill does not contain this cause. **The query that sizes it before
anyone chooses**: count name-dates whose seven-day `sentiment_daily` article sum is at
least `s5.news_gate_min_articles` while `article_count_z_own_90d` is null. A nonzero answer
makes that a four-cause table.

**Also for a human, and smaller.** §05's S4 typical-fill cell reads "2 to 8, median 5"
where §06's own measurement 55 lines later records 8.00 seats on 1,161 of 1,161 producing
dates. The H1 reads "Thirty-four components" against 36 catalogued, and C36 RecordInspector
appears in neither the layer map nor the four-outside-the-layers line. `METRICS.md` §6.3
states the percentile as `PERCENT_RANK()` over the cell, which contradicts §6.2 nine lines
above it and would be wrong if implemented; `PercentileEngine` implements §6.2 and is
correct. `METRICS.md:742` says eight PROPOSAL entries and `BUILD_PLAN.md:646` says nine;
the file carries six and one BLOCKED.

---

## 8. What blocks this pass, and what is only reported

### The blocker

**D-158.** Checkpoint 5.5.3 does not run without it and no other checkpoint depends on it.
If the decision is slow, 5.5.1, 5.5.2 and 5.5.4 onward proceed and 5.5.3 lands out of
order, which costs a note in `PROGRESS.md` and nothing else.

### Reported and not closed here, five

**The `EodhdClient` sits in `Data`, so the read-only Api links it.** `ApiIsolationTests`
checks for `Pipeline` and `Worker` and not for an outbound HTTP client that spends the
shared 100,000-unit daily allowance. Nothing in the Api constructs it today;
`ApiComposition.cs:20` returns `RecordInspector` alone. Verification lowered this from
drift to an untested placement: `CLAUDE.md:189` is a one-line tree annotation rather than
a spec of `Data`'s contents, and `Data` already holds `SystemClock` and `TransientFault`.

**Only the Api boundary has a test.** Core's zero-reference rule is held by an empty csproj
and a comment. A `ProjectReference` from Core would fail the build as a cycle, so that half
is self-enforcing; a `PackageReference` adding Npgsql or an HTTP SDK compiles green, breaks
no test and trips no guard.

**Invariant 14 has no enforcement, no test and no done-when line.** Nothing can move a risk
cap because C22 does not exist. The assertion that survives phase 7 is about the writer
rather than the seeder, since phase 7 must seed the eleven `risk.*` keys: no learn-layer
component may write a config key outside a named prefix. A `CHECK` on `config_rows`
constraining `set_by` is writable before C22 exists because it tests the table.

**`UniverseBuilder.FundamentalsAsync` is the one point-in-time read site whose as-of
predicate no test can distinguish from its absence.** Verification narrowed this: the
RecordInspector limb was wrong, `RecordInspectorTests.cs:316-334` asserting on the emitted
SQL text, and `FlowEngineTests` and `EarningsHistoryTests` defend the other two tables.

**`DigestChain.Failed` materialises a `HashSet` under a comment asserting an order.** No
production consumer today; the two consumers are test lines. It passes because .NET happens
to enumerate in insertion order when nothing has been removed.

### Refuted, and recorded so they are not found again

**`RecordInspector` is not missing from `CONFIG_REFERENCE.md`.** `:226-240` carries a
dedicated "Keys this reader consumes without owning" subsection naming all eight keys. The
document has a deliberate convention for reader-consumed keys.

**C29 is not assigned to two layers.** All three mentions give one answer; the catalogue's
"runs inside the select layer" sits in the timing column beside "Daily 18:32" and is about
when, not where. Already recorded at `PROGRESS.md:11464`.

**Invariant 5 is not untested through neglect.** Lowered on verification: no production
file names either return column, and `ScreenRangeRunTests` asserts the one
learning-adjacent component reads neither.

---

## 9. What this pass does not do

No researcher, no dossier, no prefix, no proposal, no order. No change to the digest
chain's behaviour, whose mechanism the audit found sound. **No enabling of the secondary
link and no paid call**: 5.5.11 makes the existing deferral survive a re-seed and does not
revisit it. No backfill beyond whatever recompute D-158 directs. No edit to
`ARCHITECTURE.html`, `CLAUDE.md` or `METRICS.md`'s definitions. No re-run of the attribution
write [`CLAUDE.md` §12]. And no measurement added to its own scope: the three queries named
above are queries against tables that already exist.

---

## 10. Verification

`ci.ps1` per checkpoint, green before the push. At sign-off, the two steps in
`BUILD_PLAN.md` §Sign-off, the second in a session that has not committed here.

**One extra step this pass owes and earlier ones did not.** Four checkpoints add a
conformance test whose whole value is that it can fail. At sign-off each of the four is
run against the mutation named in its checkpoint row and shown red, and the four results
are recorded together in `PROGRESS.md`. A conformance test asserted to bind and never
observed binding is the decoration `CLAUDE.md` §9 warns about, and this pass adds four of
them.
