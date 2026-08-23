-- 0014_percentile_cell_daily.sql
--
-- The population a percentile was ranked against, kept rather than discarded [D-107].
--
-- WHAT IS MISSING TODAY. C11 computes each metric's non-null population in its
-- (size_bucket, sector) cell and in its bucket inside one window function, uses both to
-- pick a scope, writes the percentile and keeps neither count. What survives is one run
-- log line per date aggregating every cell into a total. A percentile without the size
-- of the population behind it is unreadable: one over three members and one over eighty
-- are the same number on the page, and the fifteen-member fallback is theoretical
-- rather than visible.
--
-- WHY NOT RECOMPUTE IT IN A READER. The count is not count(*) over the cell's members;
-- it is the non-null count of that metric in that cell, under C11's null-sector rule,
-- its LEFT JOIN and its is_active handling. A reader reproducing those four rules is a
-- second implementation of the cell rule whose failure is a plausible number, which is
-- the defect class phase 2 and phase 3 kept finding.
--
-- THE GRAIN IS THE CELL, NOT THE ROW. The population is a property of the cell, and one
-- row per member per metric would restate it thousands of times over on the two largest
-- tables in the store. Thirty metrics by three buckets by roughly a dozen sectors by
-- 1,260 dates is about 1.4 million rows; per member it would be hundreds of millions.
--
-- bucket_members REPEATS ACROSS THE SECTORS OF A BUCKET. That redundancy is accepted so
-- a reader takes one row per metric rather than two, which is the whole shape of the
-- panel this exists for.
--
-- ranked_scope IS STORED RATHER THAN DERIVED. Comparing a count against a floor and
-- labelling the answer is a computation, and the page does not compute. Storing which
-- of the two populations the percentile actually came from moves that decision back to
-- the component that made it.
--
-- A NULL SECTOR FORMS NO CELL and goes straight to the bucket fallback [METRICS.md
-- section 6.4], so it needs a row with no sector. Postgres does not allow a null in a
-- primary key, so the key is a unique index over coalesce(sector, '') instead, and the
-- column stays nullable because '' is not a sector and storing one would invent a
-- thirteenth.
--
-- metric IS SUFFICIENT IN THE KEY WITHOUT THE TABLE BESIDE IT because the thirty metric
-- names are distinct across the four source tables today. source_table is carried so a
-- reader knows where to look, and a future collision is a failed key rather than a
-- silent overwrite.
--
-- NO MONEY HERE. Counts and a floor, so INVARIANT 16's expected monetary column count
-- does not move.
--
-- Snapshot-first: 0001 to 0013 are not edited. The statements are guarded, so a second
-- run changes nothing and ci.ps1's second migrate still prints nothing to apply.

-- Grain: date by size bucket by sector by metric. Writer: PercentileEngine.
CREATE TABLE IF NOT EXISTS percentile_cell_daily (
    date           date    NOT NULL,
    size_bucket    text    NOT NULL,
    sector         text    NULL,
    metric         text    NOT NULL,
    source_table   text    NOT NULL,
    cell_members   integer NULL,
    bucket_members integer NOT NULL,
    min_members    integer NOT NULL,
    ranked_scope   text    NOT NULL,
    CONSTRAINT percentile_cell_daily_scope_ck
        CHECK (ranked_scope IN ('cell', 'bucket', 'none'))
);

-- The key. coalesce rather than a nullable primary key, for the null-sector row.
CREATE UNIQUE INDEX IF NOT EXISTS percentile_cell_daily_key_ux
    ON percentile_cell_daily (date, size_bucket, coalesce(sector, ''), metric);

-- The page reads one date and one cell at a time, so the date leads. This is 0007's
-- lesson applied at the point the table is created rather than after it is 100 MB.
CREATE INDEX IF NOT EXISTS percentile_cell_daily_date_ix
    ON percentile_cell_daily (date, size_bucket, metric);

-- Grain: one row per source table. Writer: PercentileEngine.
--
-- WHAT THE PASS COVERED, read as coverage and never as equality [D-106]. Without it a
-- date the pass has not reached and a cell that does not exist are the same empty
-- result, and the panel would render "no cell" for a date whose cells simply have not
-- been written yet. That is the reading this table exists to separate.
--
-- ONE ROW PER SOURCE TABLE rather than one row for the store, because the pass writes
-- per source and a partial pass is a real state: four statements per date, and a halt
-- between them leaves three sources further on than the fourth.
--
-- The pass runs date-descending, so the covered set is a contiguous suffix and two
-- dates describe it. Ascending would need a set of ranges, which is a second thing to
-- get right for no gain.
CREATE TABLE IF NOT EXISTS percentile_cell_coverage (
    source_table text NOT NULL PRIMARY KEY,
    covered_from date NOT NULL,
    covered_to   date NOT NULL
);
