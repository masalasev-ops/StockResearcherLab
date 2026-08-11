-- 0005_regime_label.sql
--
-- Phase 2 checkpoint 2.9. D-80's three values, enforced by the database.
--
-- regime_label segments analysis: it is carried in the cached prefix, stored on
-- every attribution row, and shown on screen U1. A drifted or mistyped value lands
-- in its own bucket in every segmentation without ever erroring, which is the
-- failure mode this system's whole shape is built against.
--
-- So the constraint is here rather than in the writer, exactly as
-- fundamental_snapshot.filing_date_unknown_reason is and for the same reason. A
-- writer-side check protects one writer; a CHECK protects the column.
--
-- NOT NULL as well, because the label is derived from breadth and the benchmark and
-- neither can be absent on a date the stage ran. VIX contributes nothing to it and
-- is null, and allowing the label to go null with it would degrade all three
-- readers over a column none of them reads [D-80].
--
-- market_context_daily has never held a row, so the column can be tightened rather
-- than backfilled first.
--
-- Snapshot-first: 0001 to 0004 are not edited, this file sits beside them. Both
-- statements are guarded, so a second run changes nothing.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'market_context_daily_regime_label_ck'
    ) THEN
        ALTER TABLE market_context_daily
            ADD CONSTRAINT market_context_daily_regime_label_ck
            CHECK (regime_label IN ('risk_on', 'risk_off', 'mixed'));
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'market_context_daily'
          AND column_name = 'regime_label'
          AND is_nullable = 'YES'
    ) THEN
        ALTER TABLE market_context_daily ALTER COLUMN regime_label SET NOT NULL;
    END IF;
END $$;
