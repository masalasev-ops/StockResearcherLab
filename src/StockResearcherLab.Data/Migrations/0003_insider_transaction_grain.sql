-- 0003_insider_transaction_grain.sql
--
-- D-68's reopening clause, fired by measurement at 1.7.
--
-- 0002 gave insider_transaction a unique index on
--   (ticker, accession_number, reporting_owner_name, transaction_code,
--    transaction_date, shares_amount)
-- which A7 proposed and explicitly left for 1.7's sweep to confirm against real
-- rows rather than assume. The sweep says no.
--
-- Measured over 1,069 real transactions across eight tickers:
--
--   A7's tuple as written                                172 collisions
--   + security_title                                      31 collisions
--   + security_title + price_per_share + shares_owned_after 5 collisions
--   accession_number + side + ordinal                     UNIQUE
--
-- No combination of a transaction's own attributes is unique. Two line items in one
-- filing can be genuinely identical on every value the provider sends, and the
-- examples are ordinary: an option exercise reported as Common Stock acquired and
-- as Restricted Stock Units disposed, same owner, same date, same code, same share
-- count.
--
-- THE GRAIN IS THE FILING PLUS THE LINE'S POSITION IN IT, which is what a Form 4
-- actually is: an ordered document, not a set of facts. SCHEMA.md already says
-- "ticker by filing by transaction, the source's own", and this makes that literal
-- rather than approximating it with attributes.
--
-- Left as an index change rather than an attempt to derive a key from values,
-- because a key derived from values that are legitimately repeatable is not a key,
-- and an upsert on one collapses two real transactions into one silently. That is
-- D-68 not holding, which is the failure the clause exists to catch.

ALTER TABLE insider_transaction
    -- Which array the line came from. A Form 4 reports non-derivative and
    -- derivative holdings separately and they are numbered independently.
    ADD COLUMN IF NOT EXISTS transaction_side text NULL,

    -- Zero-based position within that array, as the provider ordered it. Ordinal
    -- rather than a hash of the row, because two identical rows must stay two rows.
    ADD COLUMN IF NOT EXISTS transaction_ordinal integer NULL,

    -- Kept because the S4 rubric reads them and 0002's tuple was the only thing
    -- that needed them; they are attributes now rather than key parts.
    ADD COLUMN IF NOT EXISTS security_title text NULL,
    ADD COLUMN IF NOT EXISTS shares_owned_after numeric NULL,
    ADD COLUMN IF NOT EXISTS reporting_owner_cik text NULL;

DROP INDEX IF EXISTS insider_transaction_grain_ux;

-- NULLS NOT DISTINCT for the same reason 0002 used it: Postgres treats nulls as
-- distinct by default, so a row whose key contains a null never matches
-- ON CONFLICT and every re-run inserts another copy while the statement succeeds
-- and the count climbs [A11].
CREATE UNIQUE INDEX IF NOT EXISTS insider_transaction_grain_ux
    ON insider_transaction (ticker, accession_number, transaction_side, transaction_ordinal)
    NULLS NOT DISTINCT;
