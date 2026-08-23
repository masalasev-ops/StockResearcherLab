-- 0011_sentiment_fetch_attempt.sql
--
-- C04's attempt record, and what 3.8's sweep resumes on [D-99].
--
-- THE SWEEP SPANS TWO DAYS BY ARITHMETIC, so it has to resume. D-101 widened C04's
-- pool to the live universe plus the 16,862 in-window delisted names, at the flat five
-- units a ticker 3.1 confirmed over a wide range, which is 98,515 units against a
-- hundred thousand a day with a reserve held back. A sweep that halts and cannot say
-- what it covered restarts, and a restart at five units a ticker is a second day spent
-- on ground already loaded.
--
-- WHY NOT "TICKERS ALREADY IN sentiment_daily". This is 0010's argument with the
-- sharper edge. A sparse series is the ordinary state here rather than a fault: the
-- probe measured 4 to 122 days with a row out of 180 on small caps, and rows appear
-- only on days that carry news [D-12]. So a ticker nobody wrote about across the whole
-- window is fetched, yields nothing, and gains no presence row ever. Resuming on
-- presence would re-fetch it on every run for the life of the sweep, and the names it
-- would loop on are exactly the thinly covered ones the sentiment screen exists to
-- find [ARCHITECTURE section 20, D-5].
--
-- last_yield_date null means attempted and yielded nothing, which is a different fact
-- from an absent row, which means never attempted [CLAUDE.md section 6]. On this table
-- the first case is common rather than exceptional, which is the whole reason the
-- distinction has to be storable.
--
-- THE STAMP IS THE RANGE START, which is C02's half of D-99's asymmetry rather than
-- C03's. The two calls differ in depth: the nightly stage asks from
-- context.Date - sentiment.lookback_days, and the sweep asks from
-- backfill.window_start. A ticker the nightly run touched is therefore NOT as complete
-- as one the sweep touched, so the sweep's marker has to be one no nightly run can
-- produce. C03 stamps the range end only because fundamentals/{t} returns full history
-- either way and the two calls are the same call.
--
-- NO COUNTER COLUMN, for the reason 0006, 0008 and 0010 all give: a tally would
-- increment on a re-run and D-68 requires every stage write to be idempotent on the
-- table's own grain. Each column below is a function of the last attempt alone.
--
-- A SHARED TABLE WAS REJECTED for the fourth time, on 0008's argument unchanged. Two
-- components writing one table is two claims on one component-table-operation triple
-- [INVARIANT 10] and the conformance test would catch it.
--
-- ONE ROW PER TICKER THOUGH THE CALL IS BATCHED. The endpoint takes a comma-separated
-- symbol list, so the unit of work is a batch; the unit of BILLING is a ticker, flat at
-- five [1.6, 3.1]. Resumption keys on what was paid for, so the grain is the ticker and
-- every member of a dispatched batch gains a row whether or not it came back with days.
--
-- Snapshot-first: 0001 to 0010 are not edited. The statements are guarded, so a second
-- run changes nothing and ci.ps1's second migrate still prints nothing to apply.

-- Grain: one row per ticker. Writer: SentimentIngestor.
CREATE TABLE IF NOT EXISTS sentiment_fetch_attempt (
    ticker              text    NOT NULL PRIMARY KEY,
    last_attempted_date date    NOT NULL,
    last_yield_date     date    NULL,
    rows_last_attempt   bigint  NOT NULL
);

-- The sweep reads every ticker attempted at one date on every run, so the index makes
-- that an index-only scan rather than a sequential one over the whole table. Same shape
-- as its three neighbours' indexes, serving an equality on the leading column.
CREATE INDEX IF NOT EXISTS sentiment_fetch_attempt_sweep_ix
    ON sentiment_fetch_attempt (last_attempted_date, ticker);
