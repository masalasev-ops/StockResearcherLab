-- 0020_jaccard.sql
--
-- The Jaccard overlap of two text arrays, for §5's pre-registered persistence measure
-- [4.13].
--
-- WHY A FUNCTION. The measure compares a screen's ranked set against the set it ranked
-- one, five and twenty-one sessions earlier. Each comparison is an intersection and a
-- union over two arrays, which inline is two subqueries, and three lags makes six copies
-- of one expression. Six copies is where one of them quietly differs, and a persistence
-- figure that differs between lags for that reason reads as decay.
--
-- WHY IT IS SCHEMA RATHER THAN CODE. It is called from inside an aggregate over twenty
-- million rows. A C# implementation would mean reading the sets out of the database and
-- comparing them in memory, which is the row-by-row shape `CLAUDE.md` §6 rejects for the
-- percentile and screen stages and rejects here for the same reason.
--
-- THIS IS THE THIRD SCHEMA MIGRATION IN A PHASE WHOSE PLAN PUT ALL SCHEMA AT 4.1, and it
-- is reported with 0018 and 0019 rather than counted quietly. It creates no table, alters
-- none, and touches no row, so the bargain 0017's header states is not engaged at all:
-- there is nothing here a populated table would make expensive.
--
-- IMMUTABLE and PARALLEL SAFE because it reads only its arguments. STRICT so a null
-- argument yields null rather than an empty-set answer: a date with no prior ranked set
-- has no overlap rather than an overlap of zero, and the measure filters those out
-- instead of averaging them in.

CREATE OR REPLACE FUNCTION jaccard(a text[], b text[])
RETURNS numeric
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $fn$
    SELECT CASE
        WHEN cardinality(a) = 0 AND cardinality(b) = 0 THEN NULL
        ELSE (
            SELECT count(*)::numeric
            FROM (SELECT unnest(a) INTERSECT SELECT unnest(b)) AS i
        ) / NULLIF((
            SELECT count(*)::numeric
            FROM (SELECT unnest(a) UNION SELECT unnest(b)) AS u
        ), 0)
    END;
$fn$;
