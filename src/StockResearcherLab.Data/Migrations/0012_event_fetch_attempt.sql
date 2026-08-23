-- 0012_event_fetch_attempt.sql
--
-- C06's attempt record, and what 3.10's sweep resumes on [D-99].
--
-- WHY NOT "TICKERS ALREADY IN events". This is 0011's argument with the same edge and
-- a different cause. A name that has never split and never paid a dividend across the
-- whole window is a perfectly ordinary name, not a gap: splits/{t} and div/{t} each
-- return an empty array for it, so it gains no row in `events` ever. Resuming on
-- presence would re-ask it on every run for the life of the sweep. 3.1 measured the
-- case directly, `splits/SPY.US` and `splits/CCS.US` both returning 0 rows, and SPY is
-- the benchmark rather than an obscure name.
--
-- THE EARNINGS ROWS IN THAT TABLE MAKE THE PREDICATE WORSE, not better. `events` holds
-- earnings written by the nightly stage, so a ticker with an earnings row and no
-- distributions would read as covered by a presence test while carrying nothing this
-- sweep is for. The two event families are written by different calls and the record
-- has to be of what the sweep asked for.
--
-- last_yield_date null means attempted and yielded nothing, which is a different fact
-- from an absent row, which means never attempted [CLAUDE.md section 6]. As on 0011,
-- the first case is common rather than exceptional here.
--
-- THE STAMP IS THE RANGE START, which is C02's and C04's half of D-99's asymmetry. The
-- nightly stage takes splits and dividends from the bulk feed for one date; the sweep
-- takes whole history per ticker from splits/{t} and div/{t}. The two differ in depth
-- as completely as two calls can, so the sweep's marker has to be one no nightly run
-- can produce.
--
-- ONE ROW PER TICKER FOR TWO CALLS. A ticker's unit of work is both calls together, at
-- one unit each, and neither is dispatched without the other. Splitting the record per
-- endpoint would make a half-covered ticker representable, which is a state the sweep
-- cannot produce and nothing would ever read.
--
-- NO COUNTER COLUMN, for the reason 0006, 0008, 0010 and 0011 all give: a tally would
-- increment on a re-run and D-68 requires every stage write to be idempotent on the
-- table's own grain.
--
-- A SHARED TABLE WAS REJECTED for the fifth time, on 0008's argument unchanged. Two
-- components writing one table is two claims on one component-table-operation triple
-- [INVARIANT 10] and the conformance test would catch it.
--
-- Snapshot-first: 0001 to 0011 are not edited. The statements are guarded, so a second
-- run changes nothing and ci.ps1's second migrate still prints nothing to apply.

-- Grain: one row per ticker. Writer: EventsIngestor.
CREATE TABLE IF NOT EXISTS event_fetch_attempt (
    ticker              text    NOT NULL PRIMARY KEY,
    last_attempted_date date    NOT NULL,
    last_yield_date     date    NULL,
    rows_last_attempt   bigint  NOT NULL
);

-- The sweep reads every ticker attempted at one date on every run, so the index makes
-- that an index-only scan rather than a sequential one over the whole table. Same shape
-- as its four neighbours' indexes, serving an equality on the leading column.
CREATE INDEX IF NOT EXISTS event_fetch_attempt_sweep_ix
    ON event_fetch_attempt (last_attempted_date, ticker);
