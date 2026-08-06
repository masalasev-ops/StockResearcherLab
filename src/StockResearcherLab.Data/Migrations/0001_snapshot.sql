-- 0001_snapshot.sql
--
-- Snapshot-first. This file is the whole schema as SCHEMA.md declares it, and a
-- later change adds a numbered file beside it rather than editing this one.
-- Every statement is IF NOT EXISTS, so applying it twice is a no-op and the
-- second run of migrate.ps1 changes nothing.
--
-- Every table here appears in SCHEMA.md and every table in SCHEMA.md appears
-- here. The test at 0.2 asserts that in both directions by parsing SCHEMA.md,
-- so this file cannot drift from the document without going red.
--
-- Money is numeric, never float or double, in any monetary path [INVARIANT 16].
-- Where SCHEMA.md asks for 32-bit floats those are technical indicators and not
-- money, and those columns are real.
--
-- The migration ledger lives in the meta schema rather than public, so that
-- "every table in the database is in SCHEMA.md" can be asserted exactly over
-- public without a bookkeeping table forcing an exception into the assertion.

CREATE SCHEMA IF NOT EXISTS meta;

CREATE TABLE IF NOT EXISTS meta.schema_migration (
    filename    text        NOT NULL PRIMARY KEY,
    applied_at  timestamptz NOT NULL,
    sha256      text        NOT NULL
);

-- ============================================================== Reference ===

-- Grain: one row per ticker. Writer: UniverseBuilder.
-- delisted_date is populated rather than the row deleted, because the historical
-- universe must be reconstructable per date including names that no longer exist
-- [D-48]. No clean_gap_count column: that count is computed as of the date being
-- built, never stored [M.1].
CREATE TABLE IF NOT EXISTS security (
    ticker         text    NOT NULL PRIMARY KEY,
    name           text    NULL,
    sector         text    NULL,
    size_bucket    text    NULL,
    market_cap     numeric NULL,
    first_seen     date    NULL,
    last_seen      date    NULL,
    delisted_date  date    NULL,
    is_active      boolean NOT NULL DEFAULT true
);

-- ============================================================ Market data ===

-- Grain: ticker by day. Writer: PriceIngestor.
CREATE TABLE IF NOT EXISTS price_daily (
    ticker     text    NOT NULL,
    date       date    NOT NULL,
    open       numeric NULL,
    high       numeric NULL,
    low        numeric NULL,
    close      numeric NULL,
    adj_close  numeric NULL,
    volume     bigint  NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: ticker by fiscal period. Writer: FundamentalsIngestor.
-- filing_date_effective is the key every read filters on, never period_end and
-- never the raw filing_date [D-46, D-62, INVARIANT 12]. Both of the others are
-- stored so the gap stays inspectable; no query joins on either.
-- filing_date_unknown_reason records which case fired, four distinguishable
-- states rather than a boolean, because which one fired is diagnostic.
CREATE TABLE IF NOT EXISTS fundamental_snapshot (
    ticker                     text NOT NULL,
    period_end                 date NOT NULL,
    period_type                text NOT NULL,
    filing_date                date NULL,
    filing_date_effective      date NOT NULL,
    filing_date_unknown_reason text NOT NULL,
    PRIMARY KEY (ticker, period_end, period_type),
    CONSTRAINT fundamental_snapshot_unknown_reason_ck
        CHECK (filing_date_unknown_reason IN ('null', 'equal', 'negative', 'none'))
);

CREATE INDEX IF NOT EXISTS fundamental_snapshot_effective_ix
    ON fundamental_snapshot (ticker, filing_date_effective);

-- Grain: ticker by day, whole universe. Writer: SentimentIngestor.
CREATE TABLE IF NOT EXISTS sentiment_daily (
    ticker          text    NOT NULL,
    date            date    NOT NULL,
    article_count   integer NULL,
    sentiment_score real    NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: candidate by day. Writer: HeadlineIngestor.
-- Only for names that reached the candidate set [D-23].
CREATE TABLE IF NOT EXISTS headline (
    headline_id  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticker       text        NOT NULL,
    date         date        NOT NULL,
    published_at timestamptz NULL,
    title        text        NULL,
    source       text        NULL,
    url          text        NULL
);

CREATE INDEX IF NOT EXISTS headline_ticker_date_ix ON headline (ticker, date);

-- Grain: ticker by filing by transaction, the source's own. Writer: FlowIngestor.
-- transaction_code is not optional: the S4 rubric disqualifies option exercises
-- and scheduled plan activity, so a count that cannot separate an open-market
-- purchase from an award is not the count the screen needs [D-61].
CREATE TABLE IF NOT EXISTS insider_transaction (
    insider_transaction_id bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticker                 text    NOT NULL,
    filed_at               date    NULL,
    transaction_date       date    NULL,
    reporting_owner_name   text    NULL,
    transaction_code       text    NOT NULL,
    shares_amount          numeric NULL,
    price_per_share        numeric NULL,
    total_value            numeric NULL,
    acquired_or_disposed   text    NULL
);

CREATE INDEX IF NOT EXISTS insider_transaction_ticker_date_ix
    ON insider_transaction (ticker, transaction_date);

-- Grain: ticker by holder by report date, the source's own. Writer: FlowIngestor.
-- report_date is what makes this backfillable, and it is the field short interest
-- turned out not to have [D-58].
CREATE TABLE IF NOT EXISTS institutional_holding (
    ticker      text    NOT NULL,
    report_date date    NOT NULL,
    holder_name text    NOT NULL,
    shares      numeric NULL,
    change      numeric NULL,
    change_pct  real    NULL,
    PRIMARY KEY (ticker, report_date, holder_name)
);

-- Grain: ticker by day. Writer: FlowEngine, a compute stage, not the ingest
-- [D-61]. Derived from the two source tables above, exactly as indicator_daily is
-- derived from price_daily. Short interest is gone [D-58].
CREATE TABLE IF NOT EXISTS flow_daily (
    ticker                text    NOT NULL,
    date                  date    NOT NULL,
    insider_net_usd_90d   numeric NULL,
    distinct_buyer_count  integer NULL,
    inst_ownership_change real    NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: ticker by event. Writer: EventsIngestor.
CREATE TABLE IF NOT EXISTS events (
    event_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticker         text   NOT NULL,
    event_type     text   NOT NULL,
    event_date     date   NULL,
    announced_date date   NULL
);

CREATE INDEX IF NOT EXISTS events_ticker_date_ix ON events (ticker, event_date);

-- =============================================================== Computed ===

-- Grain: ticker by day. Writer: IndicatorEngine.
-- Stored as 32-bit floats per SCHEMA.md: no technical indicator needs fifteen
-- significant figures and it halves the table. median_dollar_volume_20d is the
-- exception and is numeric, because it is money and INVARIANT 16 does not bend
-- for storage size. Reported in the 0.2 commit body.
-- The roughly forty columns and their percentiles arrive with phase 2, which owns
-- them. Only the columns SCHEMA.md names are created here.
CREATE TABLE IF NOT EXISTS indicator_daily (
    ticker                   text    NOT NULL,
    date                     date    NOT NULL,
    atr_pct                  real    NULL,
    adx14                    real    NULL,
    dist_200dma              real    NULL,
    dist_52w_high            real    NULL,
    rs_change_21d            real    NULL,
    rs_change_63d            real    NULL,
    rs_change_vs_sector      real    NULL,
    volume_vs_50d_avg        real    NULL,
    ma50_200_slope           real    NULL,
    base_breakout_flag       boolean NULL,
    median_dollar_volume_20d numeric NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: ticker by day. Writer: ValuationEngine.
-- Recomputed daily because price moves. Every fundamental input resolved as of
-- filing_date_effective [D-62].
CREATE TABLE IF NOT EXISTS valuation_daily (
    ticker                      text    NOT NULL,
    date                        date    NOT NULL,
    fcf_yield                   real    NULL,
    ev_ebit                     real    NULL,
    ev_ebit_vs_own_5y           real    NULL,
    roic                        real    NULL,
    roic_4q_change              real    NULL,
    gross_margin_4q_change      real    NULL,
    net_debt_ebitda             real    NULL,
    accruals                    real    NULL,
    share_count_change          real    NULL,
    revenue_growth_4q_trend     real    NULL,
    cash_on_hand                numeric NULL,
    quarterly_burn_rate         numeric NULL,
    last_two_earnings_surprises real[]  NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: one row per day. Writer: MarketContextEngine.
CREATE TABLE IF NOT EXISTS market_context_daily (
    date                     date  NOT NULL PRIMARY KEY,
    breadth                  real  NULL,
    vix                      real  NULL,
    regime_label             text  NULL,
    sector_relative_strength jsonb NULL
);

-- ============================================================== Selection ===

-- Grain: ticker by day. Writer: GateEngine.
-- Records every failing reason, not the first.
CREATE TABLE IF NOT EXISTS gate_result (
    ticker  text    NOT NULL,
    date    date    NOT NULL,
    passed  boolean NOT NULL,
    reasons text[]  NOT NULL DEFAULT '{}',
    PRIMARY KEY (ticker, date)
);

-- Grain: ticker by screen by day. Writer: ScreenEngine. The largest table.
CREATE TABLE IF NOT EXISTS screen_score_daily (
    ticker             text    NOT NULL,
    screen_id          text    NOT NULL,
    date               date    NOT NULL,
    score              real    NULL,
    rank_within_screen integer NULL,
    config_version     integer NOT NULL,
    PRIMARY KEY (ticker, screen_id, date)
);

-- Grain: screen by day. Writer: ScreenEngine.
-- Trailing distribution summary per screen, from which the floor is computed [D-9].
CREATE TABLE IF NOT EXISTS screen_history (
    screen_id        text    NOT NULL,
    date             date    NOT NULL,
    floor_score      real    NULL,
    p98_trailing     real    NULL,
    observation_days integer NULL,
    PRIMARY KEY (screen_id, date)
);

-- Grain: ticker by day. Writer: CandidateAllocator.
CREATE TABLE IF NOT EXISTS candidate_set (
    ticker            text    NOT NULL,
    date              date    NOT NULL,
    screens_surfacing text[]  NOT NULL,
    size_bucket       text    NULL,
    slot_filled       boolean NULL,
    PRIMARY KEY (ticker, date)
);

-- Grain: ticker by day surfaced.
-- CandidateAllocator owns the insert, ForwardReturnFiller owns the update and
-- only of the nine return columns. Neither may perform the other's operation and
-- no third component writes here at all [INVARIANT 10 as amended].
-- Written at shortlist time for every candidate, never reconstructed [D-40,
-- INVARIANT 4]. config_version is what lets history be segmented rather than
-- pooled after a screen definition changes.
CREATE TABLE IF NOT EXISTS attribution (
    ticker              text    NOT NULL,
    date                date    NOT NULL,
    screens_surfacing   text[]  NOT NULL,
    score_per_screen    jsonb   NULL,
    size_bucket         text    NULL,
    sector              text    NULL,
    regime              text    NULL,
    gate_state          text    NULL,
    config_version      integer NOT NULL,
    digest_provider     text    NULL,
    return_5d_raw       numeric NULL,
    return_5d_vs_spy    numeric NULL,
    return_5d_vs_peers  numeric NULL,
    return_21d_raw      numeric NULL,
    return_21d_vs_spy   numeric NULL,
    return_21d_vs_peers numeric NULL,
    return_63d_raw      numeric NULL,
    return_63d_vs_spy   numeric NULL,
    return_63d_vs_peers numeric NULL,
    PRIMARY KEY (ticker, date)
);

-- ================================================================= Decide ===

-- Grain: ticker by day. Writer: NewsDigester.
-- provider and model_name are not optional [D-29]. was_rotation marks the two
-- candidates a night deliberately routed to the secondary [D-27], so the paired
-- sample is separable from genuine fallthroughs.
CREATE TABLE IF NOT EXISTS news_digest (
    ticker       text    NOT NULL,
    date         date    NOT NULL,
    digest_text  text    NULL,
    provider     text    NOT NULL,
    model_name   text    NOT NULL,
    was_rotation boolean NOT NULL DEFAULT false,
    PRIMARY KEY (ticker, date)
);

-- Grain: one prefix per night plus one block per candidate. Writer: DossierBuilder.
-- Persisted before any call goes out, which is what makes citation verification
-- possible after the fact. prefix_hash is what the snapshot test asserts on.
-- ticker is null on the one prefix row per night, which is why the key is an
-- identity with partial unique indexes rather than a composite.
CREATE TABLE IF NOT EXISTS dossier (
    dossier_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    date        date   NOT NULL,
    ticker      text   NULL,
    prefix_text text   NULL,
    prefix_hash text   NULL,
    block_text  text   NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS dossier_prefix_ux
    ON dossier (date) WHERE ticker IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS dossier_block_ux
    ON dossier (date, ticker) WHERE ticker IS NOT NULL;

-- Grain: ticker by day by model.
-- ResearcherClient owns the insert. ProposalValidator owns the update, and only
-- of status and rejection_reason. Nothing else writes here [INVARIANT 10].
CREATE TABLE IF NOT EXISTS proposal (
    ticker               text    NOT NULL,
    date                 date    NOT NULL,
    model_id             text    NOT NULL,
    verdict              text    NULL,
    p_target_before_stop numeric NULL,
    thesis               text    NULL,
    counter_argument     text    NULL,
    primary_driver       text    NULL,
    stop_pct             numeric NULL,
    target_pct           numeric NULL,
    horizon_days         integer NULL,
    status               text    NULL,
    rejection_reason     text    NULL,
    PRIMARY KEY (ticker, date, model_id)
);

-- ================================================================ Execute ===

-- Grain: one row per portfolio. Writer: configuration, not a stage.
-- Exactly one research portfolio carries is_primary. Winding down stops new
-- entries while open positions run to natural exit [D-38].
CREATE TABLE IF NOT EXISTS portfolio (
    portfolio_id     text    NOT NULL PRIMARY KEY,
    name             text    NOT NULL,
    selection_method text    NOT NULL,
    provider         text    NULL,
    model_id         text    NULL,
    use_batch        boolean NOT NULL DEFAULT false,
    is_primary       boolean NOT NULL DEFAULT false,
    state            text    NOT NULL DEFAULT 'active',
    CONSTRAINT portfolio_state_ck
        CHECK (state IN ('active', 'winding_down', 'retired'))
);

-- Grain: portfolio by ticker by day. Writer: PortfolioRunner [N.1].
-- The two controls have no equivalent of proposal and this is it, which is what
-- lets one component apply risk to all four rather than two components applying
-- it to two each [INVARIANT 8]. A selection the risk caps then block leaves a row
-- here and no order, and that is the only place the difference between what a
-- control portfolio wanted and what it got is visible.
CREATE TABLE IF NOT EXISTS portfolio_selection (
    portfolio_id text NOT NULL,
    date         date NOT NULL,
    ticker       text NOT NULL,
    source       text NOT NULL,
    source_ref   text NULL,
    PRIMARY KEY (portfolio_id, date, ticker),
    CONSTRAINT portfolio_selection_source_ck
        CHECK (source IN ('proposal', 'screen_rotation', 'random_draw'))
);

-- order / fill / position. RiskGate inserts orders. PaperBroker inserts fills and
-- inserts positions. PositionManager updates positions to closed. Three tables
-- and three components, each owning a different transition [N.1].
-- "order" and "position" are quoted because both are Postgres reserved words. The
-- names are SCHEMA.md's and are not mine to change.
CREATE TABLE IF NOT EXISTS "order" (
    order_id     bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    portfolio_id text        NOT NULL,
    date         date        NOT NULL,
    ticker       text        NOT NULL,
    side         text        NOT NULL,
    quantity     numeric     NOT NULL,
    limit_price  numeric     NULL,
    stop_price   numeric     NULL,
    target_price numeric     NULL,
    status       text        NOT NULL,
    created_at   timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS order_portfolio_date_ix ON "order" (portfolio_id, date);

CREATE TABLE IF NOT EXISTS fill (
    fill_id      bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id     bigint  NOT NULL REFERENCES "order" (order_id),
    portfolio_id text    NOT NULL,
    ticker       text    NOT NULL,
    fill_date    date    NOT NULL,
    fill_price   numeric NOT NULL,
    quantity     numeric NOT NULL,
    slippage_bps numeric NULL
);

CREATE TABLE IF NOT EXISTS "position" (
    position_id  bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    portfolio_id text    NOT NULL,
    ticker       text    NOT NULL,
    opened_date  date    NOT NULL,
    closed_date  date    NULL,
    quantity     numeric NOT NULL,
    entry_price  numeric NOT NULL,
    stop_price   numeric NULL,
    target_price numeric NULL,
    is_open      boolean NOT NULL DEFAULT true
);

CREATE INDEX IF NOT EXISTS position_portfolio_open_ix
    ON "position" (portfolio_id) WHERE is_open;

-- Grain: per closed trade. Writer: PositionManager.
-- All monetary columns are decimal, never float or double [INVARIANT 16].
CREATE TABLE IF NOT EXISTS trade_outcome (
    trade_outcome_id bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    portfolio_id     text    NOT NULL,
    ticker           text    NOT NULL,
    entry_date       date    NOT NULL,
    exit_date        date    NOT NULL,
    pnl              numeric NOT NULL,
    alpha_vs_spy     numeric NULL,
    alpha_vs_peers   numeric NULL,
    mfe              numeric NULL,
    mae              numeric NULL,
    exit_reason      text    NOT NULL
);

-- ===================================================== Learn and configure ===

-- Grain: key by version. Writer: configuration and ScreenTuner.
-- Append-only and versioned. Current is MAX(version) for a key. A change inserts
-- version + 1. Anything reading config for a simulated date resolves as of that
-- date, never as-now [D-43, INVARIANT 13].
CREATE TABLE IF NOT EXISTS config_rows (
    key     text        NOT NULL,
    version integer     NOT NULL,
    value   jsonb       NOT NULL,
    set_at  timestamptz NOT NULL,
    set_by  text        NULL,
    PRIMARY KEY (key, version)
);

-- Grain: per revision. Writer: LessonWriter.
-- Maximum ten active, n at least 30, expires after six months unless reconfirmed
-- [D-44].
CREATE TABLE IF NOT EXISTS researcher_memory (
    researcher_memory_id bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    lesson_text          text        NOT NULL,
    sample_size          integer     NOT NULL,
    written_at           timestamptz NOT NULL,
    expires_at           timestamptz NOT NULL,
    reconfirmed_at       timestamptz NULL
);

-- Grain: per model per screen per report. Writer: CalibrationReporter.
CREATE TABLE IF NOT EXISTS calibration (
    model_id            text    NOT NULL,
    screen_id           text    NOT NULL,
    report_date         date    NOT NULL,
    brier_score         numeric NULL,
    reliability_buckets jsonb   NULL,
    sample_size         integer NULL,
    PRIMARY KEY (model_id, screen_id, report_date)
);

-- ============================================================= Operations ===

-- Grain: per stage per run. Writer: RunLog.
CREATE TABLE IF NOT EXISTS run_log (
    run_log_id   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_date     date        NOT NULL,
    stage        text        NOT NULL,
    status       text        NOT NULL,
    started_at   timestamptz NOT NULL,
    duration_ms  bigint      NULL,
    rows_written bigint      NULL,
    error        text        NULL
);

CREATE INDEX IF NOT EXISTS run_log_run_date_ix ON run_log (run_date, started_at);

-- Grain: per call. Writer: CostLedger.
-- Validator rejection counts are recorded here per model alongside spend.
CREATE TABLE IF NOT EXISTS cost_ledger (
    cost_ledger_id     bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    date               date    NOT NULL,
    model_id           text    NOT NULL,
    portfolio_id       text    NULL,
    input_tokens       bigint  NULL,
    cache_write_tokens bigint  NULL,
    cache_read_tokens  bigint  NULL,
    output_tokens      bigint  NULL,
    cost               numeric NOT NULL,
    was_batch          boolean NOT NULL DEFAULT false
);

-- Grain: per alert. Writer: ConcentrationMonitor.
CREATE TABLE IF NOT EXISTS alert (
    alert_id     bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    date         date    NOT NULL,
    alert_type   text    NOT NULL,
    detail       text    NULL,
    acknowledged boolean NOT NULL DEFAULT false
);

-- Grain: one row per provider in the chain. Writer: the UI, via the single
-- permitted write endpoint [D-51]. The only table the interface can write.
CREATE TABLE IF NOT EXISTS local_model_config (
    provider_order    integer     NOT NULL PRIMARY KEY,
    endpoint          text        NOT NULL,
    enabled           boolean     NOT NULL DEFAULT true,
    last_health_check timestamptz NULL,
    last_loaded_model text        NULL
);
