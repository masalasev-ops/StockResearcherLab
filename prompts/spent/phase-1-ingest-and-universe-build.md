# Phase 1 — Ingest and universe, the prompts issued during the build

**Archived after the fact, on 2026-08-10, which is the wrong time and is recorded
as such.** `CLAUDE.md` §3 says a prompt is archived before code and that a prompt
archived at the end is archived after the session has already learned things. This
file was written at the end. It is verbatim, but it is late, and a reader should
weigh it knowing that the session which assembled it is the session it describes.

**Companion to `phase-1-ingest-and-universe.md`, which is not superseded.** That
file holds what was issued before code: the opening request, the two authored
decisions N.1 to N.3, and amendments A1 to A9. It was archived at the pre-flight as
the plan required. This file holds everything issued after the plan was approved,
which is where the archiving stopped.

**Verbatim by construction rather than by transcription.** Every block below was
extracted programmatically from the session transcripts and not retyped, because a
spent prompt records what was asked and a paraphrase would destroy the only
evidence of why the code looks the way it does [`CLAUDE.md` §14, D-63]. Nothing is
summarised, reordered or corrected. Spelling and typing as issued.

Two sessions are interleaved in timestamp order: the build session, and a separate
session on 2026-08-09 that recorded four catalogue deviations as findings.

---

## What this file does not contain, stated rather than left to be discovered

**Amendments A16, A17 and A18 do not exist in any surviving transcript**, and no
document in the corpus cites them. A15 is followed directly by A19. Either the
numbering skipped three, or they were issued somewhere no longer on disk. This is
recorded because a reader who sees A15 then A19 will otherwise assume the archive
is incomplete, and the honest position is that nobody can now tell which it is.

**A1 to A9 are not reproduced here.** They are in the companion file. They appear in
this session's transcript only inside tool results, which is this session reading
that file rather than the amendments being issued again.

**Pass Q's own prompt is still not archived**, unchanged from what the companion
file reports. Only the person who issued it has the text.

---

## 1. 2026-08-07 03:12:48 UTC, build session `[issued]`

Can you continue

---

## 2. 2026-08-07 05:06:19 UTC, build session `[issued]`

## A10 Settledness is relative, not a re-fetch

The re-fetch is dropped. A stored count from an earlier run is rejected as the
replacement: a stage is a pure function of its date and config version
[CLAUDE.md §5, §6], and a guard reading what it saw on a previous wall-clock run
is not. Two databases with identical `price_daily` contents would disagree.

**1.3 settledness, restated.** The date under test is settled when its row count
in `price_daily` is at or above a configured fraction of the median row count of
the last N dates strictly before it. Computed from `price_daily` alone, on first
sight, no history. The fallback is unchanged: walk back from the newest date
until one passes all three checks, return it.

Measured support, for the commit body rather than for the threshold: settled days
now read 50,029, 50,148, 50,204 and 50,228, a spread of 0.4 percent. 2026-08-04
part-settled read 44,665 to 44,708, which is 89.0 percent of its own finished
count. The two populations are separated by roughly ten points of margin.

**The fraction and the window are D-64's**, and D-64 was opened as a deferral to
a wider sample. That sample now exists, so D-64 is closable. Do not pick the
number in the build session.

**1.2 gains a trailing re-load window.** C02 loads the last N trading dates every
night, not tonight alone, so a day loaded short tops up on a later run. D-68's
upsert is what makes that safe, and it is what actually heals accretion. Without
it, 2026-08-04 stays at 44,708 in `price_daily` for ever. The window length is a
config key, not a literal [CLAUDE.md §8].

**Consequence at 1.13:** twelve keys, not eleven. The re-load window is the
twelfth and 1.13 runs before 1.2.

**Consequence for 04238e8, before it merges.** C07 now makes exactly one provider
call, the exchange calendar. The Reads cell amended on that branch names the bulk
EOD endpoint for a re-fetch that no longer exists, so correct the amendment
rather than superseding it: it has not reached main and is an edit in progress.

**Done when:** 1.3's scope contains no re-fetch and no stored count; C07's
declared provider calls number one; the three guard tests still fail in
isolation, with the settledness case built as 44,708 stored for the date under
test against a trailing window near 50,100 rather than as a re-fetch pair;
`seed.ps1` reports twelve; `CONFIG_REFERENCE.md` carries the re-load window key
with a Consumer read off the composition code; the Reads cell on
`decisions-d64-d65` names `price_daily`, `run_log` and the exchange calendar and
not the bulk EOD endpoint.

## A11 Both unique indexes need NULLS NOT DISTINCT

Postgres treats nulls as distinct in a unique index, so a row whose key contains
a null never matches `ON CONFLICT` and every re-run inserts another copy. The
index exists, the statement succeeds, the row count climbs, and nothing fails.
That is D-68 silently not holding in the two tables A7 was written about.

`insider_transaction` has `filed_at`, `transaction_date` and `price_per_share`
nullable. `events` has `event_date` and `announced_date` nullable. Both proposed
tuples contain at least one.

Declare both indexes in 0002 as `UNIQUE NULLS NOT DISTINCT`. Available since
Postgres 15; `ci.yml` runs `postgres:18`.

**`insider_transaction`'s key takes `accession_number`**, per this session's own
finding that a form4 filing nests transaction arrays and is not itself a
transaction. That is a new column: add it in 0002 and name it in `SCHEMA.md`,
since it stops being a detail the moment it is the natural key. One filing yields
several transactions, so the key is `accession_number` plus the transaction's own
attributes. State the tuple in 1.7 and say in the commit body what a collision
inside one filing would mean, since two rows identical on every attribute would
collapse into one.

**Done when:** both indexes in 0002 carry `NULLS NOT DISTINCT`;
`insider_transaction` has an `accession_number` column and `SCHEMA.md` lists it;
a test writes twice a row whose key contains a null in each of the two tables and
the row count is unchanged both times; the conflict-target audit query returns a
unique index matching each of the eight targets.

## A12 D-69, and 1.7 splits rather than blocks

Record **D-69, whether the flow screen survives an unbackfillable insider
source.** `OPEN`, owed to phase 4, not phase 1.

Measured at 1.9: `/sec-filings/{t}/form4` ignores `from`, `to`, `limit` and
`offset`. Limits of 5, 20, 50, 100 and 1000 all return 20 rows; offsets 0 to 100
return the same first `accession_number`, 120 collected and 20 distinct; six
180-day windows and three `to` values return an identical span. CCS.US has 324
form4 filings and 20 are reachable, covering 2025-09-11 to 2026-06-12. On an
active name 20 filings may not span 90 days: the probe measured 26 transactions
for the control in that window.

D-58 already removed `short_interest_change` from S4. Unbackfillable insider data
removes `insider_net_90d_usd` and `distinct_buyer_count` from any historical
window, leaving `inst_ownership_change` alone, quarterly. The probe separately
found transaction code P at zero on all seven names over 90 days, so
`distinct_buyer_count` had nothing to rank on live either.

`ARCHITECTURE.html` §20 names sentiment and flow as the two screens doing most to
keep this system off megacaps, so this is a loss to the design's central
protection rather than to one screen. Options, none chosen here: drop S4 and run
four screens, with the slot arithmetic in §6 recomputed; rebuild S4 on
institutional ownership plus forward-only insider data; or source Form 4 from SEC
EDGAR, free and complete and canonical, at the cost of a second provider and a
real ingest.

**Before the options are treated as the whole set**, one timeboxed check: whether
the subscription exposes any insider feed that is date-ranged or market-wide
daily rather than per-ticker. A bulk daily feed backfills the way `report_date`
does for holdings. Answer it from the documentation plus a call or two and record
it in the same transcript, not as a new checkpoint.

**1.7 is not blocked and splits in two.** The `institutional_holding` half is
backfillable and proceeds as planned. The insider half ingests the reachable
filings and starts accumulating forward from tonight, because forward
accumulation costs nothing and is the only thing that shrinks the gap while D-69
is open.

**State the consequence for phase 4 rather than leaving it to be discovered:**
S4 will have no backfilled history, so its trailing 250-day floor does not exist
and it returns nothing during the backfill and for the first year of live
running. `ARCHITECTURE.html` §18 already treats a screen returning zero as
expected rather than an error, so running on four screens for that year needs no
architecture change.

**Done when:** D-69 is in `DECISIONS.md` under Open with the measured figures
above and the three options; a carried obligation row from phase 1 to phase 4
names it; 1.7's scope states the two halves and the insider half's forward-only
limit; `PROGRESS.md` records the endpoint's parameter behaviour as measured with
the transcript path; the timeboxed check's answer is in the same transcript.

---

## 3. 2026-08-07 12:57:00 UTC, build session `[issued]`

## A13 D-70 authored, D-65's mechanism struck in place

Author in `DECISIONS.md`:

**D-70 Settledness is measured against the trailing population, not by
re-fetching.** `ACTIVE`. Supersedes the settledness mechanism in D-65; D-65
otherwise stands.

Re-fetching a date inside one run detects nothing. 2026-08-06 read back to back
returned 44,204 both times, because accretion runs over hours while two calls are
seconds apart. Repairing it with a count stored by an earlier run was rejected: a
stage is a pure function of its date and config version [CLAUDE.md §5, §6], and a
guard whose verdict depends on what it saw during a previous wall-clock run is
not. Two databases holding identical `price_daily` contents would disagree.

A date is settled when its row count in `price_daily` is at or above
`freshness.settled_fraction` of the median row count of the last
`freshness.settled_window_days` dates strictly before it. Computed from
`price_daily` alone, on first sight, with no dependence on run history. The
fallback is unchanged: walk back from the newest date until one passes all three
checks, and return it.

This removes the ordering constraint the re-fetch implied between C02 and C07.
RUNBOOK's 17:30 and 17:40 stand, and C07 makes one provider call rather than two.

Then strike D-65's line 611 in place:

`~~A date is unsettled if re-fetching it returns more rows than are stored for
it.~~ [superseded, D-70]`

**Done when:** D-70 is in `DECISIONS.md` as ACTIVE and names D-65 as what it
supersedes; D-65's line 611 is struck with the pointer and D-65's status is still
ACTIVE; a whitespace-tolerant sweep for "re-fetch" over `docs/` returns only
strikes, records and D-70's own explanation of why the mechanism was rejected.

## A14 D-64 closes, and 1.13 seeds fourteen

Author in `DECISIONS.md`:

**D-64 The absolute row-count floors stand.** `ACTIVE`, closing the deferral.

2026-08-04 finished at 50,228 rows. The probe read it at 44,665, 44,686 and
44,708 and stopped rather than converged. Settled days now read 50,029, 50,148,
50,204 and 50,228, a spread of 0.4 percent.

D-59's floors are unchanged. 44,708 sat above the 40,000 abort floor and inside
the 40,000 to 45,000 alert band, so no movement of those floors would have caught
that day without also rejecting settled days. The absolute floors answer "is this
file catastrophically short". They were never the tool for "is this file still
filling", which is D-70's check.

**D-70's values, seeded at 1.13:**

`freshness.settled_fraction` = 0.95. The separation corridor runs from 89.2
percent, the measured part-settled ceiling, to 99.7 percent, the lowest settled
observation. The part-settled side is measured across three readings; the settled
side is four consecutive summer sessions and its true spread is unknown. The bound
therefore sits near the measured side, six points above it, leaving five points
for settled variation not yet observed. The asymmetry supports this rather than
opposing it: a false fail costs one stale day and self-corrects through the
fallback, while a false pass runs the night on an 11 percent short universe and
looks normal.

`freshness.settled_window_days` = 20. A median, not a mean, and 20 rather than a
handful, because a day loaded short is still in the trailing window until C02's
reload tops it up, and it would drag the reference down exactly when the test
should not loosen. A median over 20 is unmoved by one such day. 20 also matches
the window already used for median dollar volume.

**Watch item, recorded rather than rediscovered:** the first low-volume holiday
week is what would move 0.95. If a genuinely settled session between Christmas and
New Year comes in below it, the value is too high and rises with that observation
recorded [CLAUDE.md §11: never loosen a bound because a measurement missed it, so
the reasoning goes in the decision before the week arrives].

**1.13 seeds fourteen, not twelve.** The two above are new and are separate keys
from C02's reload window, since different components own them.

**Done when:** D-64 is ACTIVE and closed with the four settled observations
recorded; `CONFIG_REFERENCE.md` carries both keys citing D-70 with their Consumer
read off the composition code; `seed.ps1` reports fourteen; 1.3 reads both through
the as-of resolver and contains no literal fraction or window; 1.13's third test
case, a date earlier than every `set_at`, still passes.

## A15 D-69's subject and shape

The endpoint evidence stands as retracted and re-measured; D-69 is authored on
`inst_ownership_change` alone.

`Holders::Institutions` is a top-20 snapshot rather than a series, and
`sec-filings/{t}/13f` is a 404 with the filings index listing only `10k`, `10q`,
`form4` and `8k`. `SCHEMA.md`'s claim that `report_date` is what makes this
backfillable is false against this source and survived because the column is
populated. Strike that claim in place pointing at D-69.

This is D-58's shape. D-58 removed an input from S4 when the source could not
supply a series with an as-of date, and the same facts hold here. The difference
from the retracted framing is that S4 survives on two live inputs, so removing a
third is a smaller change than adding a second data provider.

Record alongside whichever way it goes: after this, both of S4's surviving inputs
come from one endpoint, so a single provider change takes the whole screen rather
than one input. That is a concentration the screen did not have when it was
designed with four inputs from three sources.

`institutional_holding` still ingests and 1.7's institutional half still builds.
A top-20 current-holders snapshot is a usable static feature; it is only the
change metric that has no series behind it.

**Done when:** D-69 names `inst_ownership_change` as its subject with the
measured evidence; `SCHEMA.md`'s backfillable claim is struck with the pointer;
1.7's institutional half is unchanged in scope; the single-endpoint concentration
is recorded in D-69's body whichever option is taken.

---

## 4. 2026-08-07 13:23:07 UTC, build session `[issued]`

## A19 Pin the bracket encoding in 1.1's request-form assertions

`page[offset]` and `page[limit]` carry square brackets, which are not legal
unencoded in a query string, and .NET's `Uri` does not reliably escape them.
Depending on how the request is built you can send `page[limit]=50`, `page%5Blimit%5D=50`,
or a mangled form, and the endpoint's response to a wrong one is a 422 that reads
like an entitlement problem rather than an encoding problem. That is exactly how
the paging form was misread the first time.

The four request-form assertions already pin the `::` filter's colons. Pin the
brackets the same way, asserting the exact query string the client produces
rather than that a call succeeds. A test that only asserts a 200 passes on any
form the endpoint happens to tolerate today.

**Done when:** one of 1.1's four assertions names the produced query string for a
paged call, character for character; it fails if the escaping changes; no live
call is needed to run it.

## A20 The offset loop terminates on links.next, not on a short page

A loop that stops when a page returns fewer rows than requested is wrong when the
total is an exact multiple of the page size: the last full page looks like a
middle page and the loop asks for one more. Worse, it is silently wrong the other
way if the endpoint ever returns a short page mid-sequence.

`links.next` is present and is the endpoint's own statement of whether more
exists, so terminate on its absence. `meta.total` matched the filings index
exactly on CCS, NVDA and PHAT, so assert the collected distinct count against it
in the ingest itself rather than only in a test. That turns a silent partial
ingest into a failure at the point it happens, which is the class of thing
INVARIANT 12 exists for.

**Done when:** 1.1's paging test uses a fixture whose total is an exact multiple
of the page size and still terminates correctly; the ingest compares collected
distinct count against `meta.total` and fails the stage on a mismatch rather than
logging it.

## A21 A fifth guard, when convenient

`guards.ps1` already greps for `DateTime.Now`, `float`/`double`, `Guid.NewGuid`
and `new Random(`. Add `TimeZoneInfo` and `FindSystemTimeZoneById` over `src/`,
excluding nothing, since under `InvariantGlobalization=true` every one of those
throws at runtime and only on the path that exercises it.

**Done when:** `guards.ps1` prints 5 checks, each expecting 0 and finding 0, over
the same tracked file count; the header comment names why the fifth exists.

---

## 5. 2026-08-07 15:07:38 UTC, build session `[issued]`

## A22 One line on the handler, and one assertion on the next URL

**PooledConnectionLifetime.** The long-lived `HttpClient` is the right call, and
`IHttpClientFactory` is correctly not needed for it. Set
`SocketsHttpHandler.PooledConnectionLifetime` to a few minutes on the handler the
client is constructed with. That is the other thing the factory does, it is the
part that matters across a multi-hour backfill rather than a nightly run, and it
costs one property rather than a package and a DI registration.

Note it in the commit body beside the deviation, so the deviation reads as
complete rather than as a package that was skipped.

**Assert the reconstructed URL against links.next.** The loop terminates on
`links.next` being absent, which is right. If the next request is still built from
offset arithmetic rather than by following that URL, then two things must agree
and only one is tested. Assert in the paging test that the URL the client builds
for page n+1 equals the `links.next` the fixture carries for page n. A divergence
then surfaces as a failing test rather than as a silently skipped page.

Following `links.next` directly is the alternative and is fine, with one caveat:
check whether it carries `api_token`, because a server-supplied URL containing the
token would put it wherever that URL is logged.

**Done when:** the handler sets `PooledConnectionLifetime` and the commit body
says why; the paging test asserts the built next URL against the fixture's
`links.next` character for character; 46 tests plus that one.

## A23 The SQL-conversion rule states its exception

`PROGRESS.md`'s note says every Eastern conversion happens in SQL. `SystemClock`
is the exception and is correct to be, since the one place that reads the ambient
clock is the one place that has to know what today means in market terms.

Restate as: every Eastern conversion happens in SQL, except `SystemClock`, which
is the single place permitted to read the ambient clock and therefore the single
place permitted to convert it. `guards.ps1`'s fifth check enforces exactly that
boundary and its exclusion list is the enforcement.

**Done when:** the `PROGRESS.md` note names the exception in the same sentence as
the rule; the fifth guard's header comment and the note say the same thing; no
file other than `SystemClock.cs` appears in that check's exclusions.

## A24 1.12 holds one connection, and the staging table is TEMP

The staged path is the first thing in this codebase that needs two statements on
one connection, and `StageData` opens a connection per call. Decide the shape
before writing it rather than after.

A `TEMP` table is invisible to any other connection, so a COPY on one connection
and an `INSERT ... SELECT` on another fails with a relation-does-not-exist error
that reads like a migration problem. A named `UNLOGGED` table avoids that and
brings two worse problems: a crashed run leaves rows behind for the next run's
insert to pick up, and two stages loading concurrently collide on one name.

So: `TEMP` table, created, filled and drained inside one connection held for the
duration of the staged write, dropped implicitly when it closes. The declared
access check still runs against the target table and operation before the
connection opens, per A2.

Column type mapping is strict in binary COPY and its errors are unhelpful:
`numeric` binds as `decimal`, `date` as `DateOnly`, `timestamptz` as
`DateTimeOffset`. Worth knowing before the first run rather than during it.

**Done when:** the staged route's signature makes the single-connection scope
explicit rather than leaving it to a caller; a test proves a second connection
cannot see the staging table; the undeclared-access test from 1.12 still throws
before any connection opens; loading the same fixture twice through the staged
path leaves the row count unchanged.

---

## 6. 2026-08-07 15:37:09 UTC, build session `[issued]`

## A25 Validate the identifier, then quote it

`[A-Za-z0-9_]` stops injection and does not stop `order`. Both `order` and
`position` are quoted in `0001_snapshot.sql` because Postgres reserves them, and
both pass the whitelist unchanged. The failure arrives in phase 5, when RiskGate
first writes an order and PaperBroker first writes a fill and a position, as a
syntax error in generated SQL rather than anywhere near this checkpoint.

Emit every identifier double-quoted after validating it. Quoting also pins case,
which matters because an unquoted identifier folds to lower case and the schema is
lower-case snake by convention rather than by enforcement. The whitelist already
rejects an embedded quote character, so the two together are safe.

**Done when:** every identifier the staged path emits is double-quoted in the
generated SQL; a test writes through the staged path to a table whose name is a
reserved word and it succeeds; the non-plain-identifier test still refuses; 53
tests plus that one.

## A26 1.2's window is coupled to 1.3's, and 1.3 needs a fourth case

**The coupling, which nothing currently states.** `price.reload_window_days`
decides how far back C02 tops up. `freshness.settled_window_days` decides how far
back C07 takes its median. A date that settles more slowly than the reload window
ages out of C02's reach while still short, stays short for ever, and then sits in
C07's median dragging it down. The guard gets looser rather than louder, which is
the wrong direction for a guard.

2026-08-04 was still filling more than twenty-four hours after its session closed,
so the reload window has to comfortably exceed that, not merely exceed it. The
median over twenty bounds the damage from any one such day, which is the second
reason a median beat a mean and is worth recording as such.

State the relationship in `CONFIG_REFERENCE.md` beside both keys, so a later
session tuning the reload window down for call-volume reasons sees what else moves.

**Where 1.2 gets its dates**, given there is no calendar until 1.3: either the last
N dates already in `price_daily` plus the target, or the last N calendar dates with
empty weekend responses tolerated. Pick one and say which. The second is simpler
and does not trip 1.14's zero-row halt, since that keys on the stage total rather
than on any one date.

**1.3's fourth test case.** A database holding fewer than
`freshness.settled_window_days` prior dates has no median to compare against. Fail
closed means the first run ever aborts and nothing can bootstrap. Passing silently
means the guard is off during exactly the period when the data is least trustworthy.
State the behaviour in the checkpoint rather than letting the implementation choose
it, and test it. This is A9's third case again: the one where the data is absent
rather than wrong.

**Done when:** `CONFIG_REFERENCE.md` states the coupling beside both keys; 1.2's
scope names where its window's dates come from; 1.3 carries a fourth test for
insufficient history with the expected behaviour named in the checkpoint text.

---

## 7. 2026-08-07 15:41:43 UTC, build session `[issued]`

## A27 The staged path asserts the declared column set

`TableWrite.Columns` has been declared and never asserted since phase 0, carried
forward as an obligation with no natural home. The staged route is that home: it
is the first place a stage states, in code, exactly which columns it is writing.

Compare the column set passed to the staged write against the component's
declared columns for that table and operation, before the connection opens,
alongside the existing declared-access check. A mismatch throws the same way an
undeclared table does.

While you are there, assert that the conflict target is a subset of the written
column set. It is a one-line check that turns a missing-column SQL error at
INSERT time into a named exception before anything runs.

Retire the carried obligation in the same commit rather than leaving it to be
noticed at sign-off.

**Done when:** a stage writing a column it did not declare throws before a
connection opens; a stage naming a conflict target outside its written columns
throws the same way; the `TableWrite.Columns` row leaves the carried obligations
table with a pointer to the checkpoint that closed it; 54 tests plus two.

## A28 Name the units on both window keys

`CONFIG_REFERENCE.md` now states the coupling beside two keys both set to 20 and
counting different things. `price.reload_window_days` counts calendar days back
from the run date, non-session responses tolerated. `freshness.settled_window_days`
counts dates present in `price_daily`, which are trading dates by construction.

Say so in each description, and add the one sentence that makes the coupling
readable: the requirement is that a date finishes settling before it ages out of
reload reach, not that the two windows match. Twenty calendar days is about
fourteen trading dates against an observed settling period of more than
twenty-four hours, so the margin is large and deliberate rather than incidental.

**Done when:** both key descriptions name their unit; the coupling sentence states
the requirement as reload reach outlasting the settling period; neither value
changes.

---

## 8. 2026-08-07 15:59:18 UTC, build session `[issued]`

## Widen the conflict-target audit to every table, once

The audit written for this phase covers the eight tables it writes. `order` and
`position` were just shown to have no unique index on a writable column, which
means they cannot be upserted at all, which means D-68's surrogate-key clause
lands on them at phase 5 with their grain undecided. The same may be true of
`fill`, `dossier`, `attribution`, `candidate_set`, `proposal` and anything else
carrying an identity key.

Run the existing query with the table filter removed, over all thirty-three.
Record the result in `PROGRESS.md` as a table of every store, its upsertable
grain, and "none today" where there is none. That is one query and one table, and
it converts a class of mid-checkpoint discovery into something a future phase
reads before it starts.

Do not add indexes for tables no phase writes yet. The point is to know, not to
pre-build. Each phase adds the index for the tables it writes, in the migration
that first writes them, exactly as this phase did.

**Done when:** `PROGRESS.md` carries a row for every one of the thirty-three
stores naming its upsertable grain or stating there is none; the eight this phase
writes match what their checkpoints declare; no migration is added by this clause.

## The per-checkpoint verification becomes ci.ps1

`dotnet test --no-build` reporting green against a stale binary after a failed
build is open item 9, and it has now been observed rather than predicted. The
three-command sequence in the plan's verification block is what exposes it,
because nothing stops a person running the third command after the second failed.

Replace the three with `powershell -File ./ci.ps1`. It runs the same steps in the
same order, exits non-zero on the first failure, and so cannot reach the test step
after a failed build. It also already drops and re-migrates, which the three
commands do not.

Strike open item 9 in place, with a pointer to this change and the note that the
hazard remains real for anyone who runs `dotnet test --no-build` directly. The
mitigation is that nothing in the corpus now tells them to.

**Done when:** the verification block names `ci.ps1` as the per-checkpoint
command; open item 9 is struck with the pointer; a deliberately broken build
makes `ci.ps1` exit non-zero without printing a test count.

---

## 9. 2026-08-07 16:05:09 UTC, build session `[issued]`

## Phase 7's four are event tables, not snapshots

The carried obligation currently says four stores have no upsertable grain. That
is true and it is the less useful half. Add what kind of problem it is.

`order`, `fill`, `position` and `trade_outcome` are event records. Every other
store in this system is a snapshot keyed on an entity and a date, which is why a
natural grain falls out of it. Two identical orders on one night are not a
duplicate to be collapsed; they are two orders. So a unique index on the row's own
attributes is the wrong instrument, and reaching for one is how the phase starts
badly.

State in the obligation that the likely mechanism is idempotence by run scope,
deleting and reinserting the rows a given portfolio and date own, rather than
idempotence by row identity. State it as the likely mechanism and not the decided
one, since phase 7 authors that when it can see the shape of a fill.

The same distinction is worth one line against `alert`, `headline` and
`cost_ledger`, which are event records too. `researcher_memory` is not, and
`run_log` is append-only by design rather than by omission, which is worth saying
so nobody later reads it as an outstanding gap.

**Done when:** the phase 7 obligation names the event-versus-snapshot distinction
and the run-scope mechanism as likely rather than decided; `run_log`'s row says
append-only by design; no index and no decision is added by this clause.

## ci.ps1 checks that it still mirrors ci.yml

The step list was matched by hand once. Nothing re-checks it, and the script whose
whole purpose is to stand in for CI is the last place a silent divergence should be
possible.

Have `ci.ps1` read `.github/workflows/ci.yml` at the start of a run, extract the
command from each `run:` step in the build job, and assert that each appears in its
own step list. Throw if the workflow file is missing, if no `run:` steps are found,
or if any command has no counterpart. A regex over `run:` lines is adequate and
honest; note in the header that it is deliberately crude and what it would miss.

This is the same correction just made to the guard count, applied one level out:
read the thing being mirrored rather than re-deriving it, and fail loudly when the
source is not where it was expected.

**Done when:** `ci.ps1` fails with a named error if a `run:` command in `ci.yml`
has no counterpart in its steps; adding a step to `ci.yml` and re-running `ci.ps1`
reproduces that failure; a normal run still prints 5 checks, 0 warnings, 0 errors
and 56 tests.

---

## 10. 2026-08-07 16:14:50 UTC, build session `[issued]`

## Every check states how much it checked

`guards.ps1` prints five checks and zero hits. Nothing states, or asserts, how
many files those five checks swept. A path move, an ignore rule, or a change in
how the file list is enumerated would shrink that set and produce an identical
green line. This is the same defect as the count that said four and should have
said five, sitting in the tool that found it.

Have `guards.ps1` print the swept file count on its summary line beside the check
count, and assert that count against the tracked files matching its extensions,
throwing when the two disagree. That makes the sweep's scope self-checking rather
than something a done condition asks a human to eyeball, which is what mine kept
doing.

`ci.ps1` then reads both numbers off that line, the same way it now reads the
check count, so the scope reaches the one place that gates a checkpoint.

**Done when:** `guards.ps1`'s summary names the file count as well as the check
count; removing a source file from the enumeration makes it throw rather than
report a smaller green; `ci.ps1` prints both numbers; a normal run still shows 5
checks, 0 hits, 0 warnings, 0 errors, 56 tests.

## Make the next migrate failure diagnostic

No cause is being claimed and none should be. The change is only that occurrence
three arrives with evidence attached.

On any failure of the drop, create or migrate steps, `ci.ps1` catches and records,
before it exits non-zero: the server's error message text and its SQLSTATE
verbatim rather than a summarised form, and the output of a `pg_stat_activity`
query naming pid, datname, application_name, client_addr and state for every
session. Write it to the same evidence directory the endpoint sweep uses, stamped
with the run time, and name the file in the exit message.

The exit stays non-zero and the behaviour is otherwise unchanged. Nothing is
retried and nothing is worked around, because working around a failure you cannot
explain is how it stops being observable.

Two candidates the message will distinguish immediately, recorded so the third
occurrence is read rather than investigated: a drop refused because a session is
still attached, and a create refused because a session is attached to `template1`.
Neither is asserted here.

**Done when:** a deliberately induced failure, such as holding a session open
against the target database, writes an evidence file containing the SQLSTATE and
the session list and exits non-zero; a passing run writes nothing; the open item
in `PROGRESS.md` names the evidence path so the next occurrence is looked for
there.

---

## 11. 2026-08-07 17:26:08 UTC, build session `[issued]`

proceed with 1.2

---

## 12. 2026-08-07 17:51:54 UTC, build session `[issued]`

if nothing needs to be fixed then proceed with next

---

## 13. 2026-08-07 18:10:44 UTC, build session `[issued]`

proceed

---

## 14. 2026-08-07 18:22:24 UTC, build session `[issued]`

is something running that is calling eohdhd or has something ran as of 7th ?

---

## 15. 2026-08-07 18:25:14 UTC, build session `[issued]`

fix that as a chore and then proceed with 1.4. Do not stop to give report for just  this instance

---

## 16. 2026-08-07 18:35:21 UTC, build session `[issued]`

## filing_date_effective becomes nullable, with a narrower constraint replacing NOT NULL

`0002` declares `filing_date_effective` nullable and adds:

    CHECK (filing_date_effective IS NOT NULL
           OR filing_date_unknown_reason <> 'none')

A row may lack an effective date only when the provider's own filing date was
unusable. That catches the case `NOT NULL` was pointing at, a row losing its date
to a bug, while permitting the case `NOT NULL` could only handle by writing a
date that is not true.

Null means no usable filing date and none derivable. `filing_date_unknown_reason`
continues to say why the provider's date failed, and remains `none` only where no
substitution was needed. The zero-clean-gaps population is therefore
`filing_date_unknown_reason <> 'none' AND filing_date_effective IS NULL`, which
the sign-off query already planned reads without change. No new reason value and
no new column.

`SCHEMA.md`: strike `NOT NULL` in place with the pointer, and state what a null
means in the same sentence, so the next reader does not infer it from a
constraint.

**Done when:** the column is nullable and the check exists in the database; an
attempt to write a null alongside reason `none` is refused by the database rather
than by code; a ticker with zero clean gaps writes a row whose effective date is
null and whose reason names the provider failure; a read filtering
`filing_date_effective <= date` returns no such row; `SCHEMA.md` carries the
struck `NOT NULL` with the pointer and the meaning of null beside it.

## Rename sweeps state their exclusions

My done condition was unsatisfiable and you were right not to force it. I wrote
"returns nothing over the repository" without accounting for two files the corpus
forbids editing.

The general form, so later renames do not rediscover this: a repository-wide
rename sweep excludes `prompts/spent/`, applied migration files, and struck text
by construction, and naming those three exclusions is part of writing the
condition rather than a concession made afterwards. A sweep that cannot return
zero is not a done condition.

Record the three survivors and why each is permitted in the commit body, so the
next reader sees a decision rather than an incomplete rename.

**Done when:** A1.a's condition in `PROGRESS.md` reads with the three exclusions
named; the sweep run with those exclusions returns nothing; the commit body lists
the three permitted survivors and the rule that permits each.

---

## 17. 2026-08-07 18:43:33 UTC, build session `[issued]`

## filing_date_unknown_reason must not itself be absent

`0002` declares `filing_date_unknown_reason` NOT NULL, with a CHECK restricting it
to exactly the four states. If a SQL null is currently how the "provider's filing
date was null" state is written, give that state a string of its own, so the
column holds four values rather than three values and an absence.

The fix is the same either way, because the problem is not which meaning null
carries. It is that null carries two meanings at once, a stated state and an unset
column, and no constraint can separate them.

Two things follow. The check I gave becomes total: `reason <> 'none'` is true or
false for every row and never null, so an undated row with no reason is refused by
the database rather than ignored by it. And the sign-off query gains no null
group, so every row in `fundamental_snapshot` is attributed to a reason someone
stated rather than to whichever of the two things a null meant that night.

**Done when:** the reason column is NOT NULL with a CHECK naming exactly the four
states; a write with a null effective date and a null reason is refused by the
database rather than accepted; the three-case verification runs again with a
fourth row, effective date null and reason set to the provider-null state, which
is accepted; `SELECT filing_date_unknown_reason, count(*) FROM fundamental_snapshot
GROUP BY 1` returns no null group.

---

## 18. 2026-08-07 18:52:33 UTC, build session `[issued]`

proceed

---

## 19. 2026-08-07 19:42:19 UTC, build session `[issued]`

proceed

---

## 20. 2026-08-08 00:00:23 UTC, build session `[issued]`

so we will be stuck until eodhd resets ?

---

## 21. 2026-08-09 03:05:35 UTC, build session `[issued]`

EODHD is back up. Can you check and if it is available can you tell what is needed from here

---

## 22. 2026-08-09 03:07:44 UTC, build session `[issued]`

the budget is 100k calls per day. Can you manage with that ?

---

## 23. 2026-08-09 03:17:01 UTC, build session `[issued]`

proceed

---

## 24. 2026-08-09 04:32:04 UTC, build session `[issued]`

you can run until we hit 99.99k. No need to stop at 77k if you need to run more.

---

## 25. 2026-08-09 04:59:53 UTC, build session `[issued]`

WHatever is left, does it need EODHD calls ?

---

## 26. 2026-08-09 05:02:29 UTC, build session `[issued]`

Carry on with whatever can be done without calling EODHD. Rest will have to be done tomorrow. Do not start anything that requires calls. Give me report of exactly what todos are left

---

## 27. 2026-08-09 05:10:03 UTC, build session `[issued]`

## The form4 shortfall, as a decision

Author at the next free number:

**D-nn A short page from an exhausted server is recorded, not fatal.** `ACTIVE`

`sec-filings/{t}/form4` reports a `meta.total` higher than the number of rows it
delivers. Measured over 250 tickers: 43 short, 104 rows missing of 101,325, and
all 43 had walked the server's own pagination to its end. The case the paging
check was written for, a client that stops asking while pages remain, occurred
zero times.

Two failures were sharing one exception and they separate cleanly.

A loop that terminates while `links.next` is present is this client failing to
ask. It stays fatal, at exactly its current strictness, because nothing about the
provider changes what our own defect deserves.

A loop that terminates because `links.next` is absent, having collected fewer
distinct rows than `meta.total`, is the provider disagreeing with itself. Asking
again cannot recover the rows, and halting means a universe pass can never
complete. It is recorded and the stage continues.

No threshold is set and none is to be added later without evidence gathered after
this decision was written. Every candidate value would have been chosen against
data already seen, which is what §11 forbids. The rule is structural instead: the
distinction is which condition ended the loop, and that is observable rather than
judged.

The run log carries, per run, the count of tickers that under-delivered and the
total row shortfall. Today's figures are the baseline. Tolerating a discrepancy
without measuring it is how it stops being visible.

Where the shortfall sits is recorded per affected ticker from the pages already
collected: a final page below `page[limit]` puts the missing rows at the oldest
end of a history and outside every trailing-90-day window, while a short interior
page puts them inside one. If the former dominates, `insider_net_90d_usd` and
`distinct_buyer_count` are untouched and phase P's S4 base rate can be answered
without qualification.

**Done when:** a client stopping early still throws; a ticker whose server
under-delivers ingests what was sent and the stage completes; both counts appear
in the run log; the evidence file names final-or-interior per affected ticker;
`PROGRESS.md` states which pattern dominates.

## INVARIANT 16 asserts, rather than excludes

Replace the exclusion list with two positive checks.

Every column whose name matches the monetary pattern is `numeric`. State the
expected count in advance, so the check cannot pass over an empty match set. The
pattern covers at least `_usd`, `price`, `value`, `cap`, `cost`, `equity`, `pnl`,
`dollar` and `amount`; extend it as the schema grows rather than extending an
exclusion list.

The set of `real` and `double precision` columns in the database equals the set
`SCHEMA.md` declares as non-monetary. The guard reads the document, exactly as
the 0.4 conformance test now reads its writer declarations. Adding a `real`
column then requires declaring it in the schema doc, which is where a reader
would look, rather than in a script, which is where nobody does.

The second check is what makes phase 2's forty columns a documentation act rather
than a guard-suppression act.

**Done when:** `guards.ps1` carries no per-file exclusion list for INVARIANT 16;
the monetary check states its expected count and finds it; adding an undeclared
`real` column to a scratch migration makes the check fail naming the column;
adding it to `SCHEMA.md` makes it pass; the summary still reads 5 checks over the
tracked file count.

---

## 28. 2026-08-09 15:06:20 UTC, build session `[issued]`

Can you check if EODHD  calls are reset or not at the moment.  Check the leftover  calls. It should be at 100k if it has been reset

---

## 29. 2026-08-09 15:27:27 UTC, build session `[issued]`

Can you  push this local branch to remote so we dont lose any work

---

## 30. 2026-08-09 15:48:51 UTC, catalogue-deviations session `[issued]`

# Record four catalogue deviations as findings, and change nothing else

Findings only. No fix, no amendment, no decision.

`ARCHITECTURE.html` is not edited by this work, and neither is `RUNBOOK.md`.
Nothing is struck. The D-74 and D-75 text drafted earlier is not authored. If the
sign-off review agrees with the readings below, the amendment lands in the batched
pass with two independent judgements behind it instead of one; if it disagrees,
nothing has to be undone.

Each entry separates two things and keeps them separate. The **observation** is
checkable from the files named and is not in dispute. The **reading** is one
judgement, formed outside this session, and the review's own conclusion is what
settles it. Write them so a reviewer can disagree with a reading without having to
overturn anything.

Add them to `PROGRESS.md` under a heading of their own, in whatever form that
file's existing findings use, and say in one line at the top of the section why
they are findings rather than fixes: the corpus is not amended ahead of the review
that checks the code against it.

## Four ingest components read a table the catalogue does not give them

**Observation.** `FundamentalsIngestor.ReadSet` is `["price_daily", "security"]`.
`SentimentIngestor`, `FlowIngestor` and `EventsIngestor` each declare
`["security"]`. `ARCHITECTURE.html` §3 gives their Reads as, in order,
"Fundamentals endpoint, `events`"; "Sentiment endpoint"; "Insider, ownership";
"Calendar, splits, dividends". `DeclaredAccess` enforces the declared sets at
runtime, so these reads happen.

**Reading.** The catalogue is incomplete and the code is doing the only thing it
can. A per-ticker endpoint needs a ticker list and the universe lives in
`security`. The Reads column already mixes endpoints and tables elsewhere, so
listing only an endpoint is an omission rather than a convention.

The fundamentals row is the one worth a second look. Its two reads serve different
purposes, the pool from `price_daily` and the rotation order from `security`, and
the catalogue never said where a pool comes from at all. That silence is what let
the pool be drawn from `security` and freeze coverage at 679 with no error and
plausible output.

## The rotation does not read events, which the catalogue says it does

**Observation.** §3 gives `FundamentalsIngestor` the description "Rolling
rotation, earnings jump the queue" and lists `events` in its Reads.
`FundamentalsIngestor.ReadSet` does not contain `events` and no code path reads
it. The deferral reason recorded during the build was that `events` arrived at
1.8, which it has.

**Reading.** The catalogue is right and the code is incomplete. This is unbuilt
work rather than a deviation to correct in the document, and it is larger than one
line: a round-robin rotation combined with the `filing_date_effective` gate can
leave a period unread for weeks after it became public, so a screen ranking a name
the week after its results would rank on the previous quarter. Whatever the review
concludes, do not strike that line to match the code.

## The flow cadence contradicts itself across three documents

**Observation.** §3 gives `FlowIngestor` Runs as "Weekly".
`NightlyRun.EveningOrder` contains `FlowIngestor` between fundamentals and events.
`RUNBOOK.md` line 17 reads "17:45 | Fundamentals, flow, events".
`PROGRESS.md` line 332 prices the same component at "20,000+ | weekly". The code
followed RUNBOOK and no record says it chose.

**Reading.** Two authored documents contradict and CLAUDE.md §3 says report rather
than resolve, so the resolution owes a decision either way. On substance, nightly
looks right for what the component now is: Form 4 filings arrive within two
business days and the flow screen reads a trailing ninety-day window, so a weekly
ingest is missing its most recent six days. Weekly was set when the component also
carried short interest and wrote `flow_daily`, and was not revisited after D-58
and D-61. The counter-argument is that the same stage fetches institutional
holdings, which are reported quarterly and are roughly half its call cost.

This is the reading with the least behind it. It rests on how the ninety-day
window is actually computed, which this finding does not establish.

## Nothing checks a declared read against the catalogue

**Observation.** `RegistryNameTests` asserts every registered component name
appears in §3. `WriteOwnershipConformanceTests` asserts writes against
`SCHEMA.md`'s writer declarations in both directions. `ReadSet` is asserted only
against hardcoded literals inside each component's own test file, for example
`Assert.Equal(["security"], stage.ReadSet)`.

**Reading.** This is why three of the four deviations above went unnoticed and one
was caught by eye. Names have a conformance path and writes have one; reads have
none, and the per-component assertions lock a drifted declaration in rather than
catching it. Phase 2 adds eleven components to a column nothing checks. The test
itself is batched-pass work and is not built here.

## Done when

- `PROGRESS.md` carries the four findings under one heading, each with its
  observation and its reading kept separate and labelled.
- The section opens with the one line on why they are findings.
- `git diff --stat` shows `PROGRESS.md` changed and `docs/ARCHITECTURE.html`,
  `docs/RUNBOOK.md` and `docs/DECISIONS.md` untouched.
- No decision is authored and no carried obligation is added.
- A grep for "earnings jump the queue" in `ARCHITECTURE.html` returns one hit,
  unstruck.
- `ci.ps1` green, unchanged at 136 tests.

---

## 31. 2026-08-09 15:57:25 UTC, catalogue-deviations session `[issued]`

# A fifth finding, and a correction to the second reading

Same section, same form, still findings and not fixes. `ARCHITECTURE.html`
untouched.

## The rotation stops rotating once coverage completes

**Observation.** `FundamentalsIngestor.CandidatesAsync` defines `fetched` at
line 230 as every ticker with any row in `fundamental_snapshot`. The selection at
lines 258 to 261 orders never-fetched, then fetched-and-in-universe, then the
rest, then by ticker ordinal, and takes `fundamentals.max_tickers_per_run`. No
date is read anywhere in the selection.

`PROGRESS.md` records the candidate pool as 3,184 with zero unfetched. With the
never-fetched group empty, the same alphabetically-first `max_tickers_per_run`
names are selected on every subsequent run.

The class comment at line 22 states that until `events` arrives "the rotation is
staleness-ordered only". No staleness ordering exists in the code. The comment at
lines 251 to 256 names this failure for the coverage phase, "ordering by ticker
alone re-selects the same head every run", and its clause "Coverage before
freshness while coverage is incomplete" implies a freshness ordering to follow.

The coverage line in the run log reports never-fetched and new-in-selection. Both
read zero once coverage is complete, which is the goal state for coverage.
Nothing reports how many universe members were refreshed.

**Reading.** This is a missing mechanism rather than a stale comment, and it is
the most material finding of this set. At the measured pool and run size, roughly
five hundred names refresh on every run and it is the same five hundred; the
remaining two thousand six hundred and eighty-four hold whatever they were first
fetched with. The quality and value screen ranks on these figures, so the age of a
name's fundamentals would depend on its first letter, which is arbitrary but
systematic rather than random.

The catalogue's "earnings jump the queue" is the priority rule on top of an
ordering that does not exist underneath it, so finding two and this one are the
same gap seen from two ends.

## Correction to finding two's reading

That reading describes the rotation as round robin. It is not. Replace the phrase
with a fixed head after coverage completes, and note that the correction makes the
consequence larger rather than smaller: a round robin would eventually return to
every name.

Keep the original wording visible in the same form the eleven-versus-four
correction used, so the reviewer sees what it said and what it now says.

## Done when

- Five findings under the heading, the fifth with its observation and reading kept
  separate.
- Finding two's reading names a fixed head rather than a round robin, with the
  original visible.
- `git diff --stat` still shows `docs/PROGRESS.md` alone.
- No decision, no carried obligation, no code change, and
  `fundamentals.max_tickers_per_run` untouched.
- `ci.ps1` green at 136 tests.

---

## 32. 2026-08-09 16:11:49 UTC, catalogue-deviations session `[issued]`

commit

---

## 33. 2026-08-10 00:36:17 UTC, build session `[issued]`

Can you  check the EODHD status ? ALso can we try to wrap up the calls to EODHD today  within the limits or is that not possible ?

---

## 34. 2026-08-10 02:08:53 UTC, build session `[issued]`

What do i  need to handover to fresh session

---

## 35. 2026-08-10 02:14:00 UTC, build session `[issued]`

Why didnt you archive the prompts for what has been built so far. Can you merge everything under phase 1 in spent prompts before i handover please

---
