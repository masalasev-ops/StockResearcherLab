-- 0009_earnings_history_and_sector.sql
--
-- Two stores for one call. 3.7 fetches fundamentals/{t} once for the whole pool, and
-- dropping the Financials filter makes that one call carry General::Sector and
-- Earnings::History as well at the same 10 units [3.1, measured: filtered and
-- unfiltered cost the same].
--
-- ============================== 1. earnings_history [D-96] ==================
--
-- CAPTURE IS NOT USE. D-90 is open on whether post-earnings drift registers at all,
-- and this prefers no fork. What decides the timing is the asymmetry: a capture taken
-- during a sweep already happening is cheap and reversible, and one taken afterwards
-- is a re-sweep at 10 units a ticker.
--
-- THE READ RULE IS report_date <= date, which is filing_date_effective's analogue one
-- table over [INVARIANT 12]. A row whose report_date is null is stored and is
-- unreadable, exactly as an undated fundamental row is.
--
-- That rule is not lookahead and the reason is worth stating, because it looks like it
-- might be. The nightly run executes after the close, so a result released after the
-- close of the date being computed was public before the run began. A backfilled date
-- inherits the property, report_date being when the result actually landed.
--
-- BEFORE_AFTER_MARKET IS LOAD-BEARING RATHER THAN DESCRIPTIVE. A result released after
-- the close of day D is reacted to on D+1; one released before the open of D is
-- reacted to on D. A drift screen computing its reaction window without this column
-- uses the wrong session for roughly half of all announcements, and the error is
-- systematic rather than noisy.
--
-- SURPRISE IS A FRACTION AND THE COLUMN IS NAMED FOR WHAT IT HOLDS. The provider sends
-- a percent and it is divided on the way in, which is the one rule every ratio in this
-- system follows [METRICS.md]. It is stored rather than derived because it may not be
-- derivable: the provider's figure may rest on an estimate other than the one it
-- reports. Whether it agrees with (eps_actual - eps_estimate) / abs(eps_estimate) is
-- measurable once this store is loaded, and 3.7 records the answer.
--
-- REAL RATHER THAN NUMERIC on the three figures, and they are declared as not money in
-- SCHEMA.md. An EPS is a per-share amount and a surprise is a ratio; neither is a
-- monetary total, and INVARIANT 16's guard reads that declaration rather than a list in
-- a script.
--
-- (ticker, period_end) is a natural key, so there is no surrogate and no
-- NULLS NOT DISTINCT case to reason about.

-- Grain: ticker by fiscal period. Writer: FundamentalsIngestor.
CREATE TABLE IF NOT EXISTS earnings_history (
    ticker              text NOT NULL,
    period_end          date NOT NULL,
    report_date         date NULL,
    before_after_market text NULL,
    eps_actual          real NULL,
    eps_estimate        real NULL,
    surprise_fraction   real NULL,
    PRIMARY KEY (ticker, period_end)
);

-- Every read filters report_date and most read one ticker, which is the same shape
-- fundamental_snapshot_effective_ix serves for filing dates.
CREATE INDEX IF NOT EXISTS earnings_history_report_ix
    ON earnings_history (ticker, report_date);

-- ================================ 2. sector [D-97] =========================
--
-- C01's per-member sector call buys at 10 units what fundamentals/{t} already carries
-- for nothing, and 3.11 has C01 reading sector from fundamental_snapshot where no such
-- column existed.
--
-- NOT ON security, for three reasons and the first is enough. It would make C03 a
-- second writer on the table C01 owns, requiring a split declaration [INVARIANT 10,
-- D-77], where a column here needs none. It would carry one sector per ticker, which is
-- today's sector applied to every historical date, the exact defect security_daily was
-- created to remove. And it would put a weekly stage's read behind a nightly stage's
-- write.
--
-- Sector as of a filing is not sector as of a date: a company that reclassifies between
-- filings reads as its former sector until the next one lands. That is closer to
-- point-in-time than a single current value and is not the same thing, and the
-- percentile cells inherit it. Recorded rather than proxied.

ALTER TABLE fundamental_snapshot ADD COLUMN IF NOT EXISTS sector text NULL;
