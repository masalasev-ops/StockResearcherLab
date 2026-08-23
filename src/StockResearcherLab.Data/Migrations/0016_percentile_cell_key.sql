-- 0016_percentile_cell_key.sql
--
-- 0014's key collapsed an empty-string sector into a null one. They are different
-- cells and C11 ranks them differently, so the key has to tell them apart [D-107].
--
-- WHAT HAPPENED. 0014 keyed on (date, size_bucket, coalesce(sector, ''), metric),
-- reasoning that a null sector forms no cell and Postgres does not allow a null in a
-- primary key. That is true and the substitution is not: `security_daily.sector` can
-- also hold the empty string, so two genuinely different cells mapped to one key.
--
-- IT FAILED LOUDLY RATHER THAN SILENTLY, which is the only good thing about it. The
-- first real range run stopped at 2026-07-01 with "ON CONFLICT DO UPDATE command cannot
-- affect row a second time", because that date carries one `mid` name with an
-- empty-string sector and three with none. Had the key been merely wrong in a way
-- Postgres tolerated, one of the two cells would have overwritten the other on every
-- date and the panel would have shown a population belonging to a cell the name was not
-- ranked in.
--
-- THE TWO ARE DIFFERENT CELLS AND THAT IS C11's READING, NOT AN OPINION.
-- `CellQualifies` tests `sector IS NOT NULL`, so an empty-string sector is a real cell
-- of its own; on the measured date it is a cell of one, which fails the fifteen-member
-- floor and falls back to the bucket. A null sector forms no cell at all and goes
-- straight to the same fallback [METRICS.md section 6.4]. The two arrive at the same
-- percentile by different routes, and the whole point of this store is that a reader can
-- see which route.
--
-- NULLS NOT DISTINCT rather than a sentinel. Postgres 15 and later can treat nulls as
-- equal inside a unique index, which is exactly the semantics wanted: one row per
-- (date, bucket, sector, metric) with the null sector as its own value rather than as a
-- substitute for one. Every sentinel considered was a string a sector could in principle
-- hold, which is the defect this migration exists to fix rather than a fix for it.
--
-- THE NEW INDEX IS STRICTER, so any row written under the old one is still unique under
-- this one and no data has to move.
--
-- Snapshot-first: 0014 is not edited, including its comment. Its reasoning is preserved
-- as what was believed at the time, and this file is where the correction lives.

DROP INDEX IF EXISTS percentile_cell_daily_key_ux;

CREATE UNIQUE INDEX IF NOT EXISTS percentile_cell_daily_key_ux
    ON percentile_cell_daily (date, size_bucket, sector, metric) NULLS NOT DISTINCT;
