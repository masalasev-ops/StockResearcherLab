-- 0007_security_daily_and_date_indexes.sql
--
-- Two things the backfill cannot start without, and neither is about its
-- arithmetic. One is a table the universe has never had. The other is an index
-- every per-date read has been missing since 0001.
--
-- Snapshot-first: 0001 to 0006 are not edited, this file sits beside them. Every
-- statement is guarded, so a second run changes nothing and ci.ps1's second
-- migrate still prints nothing to apply.
--
-- ================================= 1. security_daily [D-92] =================
--
-- security is one row per ticker carrying today's bucket, and C11 ranks inside
-- size-bucket-by-sector cells [D-10]. A backfilled 2021 date would therefore rank
-- every name in its 2026 cell. That is the failure this system is built to notice
-- and cannot: a percentile computed over a slightly wrong cell is not inspectable
-- afterwards, because nothing downstream can see the cell it was computed over.
--
-- Deriving it at read time was weighed and rejected in D-92. The bucket derives
-- from one name's market cap against two config floors, but the cell is
-- (size_bucket, sector) and the fifteen-member test counts the non-null population
-- in that cell on that date, so ranking one row needs every other row's bucket and
-- sector on the same date. Market cap as-of needs shares_outstanding readable on
-- filing_date_effective <= date, and sector is a provider call with no history from
-- this source at all. The derivation is the whole store recomputed per date, which
-- is a store.
--
-- IS_ACTIVE IS NOT NULL AND CARRIES NO DEFAULT, where 0001 gave security's column
-- DEFAULT true. That default is what let C01 have no path that deactivates a name
-- and no error to show for it [PROGRESS.md, 2026-08-09], and this is the table
-- membership is read from from here on. A writer that has not decided fails here
-- rather than writing true.
--
-- MARKET_CAP IS NUMERIC [INVARIANT 16]. guards.ps1 states the count of monetary
-- numeric columns in advance and this one moves it.
--
-- SECURITY KEEPS ITS FOUR COLUMNS PHYSICALLY AND THIS MIGRATION DROPS NOTHING.
-- Checkpoint 3.2 enumerates what 0007 does and a drop is not in it, and the
-- done-when line takes ExpectedMonetary from 18 to 19, which holds only while
-- security.market_cap is still there. SCHEMA.md stops listing the four under
-- D-73's clean-edit rule, which is a documentation edit and not a claim the
-- columns are gone: that document says in as many words that its column lists are
-- the load-bearing ones rather than exhaustive. What is left behind is four
-- columns nothing writes after 3.11 and nothing reads after 3.12, recorded as a
-- finding rather than dropped here, because dropping them moves two asserted
-- counts and that is a decision rather than a tidy-up.

-- Grain: ticker by date. Writer: UniverseBuilder.
CREATE TABLE IF NOT EXISTS security_daily (
    ticker      text    NOT NULL,
    date        date    NOT NULL,
    sector      text    NULL,
    size_bucket text    NULL,
    market_cap  numeric NULL,
    is_active   boolean NOT NULL,
    PRIMARY KEY (ticker, date)
);

-- The primary key serves "this ticker on or before that date", which is C11's
-- join. This serves the other read, "every member on that date", which is what
-- C10's breadth, C35's iteration set and C11's own cell population all do. The
-- ticker column is carried so the scan is index-only and its order is the index's
-- rather than a sort's [CLAUDE.md section 6].
CREATE INDEX IF NOT EXISTS security_daily_date_ticker_ix
    ON security_daily (date, ticker);

-- ============================ 2. A date-leading index on five tables ========
--
-- price_daily, indicator_daily, valuation_daily, flow_daily and
-- sentiment_derived_daily are all PRIMARY KEY (ticker, date) [0001, 0004], and
-- every per-date read constrains the column the index does not lead on. That is
-- survivable at one date a night against a table of one day's rows. It is the
-- binding cost at 1,260 dates against 1.2 GB, which is what C11 updates once per
-- backfilled date.
--
-- INDEX RATHER THAN DECLARATIVE RANGE PARTITIONING, and the reason is recorded
-- rather than assumed. An index is reversible and costs nothing on an empty table;
-- partitioning is a schema decision far cheaper before 1.2 GB than after, and
-- SCREEN_LIFECYCLE.md section 9.3 sets the precedent of deciding it once with the
-- numbers in hand rather than twice on a guess. If 3.15 measures the per-date
-- percentile update as the binding cost, partitioning is what that finding
-- recommends and this index is what it is measured against.
--
-- (date, ticker) rather than (date) alone, for the reason above: the per-date
-- reads want the date's ticker list and get it from the index without touching the
-- heap, in an order the index fixes.

CREATE INDEX IF NOT EXISTS price_daily_date_ticker_ix
    ON price_daily (date, ticker);

CREATE INDEX IF NOT EXISTS indicator_daily_date_ticker_ix
    ON indicator_daily (date, ticker);

CREATE INDEX IF NOT EXISTS valuation_daily_date_ticker_ix
    ON valuation_daily (date, ticker);

CREATE INDEX IF NOT EXISTS flow_daily_date_ticker_ix
    ON flow_daily (date, ticker);

CREATE INDEX IF NOT EXISTS sentiment_derived_daily_date_ticker_ix
    ON sentiment_derived_daily (date, ticker);
