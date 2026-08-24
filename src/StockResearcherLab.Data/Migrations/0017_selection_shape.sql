-- 0017_selection_shape.sql
--
-- All of phase 4's schema, in one migration against six tables holding zero rows
-- [D-110, D-111]. Schema is free exactly once here: a second migration later runs
-- against a populated `screen_score_daily` and forces a second conformance pass over
-- `SCHEMA.md`, `guards.ps1` and `SchemaParityTests`.
--
-- FOUR CHANGES, and every one of them is cheap now and expensive after 4.13.
--
--   1. `screen_score_daily` recreated partitioned by date with its key reordered.
--   2. A partial index over the ranked rows only.
--   3. `attribution.surfaced_as`, NOT NULL, CHECK'd, no DEFAULT.
--   4. `attribution.score_per_screen` held to an object-of-objects shape, and the
--      `candidate_attribution` view.
--
-- THE DROP IS GUARDED RATHER THAN TRUSTED. `screen_score_daily` is dropped and
-- recreated because a primary key cannot be reordered and a table cannot be
-- partitioned in place. Run after 4.13 that would discard roughly 19 million rows,
-- so the guard below raises instead. A re-run against a populated table fails loudly;
-- a re-run against an empty one is a no-op the ledger skips anyway [migrate.ps1].

-- ---------------------------------------------------------------- 1. the guard ---

DO $guard$
DECLARE existing bigint;
BEGIN
    SELECT count(*) INTO existing FROM screen_score_daily;
    IF existing <> 0 THEN
        RAISE EXCEPTION
            'screen_score_daily holds % row(s). 0017 drops and recreates this table to '
            'reorder its key and partition it, which would discard them. If the '
            'partitioned shape is genuinely wanted over a populated table, that is a '
            'deliberate truncate followed by a re-run of 4.13, taken by a human, and '
            'not a migration re-run [D-111].', existing;
    END IF;
END
$guard$;

-- --------------------------------------- 2. the table, partitioned and rekeyed ---

-- The key becomes (date, screen_id, ticker). Every read this system makes of this
-- table is one date: the allocator's nightly read and the backfill's read once per
-- date. The old key led on `ticker`, which is the one column those queries do not
-- constrain [D-111, SCREEN_LIFECYCLE.md section 9.3].
--
-- NO DEFAULT PARTITION, deliberately. A date outside the declared range fails loudly
-- rather than landing in a child nothing queries. The range covers the backfill
-- window start of 2021-01-04 through the end of 2027, which is over a year past the
-- current frontier; extending it is one CREATE TABLE and no data movement.

DROP TABLE screen_score_daily;

CREATE TABLE screen_score_daily (
    date               date    NOT NULL,
    screen_id          text    NOT NULL,
    ticker             text    NOT NULL,
    score              real    NULL,
    rank_within_screen integer NULL,
    config_version     integer NOT NULL,
    PRIMARY KEY (date, screen_id, ticker)
) PARTITION BY RANGE (date);

CREATE TABLE screen_score_daily_2021 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2021-01-01') TO ('2022-01-01');
CREATE TABLE screen_score_daily_2022 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2022-01-01') TO ('2023-01-01');
CREATE TABLE screen_score_daily_2023 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2023-01-01') TO ('2024-01-01');
CREATE TABLE screen_score_daily_2024 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
CREATE TABLE screen_score_daily_2025 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE screen_score_daily_2026 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE TABLE screen_score_daily_2027 PARTITION OF screen_score_daily
    FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');

-- The floor admits about two percent of the population, so this index covers about
-- two percent of the table. SCREEN_LIFECYCLE.md section 9.3's option 1, a full index
-- on the largest table in the system maintained across roughly 19 million inserts to
-- serve a query wanting two percent of the rows, is rejected [D-111].
CREATE INDEX screen_score_daily_ranked_idx
    ON screen_score_daily (date, screen_id, rank_within_screen)
    WHERE rank_within_screen IS NOT NULL;

-- ------------------------------------------------------------ 3. surfaced_as ---

-- NOT NULL with no DEFAULT, for security_daily.is_active's reason: a column that
-- defaults is a column a writer can decline to think about, and the value that gets
-- written is then the one nobody chose. A writer that has not decided fails at the
-- column [D-110].
--
-- The CHECK rather than a writer-side test, for D-80's reason. This column segments
-- every analysis in SCREEN_LIFECYCLE.md section 4.5, so a drifted value would land in
-- its own bucket in every one of them without ever erroring.
ALTER TABLE attribution ADD COLUMN surfaced_as text NOT NULL;

ALTER TABLE attribution ADD CONSTRAINT attribution_surfaced_as_check
    CHECK (surfaced_as IN ('candidate', 'shadow'));

-- ------------------------------------- 4. score_per_screen's shape, and the view ---

-- screen id to an object of score and rank. Held by a constraint so the flat shape
-- cannot be written at all, rather than being refused by whichever writer remembers
-- [D-110, SCREEN_LIFECYCLE.md section 4.4].
--
-- A function because a CHECK expression may not contain a subquery and the test is
-- over the members of a jsonb object. IMMUTABLE and PARALLEL SAFE because it reads
-- only its argument.
CREATE OR REPLACE FUNCTION score_per_screen_is_object_map(v jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $fn$
    SELECT v IS NULL
        OR (jsonb_typeof(v) = 'object'
            AND NOT EXISTS (
                SELECT 1 FROM jsonb_each(v) AS e WHERE jsonb_typeof(e.value) <> 'object'
            ));
$fn$;

ALTER TABLE attribution ADD CONSTRAINT attribution_score_per_screen_object_map
    CHECK (score_per_screen_is_object_map(score_per_screen));

-- Every reader meaning candidate reads the view. Reading the table becomes a
-- deliberate act rather than the default, which inverts which mistake is easy
-- [D-110, SCREEN_LIFECYCLE.md section 4.5].
CREATE VIEW candidate_attribution AS
    SELECT * FROM attribution WHERE surfaced_as = 'candidate';
