-- 0023_request_options.sql
--
-- One nullable column on a two-row table, and the value for the row that predates it
-- [D-144, 5.3's second commit].
--
-- WHY THIS IS NOT IN 0022. 5.2 landed all of the phase's schema in one migration on
-- the argument that schema is free exactly once, and that argument held for what was
-- known then. This column comes from a decision, D-144, that did not exist when 0022
-- was written, because the fact behind it had not been measured: nothing in this
-- corpus knew the local model was a reasoning model until 5.1 called it. A second
-- migration against a table holding two seeded rows costs a conformance pass over
-- `SCHEMA.md` and the parity tests and nothing else, which is the whole reason 0022's
-- argument was about `headline` and `news_digest` rather than about this table.
--
-- WHAT THE COLUMN HOLDS. The provider-specific parameters a link's request must carry
-- beyond the ones every link takes. Today that is `{"reasoning_effort": "none"}` on
-- the local link and null on the secondary, which needs none.
--
-- WHY A COLUMN RATHER THAN A CONFIG KEY [D-144]. This is a property of the endpoint's
-- loaded model rather than of the digest step. Swap the loaded model and the setting
-- changes with it; a `digest.*` key stays behind describing a model that is no longer
-- there, and does so silently, because nothing reads a key against the model it was
-- written for. `local_model_config` is already the row that says where a link is and
-- whether it is enabled.
--
-- WHY NULLABLE AND NOT `DEFAULT '{}'::jsonb`. An empty object is a request shape that
-- was chosen, and null is a link that needs none. The distinction is not decorative
-- here: 5.5 asserts that the local link with its options removed reports unhealthy,
-- and an empty object is exactly the state that assertion is written against, so a
-- default that manufactured one would make the two cases indistinguishable at the
-- column [`CLAUDE.md` section 6].
--
-- NO CHECK ON ITS SHAPE. The keys are a provider's, not this system's, and a
-- constraint enumerating them would have to be extended by whichever phase adds a
-- link, against a vocabulary no document here owns. That is the opposite of
-- `news_digest.provider`, whose two values are named by D-25 and are this system's.

ALTER TABLE local_model_config
    ADD COLUMN IF NOT EXISTS request_options jsonb NULL;

-- ------------------------------------------------ the row that predates the column ---
--
-- THE SEEDER CANNOT REACH THIS ROW AND THAT IS WHY THE VALUE IS HERE. `SeedChainAsync`
-- inserts `ON CONFLICT (provider_order) DO NOTHING`, deliberately, so that a row the
-- operator has since edited through the one permitted write endpoint is left exactly
-- as they left it [D-51, D-136]. On any database seeded before this migration the two
-- rows already exist, so the seeder does nothing for ever and the local link's
-- `request_options` stays null. Found by 5.3's own test failing against a database
-- seeded at the first commit, rather than by reasoning.
--
-- WHAT THAT WOULD HAVE COST, which is why it is worth a migration rather than a note.
-- The local link with no `reasoning_effort` returns HTTP 200, a normal usage block and
-- zero characters of content [5.1]. Under D-144 the health check then reports it
-- unhealthy and the chain digests every candidate on the paid link, on a machine whose
-- local server is up and answering. That is visible in the run log rather than silent,
-- the health check being what D-144 changed, but it is the failure the decision exists
-- to prevent arriving through the one path the decision did not describe.
--
-- GUARDED ON THE COLUMN STILL BEING NULL, so a re-run is a no-op and a row the
-- operator has since written is not disturbed. That is 0013's shape exactly, where the
-- sweep marker moved into a column added in the same migration.
--
-- THE LITERAL IS DUPLICATED, deliberately and visibly, between `ConfigSeeder.ChainLinks`
-- and here, which is 0022's provider vocabulary a second time. A test asserts the two
-- agree, so the copy cannot drift; a migration whose text is built from code at
-- migration time is a migration whose recorded hash means nothing.
--
-- ONLY `provider_order` 1. The secondary needs no provider-specific parameter and its
-- null is the answer rather than a gap, so there is nothing to backfill there and
-- writing an empty object would be inventing the state 5.5 asserts against.

UPDATE local_model_config
   SET request_options = '{"reasoning_effort": "none"}'::jsonb
 WHERE provider_order = 1
   AND request_options IS NULL;
