-- 0019_gate_state.sql
--
-- `gate_result.gate_state`, carrying `passed` or `passed_partial` [D-117].
--
-- WHY THE COLUMN IS HERE AND NOT COMPUTED IN C14. D-117 says what `gate_state` means
-- and never says which component works it out. `attribution.gate_state` is C14's to
-- write, and the fact behind it is whether each gate reason was structurally evaluable
-- on the date, which is knowable only from `events`, `position` and `trade_outcome`.
-- Those are C12's three stores and none of them is in C14's Reads cell.
--
-- The three ways to close that, and why this one:
--
--   1. Amend C14's Reads cell to add all three. That is an eighth Reads-cell amendment,
--      and it puts the phase-7 stores in front of a component that has no other reason
--      to see them.
--   2. Derive it from `gate_result` alone, by treating a reason no name carries as a
--      reason that could not fire. That conflates "nothing fired" with "could not fire",
--      which is the exact distinction D-117 asks to be labelled.
--   3. This. The component that already reads the three stores records what it found,
--      and C14 carries it onto the attribution row from a table it already reads.
--
-- REPORTED as a design decision taken during the build rather than authored. It is
-- reversible until 4.14 freezes the attribution record.
--
-- WHY A SECOND SCHEMA MIGRATION AFTER 0018. Same bargain 0018 states: `gate_result`
-- carries fixture rows only, no record has started, and the guard below is what makes
-- that checkable rather than assumed.

-- ---------------------------------------------------------------- the guard ---

DO $guard$
DECLARE started bigint;
BEGIN
    SELECT (SELECT count(*) FROM candidate_set) + (SELECT count(*) FROM attribution)
      INTO started;

    IF started <> 0 THEN
        RAISE EXCEPTION
            'candidate_set and attribution together hold % row(s), so the record has '
            'started. This migration clears gate_result to add a NOT NULL column with no '
            'default, which would discard labels the record was built against. Clearing '
            'a started record is a deliberate act taken by a human at 4.14 and not a '
            'migration re-run [D-111, RUNBOOK.md].', started;
    END IF;
END
$guard$;

-- --------------------------------------------------------------- the column ---

-- Cleared rather than backfilled. Every row here was written by a 4.8 fixture, and
-- stamping them with a state C12 never computed would put a value nobody chose into a
-- column whose whole job is to say what was and was not evaluable [D-110's reasoning
-- for surfaced_as].
DELETE FROM gate_result;

-- NOT NULL with no DEFAULT, for surfaced_as's reason: a column that defaults is a
-- column a writer can decline to think about, and the value written is then the one
-- nobody chose [D-110].
ALTER TABLE gate_result ADD COLUMN gate_state text NOT NULL;

-- `gated` is deliberately absent from the vocabulary. A gated name has no attribution
-- row to carry a state, so the only two values that can reach one are these [D-117].
ALTER TABLE gate_result ADD CONSTRAINT gate_result_gate_state_check
    CHECK (gate_state IN ('passed', 'passed_partial'));
