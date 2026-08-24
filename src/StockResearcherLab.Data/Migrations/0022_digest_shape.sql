-- 0022_digest_shape.sql
--
-- All of phase 5's schema, in one migration against two tables holding zero rows
-- [D-131, D-134, 5.2]. Schema is free exactly once here, exactly as it was at 0017:
-- a second migration later runs against a populated `headline` and forces a second
-- conformance pass over `SCHEMA.md`, `guards.ps1` and `SchemaParityTests`.
--
-- THREE CHANGES, and one deliberate non-change.
--
--   1. `headline.content`, nullable, which is the column the digest actually reads.
--   2. `news_digest.provider` closed to the chain's link names by a CHECK.
--   3. `news_digest.model_name` required to be non-empty rather than merely non-null.
--   x. `attribution.digest_provider` is left exactly as it is, and stays null for ever
--      [D-135]. Stated here because a reader of this migration is the reader most
--      likely to wonder why it was not filled in.
--
-- NOT DESTRUCTIVE AND NOT GUARDED. `headline` and `news_digest` both hold zero rows.
-- 0017's guard exists because that migration drops and recreates a table 4.13 was
-- about to fill with nineteen million rows; nothing here drops anything, and a CHECK
-- added to an empty table validates against nothing.

-- --------------------------------------------------- 1. headline.content [D-131] ---
--
-- WHY THE TABLE COULD NOT HOLD AN ARTICLE. `headline` was created by 0001 with
-- ticker, date, published_at, title, source and url. `ARCHITECTURE.html` section 07
-- prices the whole local-enrichment step against articles of five to eight hundred
-- tokens, which is an argument about article text; a title is about fifteen tokens and
-- condensing three of them to 150 is not a condensation.
--
-- MEASURED BEFORE IT WAS ADDED, at 5.1. The `news` payload carries content on 275 of
-- 275 rows over the seven days ending 2026-08-12, at a median of 5,365 characters and
-- 1,248 real tokens counted by the model that would digest them. The teaser reading
-- that would have stopped this migration did not occur, and the transcript is at
-- `docs/evidence/phase-5/sweep-20260824.txt`.
--
-- NULLABLE, AND NULL MEANS THE PROVIDER SENT NO BODY. Not that the body was empty
-- [`CLAUDE.md` section 6]. A row carrying null content is excluded from the digest
-- input and counted, rather than being sent as an empty article.
--
-- NOT `text NOT NULL DEFAULT ''`, which is the shape that suggests itself and which
-- would make an absent body indistinguishable from an empty one at exactly the grain
-- where D-134 needs them separable.

ALTER TABLE headline
    ADD COLUMN IF NOT EXISTS content text NULL;

-- The digest reads a ticker's articles inside a window on `published_at` and takes the
-- most recent [D-133]. The 0001 index is on (ticker, date), which is the run's date
-- rather than the article's, so the ordering column has no index of its own.
CREATE INDEX IF NOT EXISTS headline_ticker_published_ix
    ON headline (ticker, published_at DESC);

-- ------------------------------------ 2. the provider vocabulary [D-134, D-25] -----
--
-- WHY IT WAS OPEN. `SCHEMA.md` gives `news_digest.provider` no vocabulary, so the two
-- strings C33 will write would be the build's own and nothing would stop a later phase
-- spelling one of these links a third way. That is what 0018 closed for the gate
-- reasons and 0021 for the alert types, and it is closed here by the same instrument
-- for the same reason.
--
-- WHY IT MATTERS MORE HERE THAN IT LOOKS. This column is how a fall-through is visible
-- in the record and how a later shift in results is separated from a change in the
-- evidence [D-29, INVARIANT 7's second boundary]. A column able to hold an
-- unrecognised string is a column able to hold a night nobody can attribute.
--
-- THE LIST IS DUPLICATED, deliberately and visibly, between `DigestProvider` and here.
-- A test reads the constraint out of the catalogue and asserts the two agree, so the
-- copy cannot drift silently. A constraint built from a list in code at migration time
-- is not available to a plain `.sql` file, and a migration whose text changes with a
-- rebuild is a migration whose recorded hash means nothing [0021].
--
-- A THIRD LINK IS AN INSERT AND ALSO A MIGRATION. `CONFIG_REFERENCE.md` says the chain
-- is an ordered list so adding a link is an insert; that is true of the order and not
-- of the vocabulary. Whichever phase adds one extends this constraint and
-- `DigestProvider` in the same checkpoint.

ALTER TABLE news_digest
    ADD CONSTRAINT news_digest_provider_vocabulary
    CHECK (provider IN (
        'local',
        'haiku'
    ));

-- ---------------------------------------- 3. model_name is non-empty [D-134] -------
--
-- NOT NULL was already there and it is not enough. An empty string satisfies NOT NULL
-- and records nothing, so a row could carry a provider and no model and read as a
-- complete attribution. D-29 asks for both the provider and the loaded model name, and
-- the loaded model name is the half that arrives from the server rather than from
-- config, so it is the half that can come back blank.
--
-- NOT `<> ''`, because a single space satisfies that. NOT `btrim(model_name) <> ''`
-- either, because the one-argument form trims spaces alone and a tab satisfies it.
-- That was not reasoned out: the test written beside this constraint asserts an empty
-- string, a space and a tab, and the tab passed the btrim form. A constraint holding
-- for two of three blank cases is the shape this corpus keeps removing, and the case
-- it misses is a model name arriving with a stray tab and reading as attributed.
--
-- A REGEX RATHER THAN AN ESCAPED TRIM SET, so the constraint carries no control
-- characters. An escaped trim set written into a `.sql` file is either escape
-- sequences or the characters themselves depending on how it was written, and one
-- of those two puts a literal newline and a carriage return inside a migration whose
-- text is hashed and recorded [`CLAUDE.md` §10 on line endings]. `[^[:space:]]` says
-- the same thing in printable characters: at least one character that is not
-- whitespace.

ALTER TABLE news_digest
    ADD CONSTRAINT news_digest_model_name_not_blank
    CHECK (model_name ~ '[^[:space:]]');

-- ------------------------------------------- what this migration deliberately omits ---
--
-- `headline.source` HAS NO INPUT AND GAINS NOTHING HERE. The `news` payload carries
-- content, date, link, sentiment, symbols, tags and title, measured at 5.1 over 275
-- rows. Nothing maps to source. The host of `link` is derivable and deriving it is a
-- choice rather than a read, so C29 writes the column null and the column stays,
-- which is `slot_filled` [D-124] and `attribution.digest_provider` [D-135] a third
-- time. Removing it is a decision nobody has taken and inventing a value is worse
-- than an absence.
--
-- `sentiment` AND `tags` GAIN NO COLUMNS. Both are carried by the payload and neither
-- is asked for by any document. Per-article sentiment is not `sentiment_daily`'s
-- aggregate from the `sentiments` endpoint and would be a second measurement of the
-- same thing under a name that does not say so.
--
-- `attribution.digest_provider` IS NOT TOUCHED [D-135]. C14 inserts the attribution
-- row at 18:30 and the digest exists at 18:33, C21 owns the only update and only of
-- the nine return columns, and `SCHEMA.md` says no third component writes there. The
-- provider is on `news_digest` at the same grain, so the record is complete by join
-- rather than by copy.
