-- 0018_gate_reason_vocabulary.sql
--
-- One CHECK, closing `gate_result.reasons` to the five reasons `ARCHITECTURE.html`
-- section 03 names [D-117].
--
-- WHY THIS IS A SECOND MIGRATION IN A PHASE WHOSE PLAN PUT ALL SCHEMA AT 4.1, stated
-- rather than slipped in. 4.1's scope is all of phase 4's schema in one migration
-- against tables holding zero rows, and 0017's own header says schema is free exactly
-- once. This is the same bargain and not a breach of it: `gate_result` holds zero rows
-- today, 4.8 is its first writer, and the cost of a second pass over `SCHEMA.md`,
-- `guards.ps1` and `SchemaParityTests` is paid here rather than after 4.14 freezes
-- anything. It is reported in the checkpoint commit as a deviation from the plan's
-- shape.
--
-- WHY A CONSTRAINT RATHER THAN AN ENUM IN CODE. 4.8's definition of done asks that the
-- reason vocabulary be closed and that a value outside it fail. A closed enum in
-- `GateReasons` closes what this system writes and leaves the column able to hold a
-- string no reader can interpret, which `GateReasons.Parse` would then throw on at read
-- time, one night later and one component away from whatever wrote it. Preferring an
-- impossible mistake over a documented one is section 5 of `CLAUDE.md`.
--
-- THE LIST IS DUPLICATED, deliberately and visibly. It lives in `GateReason` and here.
-- A test asserts the two agree by reading the constraint out of the catalogue, so the
-- copy cannot drift silently; a constraint built from a list in code at migration time
-- is not available to a plain `.sql` file, and a migration whose text changes with a
-- rebuild is a migration whose recorded hash means nothing.

ALTER TABLE gate_result
    ADD CONSTRAINT gate_result_reasons_vocabulary
    CHECK (reasons <@ ARRAY[
        'earnings_blackout',
        'gap',
        'halt',
        'already_held',
        'cooldown'
    ]::text[]);
