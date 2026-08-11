-- 0006_fundamental_fetch_attempt.sql
--
-- The fundamentals rotation stops rotating once coverage completes, and this is the
-- store that lets it keep going.
--
-- C03 ordered never-fetched first, where "fetched" meant any row in
-- fundamental_snapshot. Once the pool is covered that group is empty and the same
-- alphabetically-first names are selected on every run afterwards, for ever.
-- Measured: capital_expenditures existed for 482 tickers running contiguously from
-- A.US to CCBG.US, and two consecutive runs wrote an identical 44,365 rows over an
-- identical 500 tickers.
--
-- WHY A TABLE RATHER THAN A COLUMN, and both alternatives fail on the same case.
--
-- A fetched_at column on fundamental_snapshot only moves when rows are written. A
-- ticker whose fetch returns nothing writes nothing, so its freshness never moves
-- and it sits at the front of the rotation on every run afterwards. That is the
-- fourteen-404s defect from the flow ingest, one component over: "never fetched"
-- conflates not yet attempted with attempted and empty.
--
-- A column on security fails differently. C03's pool is the candidate set, which is
-- deliberately broader than the universe [1.8], so a pool member with no security
-- row would have nowhere to record an attempt and would never leave the head of the
-- rotation.
--
-- So the record is of the ATTEMPT, keyed on ticker, written whether or not the fetch
-- yielded rows. last_yield_date is what keeps the two absences distinguishable: null
-- means attempted and never yielded, which is a different fact from absent, which
-- means never attempted [CLAUDE.md section 6].
--
-- NO COUNTER COLUMN. An attempts tally would increment on a re-run of the same date
-- and D-68 requires every stage write to be idempotent on the table's own grain. The
-- three columns below are each a function of the last attempt alone, so a second run
-- over one date writes what the first did.
--
-- HOW THIS STAYS DETERMINISTIC. The rotation reads attempts strictly before the run
-- date, so a re-run of one date sees the state the first run saw and selects the same
-- names [CLAUDE.md section 6]. Advancing happens between dates rather than between
-- runs, which is the same point-in-time discipline every fundamental read already
-- uses on filing_date_effective [INVARIANT 12, INVARIANT 13].
--
-- Snapshot-first: 0001 to 0005 are not edited, this file sits beside them. The
-- statement is guarded, so a second run changes nothing.

-- Grain: one row per ticker. Writer: FundamentalsIngestor.
CREATE TABLE IF NOT EXISTS fundamental_fetch_attempt (
    ticker              text    NOT NULL PRIMARY KEY,
    last_attempted_date date    NOT NULL,
    last_yield_date     date    NULL,
    rows_last_attempt   bigint  NOT NULL
);

-- The rotation orders by this and reads it for the whole pool on every run, so the
-- ordering is served rather than sorted after a sequential scan. Small table, and it
-- is the only read pattern there is.
CREATE INDEX IF NOT EXISTS fundamental_fetch_attempt_rotation_ix
    ON fundamental_fetch_attempt (last_attempted_date, ticker);
