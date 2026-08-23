-- 0015_universe_rejection.sql
--
-- The criterion C01 rejected a name on, per ticker per evaluation date [D-108].
--
-- WHY A STORE AND NOT A QUERY. "Why is this obvious company not in my universe" is the
-- question the membership panel exists for and it is the half that currently takes a
-- query nobody can write, because the answer is not in the store at all. C01 counts six
-- rejections into one run log line and writes no per-ticker row, and three further
-- criteria are applied together inside one SQL filter in LiquidAsync, so a name failing
-- price, dollar volume or history is absent from the counted loop and absent from the
-- counts.
--
-- RECONSTRUCTING IT LATER DOES NOT WORK, which is D-92's argument arriving again. The
-- clean gap count is computed as of the date and never stored [M.1]. Market
-- capitalisation is computed from a share count readable on that date and is stored
-- only for members. Instrument type comes from a provider symbol list that is stored
-- nowhere. Three of the nine criteria have no persisted input at all, so a reader
-- cannot re-derive the verdict however carefully it tries.
--
-- ONE CRITERION PER ROW, BEING THE ONE THAT REJECTED. D-4 is a conjunction and a name
-- fails on the first criterion it fails, so recording all nine would suggest an
-- independent evaluation the code does not perform. What the column means is the
-- criterion the evaluation stopped on, which is a property of C01's order rather than
-- of D-4. SCHEMA.md states that so a later reader does not read it as the only
-- criterion the name failed.
--
-- THE CHECK IS IN THE DATABASE for the same reason regime_label's is [D-80, 0005]. This
-- column segments every count taken off it, so a drifted or mistyped value would land
-- in its own bucket in every segmentation without ever erroring. A writer-side check
-- protects one writer; a constraint protects the column.
--
-- THE ENUMERATED VALUES ARE IN TWO GROUPS AND THE ORDER IS C01's. The first three are
-- LiquidAsync's, applied together in one statement and classified rather than filtered
-- from D-108 onward. The remaining six are MembershipAsync's loop, in the order it
-- tests them. A name reaching the loop has already passed the first three.
--
-- NO COLUMN FOR THE THRESHOLD IT FAILED AGAINST. That is configuration, resolved as of
-- the evaluation date by the same rule every other reader uses [D-43, INVARIANT 13],
-- and storing it here would be a second copy that can disagree with config_rows.
--
-- Snapshot-first: 0001 to 0014 are not edited. The statements are guarded, so a second
-- run changes nothing and ci.ps1's second migrate still prints nothing to apply.

-- Grain: ticker by evaluation date. Writer: UniverseBuilder.
CREATE TABLE IF NOT EXISTS universe_rejection (
    ticker    text NOT NULL,
    date      date NOT NULL,
    criterion text NOT NULL,
    PRIMARY KEY (ticker, date),
    CONSTRAINT universe_rejection_criterion_ck CHECK (criterion IN (
        'below_min_price',
        'below_min_dollar_volume',
        'insufficient_history',
        'not_common_stock',
        'delisted_on_date',
        'no_fundamentals',
        'below_clean_gaps',
        'no_share_count',
        'below_market_cap'))
);

-- The write recomputes one evaluation date whole, deleting that date before inserting
-- it, so the delete constrains the column the primary key does not lead on. This is the
-- same shape 0007 added to the five ticker-by-day tables and for the same reason.
CREATE INDEX IF NOT EXISTS universe_rejection_date_ix
    ON universe_rejection (date, ticker);
