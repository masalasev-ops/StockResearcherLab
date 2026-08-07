-- 0002_statement_fields_and_grains.sql
--
-- Four things, all owed to checkpoint 1.4.
--
--   1. The statement fields on fundamental_snapshot, which 0001 left out.
--   2. The flow_daily column rename A1.a found, correcting the schema to the
--      architecture rather than the other way round.
--   3. Unique indexes giving insider_transaction and events an upsertable grain,
--      which they have never had [A7, A11, D-68].
--   4. accession_number on insider_transaction, which is part of that grain.
--
-- Snapshot-first: 0001 is not edited, this file sits beside it. Every statement is
-- IF NOT EXISTS or guarded, so a second run changes nothing.

-- ================================================ 1. Statement fields ========
--
-- SCHEMA.md declares fundamental_snapshot as "plus the statement fields" and 0001
-- created only the six key columns, on the same "the phase that owns them adds
-- them" footing as indicator_daily. This is that phase.
--
-- The column list is driven by what valuation_daily needs in phase 2, not by what
-- the provider happens to send: the balance sheet alone carries about sixty fields
-- per period and ingesting all of them would be storing data because it is there.
-- Every column below is an input to one of valuation_daily's thirteen.
--
-- All money is numeric, never float or double [INVARIANT 16]. Every column is
-- nullable because absent is the ordinary state for a great deal of this data and
-- null means unknown, never zero [CLAUDE.md section 6].

ALTER TABLE fundamental_snapshot
    -- Balance sheet. Feeds roic, net_debt_ebitda, accruals, cash_on_hand.
    ADD COLUMN IF NOT EXISTS total_assets                 numeric NULL,
    ADD COLUMN IF NOT EXISTS total_liab                   numeric NULL,
    ADD COLUMN IF NOT EXISTS total_stockholder_equity     numeric NULL,
    ADD COLUMN IF NOT EXISTS cash                         numeric NULL,
    ADD COLUMN IF NOT EXISTS cash_and_equivalents         numeric NULL,
    ADD COLUMN IF NOT EXISTS short_term_investments       numeric NULL,
    ADD COLUMN IF NOT EXISTS net_debt                     numeric NULL,
    ADD COLUMN IF NOT EXISTS short_long_term_debt_total   numeric NULL,
    ADD COLUMN IF NOT EXISTS long_term_debt               numeric NULL,
    ADD COLUMN IF NOT EXISTS inventory                    numeric NULL,
    ADD COLUMN IF NOT EXISTS net_receivables              numeric NULL,
    ADD COLUMN IF NOT EXISTS accounts_payable             numeric NULL,
    ADD COLUMN IF NOT EXISTS total_current_assets         numeric NULL,
    ADD COLUMN IF NOT EXISTS total_current_liabilities    numeric NULL,
    ADD COLUMN IF NOT EXISTS property_plant_equipment_net numeric NULL,
    ADD COLUMN IF NOT EXISTS goodwill                     numeric NULL,
    ADD COLUMN IF NOT EXISTS intangible_assets            numeric NULL,

    -- Income statement. Feeds ev_ebit, roic, gross_margin_4q_change,
    -- revenue_growth_4q_trend, net_debt_ebitda, accruals.
    ADD COLUMN IF NOT EXISTS total_revenue                numeric NULL,
    ADD COLUMN IF NOT EXISTS cost_of_revenue              numeric NULL,
    ADD COLUMN IF NOT EXISTS gross_profit                 numeric NULL,
    ADD COLUMN IF NOT EXISTS operating_income             numeric NULL,
    ADD COLUMN IF NOT EXISTS ebit                         numeric NULL,
    ADD COLUMN IF NOT EXISTS ebitda                       numeric NULL,
    ADD COLUMN IF NOT EXISTS net_income                   numeric NULL,
    ADD COLUMN IF NOT EXISTS income_before_tax            numeric NULL,
    ADD COLUMN IF NOT EXISTS income_tax_expense           numeric NULL,
    ADD COLUMN IF NOT EXISTS interest_expense             numeric NULL,
    ADD COLUMN IF NOT EXISTS research_development         numeric NULL,

    -- Cash flow. Feeds fcf_yield, quarterly_burn_rate, accruals.
    ADD COLUMN IF NOT EXISTS cash_from_operating          numeric NULL,
    ADD COLUMN IF NOT EXISTS cash_from_investing          numeric NULL,
    ADD COLUMN IF NOT EXISTS cash_from_financing          numeric NULL,
    ADD COLUMN IF NOT EXISTS depreciation                 numeric NULL,
    ADD COLUMN IF NOT EXISTS dividends_paid               numeric NULL,
    ADD COLUMN IF NOT EXISTS sale_purchase_of_stock       numeric NULL,

    -- Shares, which is what makes market capitalisation derivable from
    -- price_daily rather than needing a per-ticker call [1.5]. It arrives in its
    -- own block rather than on a statement.
    ADD COLUMN IF NOT EXISTS shares_outstanding           numeric NULL,

    -- Which statement supplied filing_date, so a disagreement between the balance
    -- sheet and the income statement is visible rather than resolved silently. The
    -- probe found them disagreeing on NVDA in 2 of 109 periods.
    ADD COLUMN IF NOT EXISTS filing_date_source           text NULL;

-- ======================== 1a. filing_date_effective becomes nullable =========
--
-- D-62 substitutes period_end plus that ticker's widest clean gap. A ticker with
-- ZERO clean gaps has no widest to substitute from, and NOT NULL could only be
-- satisfied by writing a date that is not true: period_end makes the row readable
-- immediately, which is the lookahead D-62 exists to prevent, and a universal
-- constant is what D-62 explicitly rejected.
--
-- Null means no usable filing date and none derivable. Every read filters
-- filing_date_effective <= date, so such a row is unreadable by construction rather
-- than by anyone remembering to exclude it.
--
-- The CHECK is narrower than the NOT NULL it replaces rather than weaker. It still
-- catches the case NOT NULL was pointing at, a row losing its date to a bug, while
-- permitting the one case NOT NULL could only handle by fabricating.

ALTER TABLE fundamental_snapshot ALTER COLUMN filing_date_effective DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fundamental_snapshot_effective_date_ck')
    THEN
        ALTER TABLE fundamental_snapshot
            ADD CONSTRAINT fundamental_snapshot_effective_date_ck
            CHECK (filing_date_effective IS NOT NULL
                   OR filing_date_unknown_reason <> 'none');
    END IF;
END $$;

-- ==================================================== 2. flow_daily rename ===
--
-- ARCHITECTURE.html sections 3 and 5 and D-61 all name this insider_net_90d_usd.
-- SCHEMA.md and 0001 named it insider_net_usd_90d. The architecture constrains the
-- code, so the schema was wrong [A1.a, CLAUDE.md sections 6 and 13].
--
-- Renamed rather than dropped and recreated: the table has never held a row, so
-- either would do, and a rename says what happened where a drop would not.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'flow_daily'
          AND column_name = 'insider_net_usd_90d')
    THEN
        ALTER TABLE flow_daily RENAME COLUMN insider_net_usd_90d TO insider_net_90d_usd;
    END IF;
END $$;

-- ====================================== 3 and 4. Grains for the two tables ===
--
-- Both carry a bigint GENERATED ALWAYS AS IDENTITY primary key and neither had a
-- unique constraint on any column tuple, so ON CONFLICT against either raised
-- before a row was written and D-68's idempotence was unreachable [A7].
--
-- NULLS NOT DISTINCT is the point of the exercise [A11]. Postgres treats nulls as
-- distinct in a unique index by default, so a row whose key contains a null never
-- matches ON CONFLICT and every re-run inserts another copy: the index exists, the
-- statement succeeds, the row count climbs, and nothing fails. Both tuples below
-- contain a nullable column.

ALTER TABLE insider_transaction
    ADD COLUMN IF NOT EXISTS accession_number text NULL;

-- One form 4 filing nests many transactions, so the filing's accession number
-- alone is not the grain: it is the accession number plus the transaction's own
-- attributes [1.9]. Whether that tuple is genuinely unique in provider data is
-- answered by 1.7 against real rows rather than assumed here, and D-68's reopening
-- clause is the route if it is not.
CREATE UNIQUE INDEX IF NOT EXISTS insider_transaction_grain_ux
    ON insider_transaction (
        ticker, accession_number, reporting_owner_name, transaction_code,
        transaction_date, shares_amount)
    NULLS NOT DISTINCT;

-- events is ticker by event. announced_date is excluded from the key: it is the
-- attribute most likely to be revised after the fact, and a revision should update
-- the row rather than insert a second one for the same event.
CREATE UNIQUE INDEX IF NOT EXISTS events_grain_ux
    ON events (ticker, event_type, event_date)
    NULLS NOT DISTINCT;
