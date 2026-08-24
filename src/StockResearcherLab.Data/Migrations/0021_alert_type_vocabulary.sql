-- 0021_alert_type_vocabulary.sql
--
-- One CHECK, closing `alert.alert_type` to the two types this system has a writer for
-- [D-126, Q.4].
--
-- WHY IT WAS OPEN. `SCHEMA.md` gives `alert.alert_type` no vocabulary at all, so the two
-- strings C28 writes were the build's own and nothing stopped a third spelling of the
-- same condition arriving from a later phase. That is the shape 0018 closed for the gate
-- reasons and it is closed here for the same reason and by the same instrument.
--
-- WHY THESE TWO AND NOT MORE. Section 3's Writes column gives `alert` to C28 alone, and
-- section 18's failure table gives C28 exactly two conditions: megacap share above a
-- third over 20 days, and distinct tickers over 60 days below 250. The strings are those
-- two conditions' config keys less the bound, so a reader who has one has the other.
--
-- **Section 18 names four more conditions whose row says "Alert" and whose owner is not
-- C28**: C07 on completeness, C03 on the filing date substitution rate, C13 on every live
-- screen returning zero, and C26 on the cache miss rate. None of their components
-- declares a write to `alert` and no document says how those alerts are recorded, so
-- admitting a guess at their type strings here would be inventing a vocabulary rather
-- than closing one. When a phase gives one of them a writer it extends this constraint,
-- which is a migration and therefore visible.
--
-- WHY A CONSTRAINT RATHER THAN A CONSTANT IN CODE, which is 0018's argument unchanged. A
-- constant closes what this system writes and leaves the column able to hold a string no
-- reader can interpret. Preferring an impossible mistake over a documented one is section
-- 5 of `CLAUDE.md`.
--
-- THE LIST IS DUPLICATED, deliberately and visibly, between `AlertType` and here. A test
-- reads the constraint out of the catalogue and asserts the two agree, so the copy cannot
-- drift silently. A constraint built from a list in code at migration time is not
-- available to a plain `.sql` file, and a migration whose text changes with a rebuild is
-- a migration whose recorded hash means nothing.
--
-- NOT DESTRUCTIVE AND NOT GUARDED. `alert` holds zero rows: C28's only run to date is the
-- warm-up night of 2026-08-07, which raised none. A CHECK added to a populated table
-- would be validated against every row and would fail on the first one outside the list,
-- which is the case this does not have.

ALTER TABLE alert
    ADD CONSTRAINT alert_alert_type_vocabulary
    CHECK (alert_type IN (
        'megacap_share',
        'distinct_tickers_60d'
    ));
