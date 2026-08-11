-- 0004_compute_columns.sql
--
-- Phase 2 checkpoint 2.2. The columns the compute layer writes.
--
-- 0001 created indicator_daily, valuation_daily and market_context_daily with the
-- columns SCHEMA.md names, and said in as many words that "the roughly forty
-- columns and their percentiles arrive with phase 2, which owns them". This is
-- that phase, and the count it actually owns is smaller than forty: fifteen
-- indicator columns, every one of which has a reader named in ARCHITECTURE.html
-- section 5, section 7 or section 11. Eleven exist. Four arrive here.
--
-- Four things.
--
--   1. Four indicator_daily columns, each a metric a screen or a gate ranks on
--      that had no column anywhere.
--   2. Thirty percentile columns across four tables, written by C11 as an Update
--      to the row its metric engine inserted [D-77, pending].
--   3. sentiment_derived_daily, the store S3 ranks on and S5 gates on [D-78].
--   4. capital_expenditures on fundamental_snapshot, so fcf_yield is free cash
--      flow rather than cash flow after all investing [D-79].
--
-- Thirty-four new metric and percentile columns, plus the new table's own two key
-- columns and one fundamental field.
--
-- Snapshot-first: 0001, 0002 and 0003 are not edited, this file sits beside them.
-- Every statement is IF NOT EXISTS, so a second run changes nothing and ci.ps1's
-- idempotence gate passes.
--
-- Every metric column is nullable. Absent is the ordinary state for a great deal
-- of this data and null means unknown, never zero [CLAUDE.md section 6]. A name
-- with 120 bars has no 200-day average, and substituting the 120 it has produces
-- a number that is not what the column says it is, on exactly the names whose
-- history is thinnest.
--
-- Every percentile column is real and none of them is money, including the two
-- whose names match the monetary pattern. A percentile of a dollar figure is a
-- rank between 0 and 100. Both are declared in SCHEMA.md's "Columns that are not
-- money" table, which is the exception route guards.ps1 provides [INVARIANT 16].
--
-- Formulas, windows, warm-ups and null rules for every column below are in
-- docs/METRICS.md, drafted at 2.1.

-- ==================================== 1. The four new indicator columns ======
--
-- dist_20dma            S5's stabilisation gate, which ARCHITECTURE.html section
--                       5 writes as "close > 20dma". Stored as the signed
--                       fraction so the gate reads dist_20dma > 0 rather than
--                       joining back to the close, which matches dist_200dma's
--                       shape and keeps a price out of a real column.
-- rs_20d_slope          The other half of that same gate.
-- rs_21d_63d_change     S2's first ranking input, named in section 5 with no
--                       column behind it.
-- dist_52w_high_20d_change  S2's fifth, the same.
--
-- All four are real, per SCHEMA.md's rule that technical indicators are 32-bit
-- floats because none of them needs fifteen significant figures.

ALTER TABLE indicator_daily
    ADD COLUMN IF NOT EXISTS dist_20dma               real NULL,
    ADD COLUMN IF NOT EXISTS rs_20d_slope             real NULL,
    ADD COLUMN IF NOT EXISTS rs_21d_63d_change        real NULL,
    ADD COLUMN IF NOT EXISTS dist_52w_high_20d_change real NULL;

-- ======================================= 2. The percentile columns ==========
--
-- One per ranked metric, alongside its source column rather than in a table of
-- their own, which is what SCHEMA.md's percentile section says: "written
-- alongside their source tables by PercentileEngine".
--
-- Scaled 0 to 100 rather than 0 to 1. WORKED_EXAMPLE.md prints 84 and 12 and
-- calls them top and bottom quintile, so the scale is decided and top quintile is
-- at or above 80.

-- 2a. indicator_daily, fourteen. Every column except base_breakout_flag, which is
-- boolean: a percentile over a two-valued column collapses to two values and
-- carries nothing the flag does not, and how a boolean enters a screen score is
-- phase 4's to author.
--
-- median_dollar_volume_20d_pctile is here because ARCHITECTURE.html section 7
-- says every metric in the fixed core arrives twice, as the raw value and as its
-- percentile, and the core's identity group names it. A liquidity percentile
-- inside a size and sector cell also separates a thinly traded name from a
-- heavily traded one among its actual peers rather than against megacaps.

ALTER TABLE indicator_daily
    ADD COLUMN IF NOT EXISTS atr_pct_pctile                  real NULL,
    ADD COLUMN IF NOT EXISTS adx14_pctile                    real NULL,
    ADD COLUMN IF NOT EXISTS dist_20dma_pctile               real NULL,
    ADD COLUMN IF NOT EXISTS dist_200dma_pctile              real NULL,
    ADD COLUMN IF NOT EXISTS dist_52w_high_pctile            real NULL,
    ADD COLUMN IF NOT EXISTS dist_52w_high_20d_change_pctile real NULL,
    ADD COLUMN IF NOT EXISTS rs_change_21d_pctile            real NULL,
    ADD COLUMN IF NOT EXISTS rs_change_63d_pctile            real NULL,
    ADD COLUMN IF NOT EXISTS rs_21d_63d_change_pctile        real NULL,
    ADD COLUMN IF NOT EXISTS rs_20d_slope_pctile             real NULL,
    ADD COLUMN IF NOT EXISTS rs_change_vs_sector_pctile      real NULL,
    ADD COLUMN IF NOT EXISTS volume_vs_50d_avg_pctile        real NULL,
    ADD COLUMN IF NOT EXISTS ma50_200_slope_pctile           real NULL,
    ADD COLUMN IF NOT EXISTS median_dollar_volume_20d_pctile real NULL;

-- 2b. valuation_daily, ten. The ratio columns only.
--
-- cash_on_hand and quarterly_burn_rate are levels in dollars, sent to the dossier
-- for magnitude rather than for comparison, and last_two_earnings_surprises is an
-- array. None of the three is ranked by any screen.

ALTER TABLE valuation_daily
    ADD COLUMN IF NOT EXISTS fcf_yield_pctile               real NULL,
    ADD COLUMN IF NOT EXISTS ev_ebit_pctile                 real NULL,
    ADD COLUMN IF NOT EXISTS ev_ebit_vs_own_5y_pctile       real NULL,
    ADD COLUMN IF NOT EXISTS roic_pctile                    real NULL,
    ADD COLUMN IF NOT EXISTS roic_4q_change_pctile          real NULL,
    ADD COLUMN IF NOT EXISTS gross_margin_4q_change_pctile  real NULL,
    ADD COLUMN IF NOT EXISTS net_debt_ebitda_pctile         real NULL,
    ADD COLUMN IF NOT EXISTS accruals_pctile                real NULL,
    ADD COLUMN IF NOT EXISTS share_count_change_pctile      real NULL,
    ADD COLUMN IF NOT EXISTS revenue_growth_4q_trend_pctile real NULL;

-- 2c. flow_daily, three. S4 ranks on all three.
--
-- insider_net_90d_usd_pctile carries _usd and is not money. It is one of the two
-- percentile names that match the monetary pattern.

ALTER TABLE flow_daily
    ADD COLUMN IF NOT EXISTS insider_net_90d_usd_pctile   real NULL,
    ADD COLUMN IF NOT EXISTS distinct_buyer_count_pctile  real NULL,
    ADD COLUMN IF NOT EXISTS inst_ownership_change_pctile real NULL;

-- ============================== 3. sentiment_derived_daily [D-78] ===========
--
-- Grain: ticker by day. Writer: SentimentEngine, a compute stage, not the ingest.
--
-- Five columns of which three are derived. Derived from sentiment_daily exactly
-- as flow_daily is derived from the two flow source tables and indicator_daily
-- from price_daily: ingest grain follows the source, consumption grain follows
-- the screen [D-61].
--
-- sentiment_daily holds what the provider sends, ticker, date, article_count and
-- sentiment_score. S3 ranks on the three columns here and on nothing else, S5's
-- stabilisation gate reads the first two, and section 7's fixed core carries two.
-- No component had a path from one to the other until D-78.
--
-- The three windows are named in the columns, so they are constants in the stage
-- rather than config keys. FlowEngine's 90 is the precedent: a tunable 90 beside
-- a column called insider_net_90d_usd is a second place for the number to live
-- and the column name would be wrong the first time they disagreed.
--
-- All three are real. A z-score, a difference of two sentiment means and a
-- sentiment mean are none of them money.

CREATE TABLE IF NOT EXISTS sentiment_derived_daily (
    ticker                         text NOT NULL,
    date                           date NOT NULL,
    article_count_z_own_90d        real NULL,
    sentiment_delta_7v30           real NULL,
    sentiment_7d_level             real NULL,
    article_count_z_own_90d_pctile real NULL,
    sentiment_delta_7v30_pctile    real NULL,
    sentiment_7d_level_pctile      real NULL,
    PRIMARY KEY (ticker, date)
);

-- ================================ 4. capital_expenditures [D-79] ============
--
-- fcf_yield computed as cash from operating plus cash from investing reads an
-- acquisition as capital expenditure and an asset sale as free cash flow. That is
-- S1's first ranking input, and both errors land hardest on exactly the names the
-- screen exists to find.
--
-- One field from the endpoint C03 already calls. 0002's stated basis for its own
-- column list, "driven by what valuation_daily needs in phase 2, not by what the
-- provider happens to send", covers this one.
--
-- numeric, because it is money, and its name matches the monetary pattern through
-- "cap", so guards.ps1 asserts it. ExpectedMonetary moves from 17 to 18 with it,
-- which is that number doing the job it was made exact for [INVARIANT 16].

ALTER TABLE fundamental_snapshot
    ADD COLUMN IF NOT EXISTS capital_expenditures numeric NULL;
