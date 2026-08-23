-- 0010_price_fetch_attempt.sql
--
-- C02's attempt record, and what a ticker-partitioned sweep resumes on [3.6].
--
-- THE RECORD REPLACES A POSITION. The price sweep resumed from a ticker parsed back
-- out of the run log line, written by whichever of three exits the run took. Three
-- real failures in one day showed what that is worth: the first recorded no position
-- because the fault arrived in the allowance gate's own call rather than inside the
-- dispatch loop, the second recorded no position because a test fixture's cleanup had
-- deleted the row carrying it, and the third recorded one and resumed correctly. A
-- kill -9 records nothing at all, the run log write being the last thing a run does.
--
-- An attempt row is written as the sweep goes, so every exit resumes identically and
-- none of them is consulted. What the sweep has done is in the table rather than in a
-- sentence about the table.
--
-- WHY NOT "TICKERS ALREADY IN price_daily". C02's nightly reload has been loading
-- every admitted name since phase 2, where the table held 13,091,293 rows over 274
-- dates, roughly 47,800 tickers against a pool of 50,785 [PROGRESS.md]. Presence
-- therefore says almost nothing about whether a ticker was swept: a swept ticker has
-- years of bars and a nightly-only ticker has the last twenty dates, and presence
-- cannot tell them apart. Resuming on it would skip most of the pool.
--
-- The attempt record also closes the leak that predicate carries. A ticker the
-- provider does not carry answers 404, writes no bars, and would never gain a
-- presence row, so it would be re-fetched on every run for ever. That is the
-- fourteen-404s defect 0008 measured at C05, and the answer here is 0006's: the
-- record is of the ATTEMPT, written for every dispatched ticker whether or not it
-- yielded.
--
-- last_yield_date null means attempted and yielded nothing, which is a different fact
-- from an absent row, which means never attempted [CLAUDE.md section 6].
--
-- NO COUNTER COLUMN, for the reason 0006 and 0008 both give: a tally would increment
-- on a re-run and D-68 requires every stage write to be idempotent on the table's own
-- grain. Each column below is a function of the last attempt alone.
--
-- A SHARED TABLE WAS REJECTED for the third time, on 0008's argument unchanged. Two
-- components writing one table is two claims on one component-table-operation triple
-- [INVARIANT 10] and the conformance test would catch it.
--
-- WHAT DIFFERS FROM ITS TWO NEIGHBOURS. Those two order a rotation and read attempts
-- strictly before the run date. This one is a set difference and reads attempts AT one
-- date: a range run stamps every attempt with the range start, so the remaining set is
-- the pool minus the tickers carrying an attempt at that date. The range start is what
-- is stable across the days a sweep spans, where the range end defaults to today and
-- moves under a sweep re-invoked the next morning.
--
-- Snapshot-first: 0001 to 0009 are not edited. The statements are guarded, so a second
-- run changes nothing.

-- Grain: one row per ticker. Writer: PriceIngestor.
CREATE TABLE IF NOT EXISTS price_fetch_attempt (
    ticker              text    NOT NULL PRIMARY KEY,
    last_attempted_date date    NOT NULL,
    last_yield_date     date    NULL,
    rows_last_attempt   bigint  NOT NULL
);

-- The sweep reads every ticker attempted at one date on every run, so the index makes
-- that an index-only scan rather than a sequential one over the whole table. Same
-- shape as its two neighbours' indexes, serving an equality on the leading column here
-- rather than an ordering.
CREATE INDEX IF NOT EXISTS price_fetch_attempt_sweep_ix
    ON price_fetch_attempt (last_attempted_date, ticker);
