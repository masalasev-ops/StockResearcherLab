-- 0008_flow_fetch_attempt.sql
--
-- C05's rotation gets its own attempt record [D-95].
--
-- FlowIngestor.SelectionFor carried C03's defect unchanged: never-fetched first,
-- where "fetched" meant any row in insider_transaction, then ticker ordinal. Once
-- the pool is covered that first group is empty and the same alphabetically-first
-- names are selected on every run afterwards, for ever. The 14 of 250 that answer
-- 404 Symbol not found never write a row, so they stay never-fetched and are
-- re-asked on every run, and 3.1 measured what that costs: a 404 is billed at 10
-- units, so the frozen head spends 140 units a night on calls that cannot succeed.
--
-- D-91 fixed this for C03 and named C05 as not closed by it. This closes it, in the
-- shape D-91 chose, and the three reasons carry over unchanged:
--
--   the record is of the ATTEMPT, written for every selected ticker whether or not
--   the fetch yielded rows, so a ticker that returns nothing still moves down the
--   rotation;
--
--   attempts are read STRICTLY BEFORE the run date, so a re-run of one date sees
--   the state the first run saw and selects the same names. The rotation advances
--   between dates and never between runs, which is what keeps the stage a pure
--   function of its date and config version [CLAUDE.md section 6];
--
--   last_yield_date null means attempted and never yielded, which is a different
--   fact from an absent row, which means never attempted [CLAUDE.md section 6].
--
-- A SHARED TABLE WAS REJECTED. Two components writing one table is two claims on
-- one component-table-operation triple [INVARIANT 10], and the conformance test
-- would catch it. What is shared is the ordering function, RotationSelection, so
-- the two rotations cannot drift apart the way the code and the catalogue did.
--
-- NO COUNTER COLUMN, for the reason 0006 gives: an attempts tally would increment
-- on a re-run of the same date and D-68 requires every stage write to be idempotent
-- on the table's own grain. Each column below is a function of the last attempt
-- alone.
--
-- Snapshot-first: 0001 to 0007 are not edited. The statements are guarded, so a
-- second run changes nothing.

-- Grain: one row per ticker. Writer: FlowIngestor.
CREATE TABLE IF NOT EXISTS flow_fetch_attempt (
    ticker              text    NOT NULL PRIMARY KEY,
    last_attempted_date date    NOT NULL,
    last_yield_date     date    NULL,
    rows_last_attempt   bigint  NOT NULL
);

-- The rotation orders by this and reads it for the whole pool on every run, so the
-- ordering is served rather than sorted after a sequential scan. Same shape as
-- fundamental_fetch_attempt's index and for the same reason: it is the only read
-- pattern there is.
CREATE INDEX IF NOT EXISTS flow_fetch_attempt_rotation_ix
    ON flow_fetch_attempt (last_attempted_date, ticker);
