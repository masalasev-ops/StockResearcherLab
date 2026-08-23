-- 0013_attempt_sweep_marker.sql
--
-- The sweep marker leaves the rotation column, on C03 and C05. Closes open item 44,
-- which item 62 was merged into, and it is the split D-99 named and deferred in its
-- own words: "This is the thing to split if it ever bites, and it is not split now."
--
-- WHAT WAS WRONG. `last_attempted_date` served two purposes at once on the only two
-- tables where both paths write. It was the rotation's freshness ordering, read
-- `WHERE last_attempted_date < asOf` and sorted ascending so the oldest goes first,
-- and it was simultaneously the sweep's done-marker, read `WHERE last_attempted_date
-- = asOf` to compute what a resumed sweep still owes. The two readings disagree
-- about what a date in that column means, and each path undid the other's work:
--
--   A ticker the night touched had its sweep stamp overwritten with the run date,
--   fell back into the sweep's remaining set and was walked again at ten units.
--
--   A ticker the sweep had just fetched whole carried the range end, which sorts
--   AHEAD of any later nightly date under an ascending order, so the rotation
--   preferred precisely the names the sweep had just paid for.
--
-- WHY NOW RATHER THAN WHEN D-99 WROTE IT DOWN. D-105 refuses a range end past the
-- ingest frontier, and 2026-08-13 is past it. That was the only end C03 and C05 were
-- stamped for, so every future range end matches nothing and both pools re-dispatch
-- whole: 20,068 tickers at ten units is 200,680, and 2,864 members at the 83.5 a
-- member 3.9 measured is about 239,144. Roughly 440,000 units and about 4.9 provider
-- days. The trigger is live rather than prospective.
--
-- THE MARKER MEANS "SWEPT THROUGH AT LEAST THIS DATE", AND THAT IS THE HALF A COLUMN
-- ALONE DOES NOT FIX. Splitting the column and keeping the equality test leaves the
-- backfilled marker at 2026-08-13 against a corrected end of 2026-08-12, matching
-- nothing and re-dispatching exactly as before. The reads therefore become
-- `swept_through_date >= <range end>`. This is also the only form that answers item
-- 44's third named trigger, "any later frontier correction that moves a range end":
-- an equality test fails on every such correction by construction, where coverage
-- through a date is still coverage when the end moves back.
--
-- THE STAMP ITSELF IS UNCHANGED AND STAYS THE RANGE END. D-99's asymmetry is
-- deliberate and documented per stage: C03's and C05's nightly and sweep calls are
-- the same call, where C02's, C04's and C06's differ in depth, which is why those
-- three key on the range start and these two do not. Item 44 says so directly, that
-- "what is at issue is the shared column rather than the choice of stamp". Changing
-- which date is stamped would be reopening that decision and it is not reopened here.
--
-- THE BACKFILL MOVES THE VALUE RATHER THAN COPYING IT, and the difference is the
-- defect. Every row in both tables was written by a sweep: measured at the sign-off
-- review, all 20,068 `fundamental_fetch_attempt` rows and all 2,864
-- `flow_fetch_attempt` rows carry 2026-08-13, the sweep's range end, and no other
-- value appears in either table. Copying would leave those sweep stamps sitting in
-- the rotation column, which is the nightly half of item 44 preserved rather than
-- closed. Moving them states what is true: the sweep covered these tickers, and the
-- night has never attempted them. On an empty database both statements are no-ops,
-- which is what CI applies them against.
--
-- SO `last_attempted_date` BECOMES NULLABLE, and null is the ordinary case here
-- rather than an exceptional one. It means the nightly rotation has never attempted
-- this ticker, which is a different fact from an absent row, meaning nothing has
-- attempted it at all [CLAUDE.md section 6]. The rotation reads
-- `WHERE last_attempted_date < asOf`, so a null row is excluded and lands in the
-- never-attempted tier, which is where a ticker the night has never touched belongs.
-- The sweep can then insert a row for a pool member it has just fetched without
-- claiming a nightly attempt that did not happen.
--
-- NO INDEX ON THE NEW COLUMN. The rotation index exists because that read is ordered
-- and runs over the whole pool on every night. The sweep read is an unordered
-- membership test over 20,068 and 2,864 rows, run once at the head of a sweep, and an
-- index built for it would be an index nothing needed [CLAUDE.md section 6, build
-- what is needed now]. Recorded so its absence reads as a decision.
--
-- Snapshot-first: 0001 to 0012 are not edited. Every statement is guarded, so a
-- second run changes nothing and ci.ps1's second migrate still prints nothing to
-- apply.

-- Writer: FundamentalsIngestor. Grain unchanged, one row per ticker.
ALTER TABLE fundamental_fetch_attempt
    ADD COLUMN IF NOT EXISTS swept_through_date date NULL;

-- Writer: FlowIngestor. Grain unchanged, one row per ticker.
ALTER TABLE flow_fetch_attempt
    ADD COLUMN IF NOT EXISTS swept_through_date date NULL;

-- The constraint goes before the move, because the move writes the null it forbids.
ALTER TABLE fundamental_fetch_attempt
    ALTER COLUMN last_attempted_date DROP NOT NULL;

ALTER TABLE flow_fetch_attempt
    ALTER COLUMN last_attempted_date DROP NOT NULL;

-- The move. Guarded on the new column still being null, so a re-run is a no-op and
-- a row a nightly path has since written is not disturbed.
UPDATE fundamental_fetch_attempt
   SET swept_through_date = last_attempted_date,
       last_attempted_date = NULL
 WHERE swept_through_date IS NULL
   AND last_attempted_date IS NOT NULL;

UPDATE flow_fetch_attempt
   SET swept_through_date = last_attempted_date,
       last_attempted_date = NULL
 WHERE swept_through_date IS NULL
   AND last_attempted_date IS NOT NULL;
