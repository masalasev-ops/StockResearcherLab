# Phase 5 — Digest chain

**Target** phase 5, the headline ingest for candidates, the local client over the
OpenAI-compatible endpoint, the secondary client, the ordered chain with health checks,
the nightly rotation of two candidates to the secondary, and the gate.

**Authored** 2026-08-24, at phase 4's sign-off, as `BUILD_PLAN.md` requires of the next
phase's detail.

**Read against** `ARCHITECTURE.html` §01, §03, §04, §07, §16 and §18, `SCHEMA.md`,
`RUNBOOK.md`, `CONFIG_REFERENCE.md`, `DECISIONS.md` D-23 to D-29, D-51, D-55, D-60, D-90,
D-96, D-101, D-105, D-106, D-109, D-117, D-119, D-124, D-126 and D-130, `FIXTURES.md`,
`VALIDITY.md` §5, `GLOSSARY.md`, `prompts/digest-instruction.md`, `prompts/README.md` and
`CLAUDE.md` §2, §3, §5, §6, §9, §11, §13 and §14, at HEAD `44efc7c`.

Eleven claims below rest on the code or on the store rather than on a document and were
read there [`CLAUDE.md` §7]: `NightlyRun.EveningOrder`, `PipelineComposition.BuildRegistry`
and `AllOwnersForConformance`, `StageRegistry`, `StageContracts` with `IReadOwner`,
`IWriteOwner` and `StageResult.ZeroRowsExpected`, `WriteOwnershipConformanceTests`'
`ExpectedOwners = 18`, `ConfigResolutionTests`' `ConfigSeeder.Keys.Count = 95`,
`AlertTypes` and its closed vocabulary, `guards.ps1`'s five checks and its
`ExpectedMonetary = 19`, the `headline`, `news_digest`, `cost_ledger` and
`local_model_config` definitions in `0001_snapshot.sql`, the absence of any
`Pipeline/Decide/` folder and of any component named `HeadlineIngestor`, `NewsDigester`,
`LocalModelClient` or `CostLedger` anywhere under `src/`, and the news row of
`docs/evidence/phase-1/endpoint-sweep-20260807.txt`.

**Status** `DRAFT`, which is the only value this corpus uses for a plan. The numbered
checkpoints in §6 are phase scope and are authored into `BUILD_PLAN.md` by a human rather
than from here, as are the decision clauses in §4 and §12 [`CLAUDE.md` §13].

**§4's twelve were adopted 2026-08-24 on the operator's direction and are in
`DECISIONS.md`. §12's two are not**: D-143 and D-144 were drafted at 5.2, after 5.1
measured two things this document had assumed, and they are unauthored. The checkpoints
have begun landing, so this plan is no longer un-issued; it is archived to
`prompts/spent/` when every checkpoint has landed, as phase 4's was.

**Archived on 2026-08-25**, all fourteen having landed, to
`prompts/spent/phase-5-digest-chain.md`. Its body is byte-identical to this document from
§Context onward and its header records what was authored and what landed. §12's two are
authored: D-143 and D-144. **This document remains the plan and stays `DRAFT`.**

---

## Context

**This is the first phase that spends money, and `BUILD_PLAN.md` says that is phase 6.**
D-27 routes two candidates a night to the secondary provider regardless of whether the
primary is healthy, so the first ordinary night phase 5 runs makes a paid call. Phase 6's
carried obligation, that the cost ledger must be recording per model before the first full
night rather than after, binds here. §4's D-140 and §6's 5.14 are where that lands, and it
is the largest single thing this plan proposes that the phase's two-line scope does not
already say.

**The phase's inputs do not exist.** `headline` holds zero rows and no component writes
it: C29 HeadlineIngestor is catalogued in `ARCHITECTURE.html` §03 and is in no phase's
scope in `BUILD_PLAN.md`. C33 NewsDigester reads `headline`, so without C29 the digest
step has nothing to condense. Phase 5 builds both or it builds neither.

**And `headline` has no column that can hold an article.** Its columns are `ticker`,
`date`, `published_at`, `title`, `source` and `url`. §07's whole argument for a local model
is that full articles run five to eight hundred tokens each and three per candidate sent
straight to Opus would cost around $64 a year, which is an argument about article text
rather than about titles. A title is roughly fifteen tokens and condensing three of them to
150 is not a condensation. Either the table gains the text or the design's own cost
reasoning does not apply to what gets built. §5 measures the text and §4's D-131 states the
column.

**The chain is the one place in this system where a failure produces no error and no
output.** §18 gives it two rows: the primary unreachable falls through and is recorded, and
no link healthy halts before any researcher call. Both are silent by construction. A run
that fell through and was not recorded looks exactly like a run that did not, and that is
what `news_digest.provider` and `model_name` being NOT NULL exist to prevent [D-29,
INVARIANT 7's second boundary].

**Nothing here is a new table.** `headline`, `news_digest`, `local_model_config` and
`cost_ledger` were all created by `0001_snapshot.sql` and all four hold zero rows. What
phase 5 adds to the schema is columns on one table, a grain rule on that same table, and a
vocabulary. All of them are free now and expensive later.

**The size of the work is one migration, three components, one client, one panel, a config
namespace, one measured sweep, and one real night.** No range pass, no backfill, and no
row that cannot be recomputed. §3 says why.

---

## 1. What exists, and what phase 5 has to reach around

| Component | What is stored today | What is missing, and where it went |
|---|---|---|
| **C29 HeadlineIngestor** | `headline` at an identity key with `ticker`, `date`, `published_at`, `title`, `source` and `url`, one index on `(ticker, date)`, zero rows. The `news` endpoint answered 200 at 1.9 with the shape `date,title,content,link` over 10 rows at `limit=10` | **The component, and the column the digest reads.** No class named `HeadlineIngestor` exists, no phase in `BUILD_PLAN.md` scopes one, and the table has nowhere to put `content`. Also missing: a grain. `headline` is an event record with no unique index, which is the carried obligation 1 owed to 5 |
| **C33 NewsDigester** | `news_digest` at `(ticker, date)` with `digest_text` nullable, `provider` and `model_name` NOT NULL, and `was_rotation` defaulting false. `prompts/digest-instruction.md` is written, versioned and read by nothing | **Everything that calls a model.** No provider interface, no chain, no health check, no rotation, no retry rule. Also missing: what a night with no articles stores, which the NOT NULL on `provider` forces a decision about before the first insert |
| **C32 LocalModelClient** | `local_model_config` at `provider_order` with `endpoint`, `enabled`, `last_health_check` and `last_loaded_model`, zero rows, and `SCHEMA.md` naming the UI as its only writer [D-51] | **The rows, and a writer for two of the columns.** The UI is phase 9, so nothing can seed the chain and nothing may stamp a health check. `seed.ps1`'s header already says the digest provider chain from D-25 is "still to come, in the phases that build them" |
| **The secondary link** | `appsettings.Secrets.example.json` carries an `Anthropic:ApiKey` slot. Nothing reads it | **A component number.** §03 catalogues C32 for the local link and catalogues nothing for the secondary, so a registered secondary client would fail `RegistryNameTests`, which asserts every registered component is named in §03 |
| **C26 CostLedger** | `cost_ledger` at an identity key with `date`, `model_id`, `portfolio_id` nullable, four token columns, `cost` numeric and `was_batch`, zero rows. `cost.annual_budget` and `monitor.cache_hit_rate_min` are documented and unseeded | **The component.** It is `BUILD_PLAN.md` phase 6's carried obligation and the rotation makes it phase 5's. §4's D-140 |

**Five things phase 5 reuses rather than rebuilds, named because the alternative to each is
a second implementation.**

`EodhdClient` with `EodhdRateLimiter` and `EodhdUrl` is the shape a provider client takes
here, and C29's news call goes through it rather than beside it. One client, one sliding
window, for the reason `PipelineComposition`'s second overload already states: two
independent windows of 1,000 against one limit of 1,000 meet the limit as a 429 in the
middle of a sweep.

`DeclaredAccess` throws `UndeclaredTableAccessException` before a connection opens when a
component touches a table outside its declared set, and `IReadOwner` behind an empty
`WriteSet` is what D-109 built for C36. That is exactly C32's shape: a component that reads
`local_model_config`, writes nothing, and is not a stage.

`StageResult.ZeroRowsExpected` is how a stage says it wrote nothing and that this is
correct, rather than tripping the zero-row halt. C14 already uses it for the warm-up night.
C29 and C33 both need it for the night no screen surfaces a name.

`StageResult.Alert` and the run log are how a component that may not write `alert` reports
something worth attention. C07 does it for its abort and `AlertTypes`' comment says in
terms that a phase giving one of §18's other conditions a writer adds the name and extends
migration `0021` in the same checkpoint. Phase 5 does not need to: falling through is a
recorded fact on every digest row rather than an alert.

`ConfigSeeder` with its fixed `SeedInstant` is how a key arrives without moving any prior
date's config version [D-72]. `ConfigResolutionTests` asserts `Keys.Count = 95`, and that
number moves in 5.3 deliberately.

**Four facts a phase 5 reader will meet and should not read as broken.**

`attribution` holds 74,767 frozen rows and every one of them has `digest_provider` null.
They cannot gain one: INVARIANT 4 forbids rewriting the table, and C14 inserts the row at
18:30 while the digest is produced at 18:33. §4's D-135.

`gate_result` carries 3,812,120 rows over the range and C12's earnings blackout never fired
on any of them, because 3.10 deliberately loaded no earnings. That is uniform across
survivors and delisted names alike and is not a survivorship defect [D-96 discussion,
`PROGRESS.md` 3.10].

`local_model_config` being empty is not a missing migration. The table exists and its only
declared writer is a UI that does not exist yet.

The evening order today ends at `ConcentrationMonitor`. C29 at 18:32 and C33 at 18:33 both
belong between `CandidateAllocator` at 18:30 and `ConcentrationMonitor` at 19:00, which is
what 5.12 inserts and what §4's D-139 has a consequence about.

---

## 2. Three rules, before the first checkpoint

**The digest step transforms evidence and never judges** [INVARIANT 7, D-28]. The
enforcement point is `prompts/digest-instruction.md` and it is already written. The rule
that follows for the build is narrower than the invariant and is the one that gets broken
by accident: **nothing in the digest path may read a score, a rank, a screen id or a
candidate's provenance, and nothing may drop a candidate.** C33's declared read set is what
holds that, so it names `candidate_set` for the list of names and `headline` for the
articles, and it does not name `screen_score_daily` or `attribution`. A digester that
knows which screen surfaced a name can be steered by it, and the failure is a plausible
summary rather than an error.

**Absent is not empty, and this table has four states rather than two** [`CLAUDE.md` §6].
No row at all, a row whose digest is null because there was nothing to read, a row whose
digest is the literal `NO MATERIAL NEWS` because a model read the articles and found
nothing, and a row carrying prose. D-60's no-digest disqualifier bites on the second and
not on the third, and `s5.no_digest_disqualifier_min_articles_90d` is the key that says
when. Collapsing any two of the four is a silent correctness bug in phase 6's rubric rather
than in phase 5's code, which is why it is settled here. §4's D-134.

**The two links receive byte-identical instruction text and the difference between them is
the model** [`prompts/digest-instruction.md`, "Identical text for every provider"]. The
rotation exists to build a paired sample and a difference in prompt would confound it
[D-27]. So the instruction is read from the file at execution time, once, and handed to
whichever link answers. A per-provider template, a per-provider token cap, or a system
prompt on one side and not the other all break the comparison the rotation is for, and none
of them fails loudly.

---

## 3. The scope question: what a night costs, and what phase 5 does not run

**Phase 5 runs no range pass and backfills nothing, and that is a decision rather than an
omission.** It is put here because a reader coming from phase 4, which froze 74,767 rows
over 1,457 sessions, will ask why this phase does not do the same.

Three reasons, in the order they bind.

**A backfilled digest is not point-in-time.** The `news` endpoint returns articles by
publication date, so the article set for a past night is recoverable, but the digest is
model output produced today by whatever model is loaded today. Storing it against a 2022
date puts a 2026 summariser's reading of 2022 news into a row indistinguishable from a
live one, and `news_digest` has no column that could say otherwise. D-29 keeps provider and
model separable within a night, not across four years of model releases.

**Nothing downstream would read it.** The digest's only consumers are C15's dossier and the
researcher, both phase 6, and neither runs over history. `attribution`'s frozen rows carry
no digest and cannot gain one.

**And it would cost real money and real units.** 34,932 historical candidates at three
articles each is 104,796 news calls, and at the local model's throughput it is days of GPU
time producing output nothing reads.

**What a live night costs, derived from the architecture's own two figures rather than
guessed.** §07 states the rotation at about $1.30 a year and a full year run entirely on
the secondary at roughly $18. Haiku 4.5 is $1.00 per million input tokens and $5.00 per
million output. At 28 candidates and 252 sessions the output side is 28 x 252 x 150 tokens,
which is 1,058,400 tokens and $5.29, leaving $12.71 of the $18 for input. That is 12.71
million input tokens over 7,056 candidate-nights, or **about 1,800 input tokens per
candidate**. §07 puts an article at five to eight hundred tokens, so 1,800 is **three
articles** plus the instruction, and the rotation at two of twenty-eight candidates is 2/28
of $18, which is $1.29 against the stated $1.30.

**So `digest.max_articles` is 3 and it is read off the design rather than chosen**, and it
agrees with §07's screen blocks, which give S3 and S5 three headlines each. The arithmetic
is an estimate until 5.1 measures an article, which is the one number in it that comes from
outside this corpus. If articles measure materially longer than 800 tokens, the cap rather
than the budget is what moves, and §5 says so before anything is built on it.

**What the local link costs is nothing at the margin and is not nothing in wall clock.**
Twenty-eight sequential calls at roughly 1,800 tokens in and 150 out is the one part of the
evening whose duration depends on hardware this corpus has never measured. 5.1 measures it,
because the window between 18:33 and 18:35 in `RUNBOOK.md` is two minutes wide and a chain
that takes longer than that is a scheduling fact rather than a failure.

**One thing does become immutable and it is worth naming.** From the night 5.12 runs,
`news_digest` accumulates rows no later pass may recompute, for a reason adjacent to
`attribution`'s: the model that wrote each row is recorded, and re-running would replace a
recorded model's output with a different model's. `RUNBOOK.md`'s recovery section already
says re-running anything that spends money is a decision rather than a safe operation, and
the digest is the first stage that clause reaches. **It is not INVARIANT 4 and must not be
written as though it were**: that prohibition is absolute and this one is a judgement about
what a re-run destroys.

---

## 4. Decisions that need authoring before code

Twelve. Each is authored content under `CLAUDE.md` §13 and none is the build session's to
write. Clauses are in `CLAUDE.md` §15's amendment format so they paste into a build prompt,
and each names the checkpoints it blocks. Numbering continues from D-130, the last decision
in the register.

**Two of the twelve close carried obligations rather than opening new ground.** D-141
closes the `events` point-in-time item `BUILD_PLAN.md` has carried from phase 1 to phase 5,
and D-142 answers the question phase 3 handed here about whether `earnings_history` can
serve C12's earnings blackout. Neither is a digest decision and both are owed by this phase
by name.

### D-131, `headline` gains the article text

Blocks 5.2 and 5.4.

```
D-131 headline carries the article body, and the digest reads it rather than
the title. ACTIVE

ARCHITECTURE.html section 07 prices local enrichment against articles of five
to eight hundred tokens and headline has no column that can hold one. Its
columns are ticker, date, published_at, title, source and url. A digest built
from titles alone is a summary of about forty-five tokens of input, which is
not the condensation section 07 argues for, and it would leave the qualitative
gap that section identifies unaddressed while every output looked normal.

headline gains content, nullable text, and published_at is the column the
lookback window is read against. Content is nullable because the endpoint may
send an article without one, and null there means the provider sent no body
rather than that the body was empty.

Test: a headline row with a null content is excluded from the digest input and
counted, and the count reaches the run log. A candidate whose every article has
a null body produces the no-article outcome of D-134 rather than a digest over
nothing.

Done when: migration 0022 adds the column, SCHEMA.md's headline section states
it in the same checkpoint, and 5.1's measurement of body length is recorded in
PROGRESS.md against the five-to-eight-hundred-token estimate section 07 uses.
```

### D-132, `headline`'s grain is a run scope rather than a row identity

Blocks 5.2 and 5.4.

```
D-132 headline is idempotent by run scope: C29 deletes the ticker and date it
is about to write and reinserts. ACTIVE

This is the carried obligation BUILD_PLAN.md has held from phase 1, that
headline is an event record and the snapshot grain the other layer-3 stores
have does not apply. Two articles about one company on one day are not a
duplicate to be collapsed; they are two articles, and they may share a title, a
source and a publication timestamp while differing in body. So a unique index
on the row's own attributes is the wrong instrument, and reaching for one is
how this starts badly.

C14 already establishes the pattern and SCHEMA.md states it: the allocator
deletes the date before it rebuilds it, both operations are its own, and
INVARIANT 10 read per operation is untouched. C29 declares Insert and Delete on
headline and nothing else writes there.

The scope is ticker and date rather than date alone, because C29's unit of work
is a candidate and a partial night must not delete the candidates it did not
reach.

Test: two runs of C29 over one date produce byte-identical headline contents,
asserted as a set comparison in both directions rather than on a row count. A
seeded pair of articles identical on every attribute but the body survives as
two rows.

Done when: WriteOwnershipConformanceTests sees C29 claiming headline.Insert and
headline.Delete with no other component on either, and SCHEMA.md's headline
section states the run scope and the reason.
```

### D-133, the digest's input window and cap

Blocks 5.3 and 5.8.

```
D-133 The digester reads at most digest.max_articles articles published within
digest.lookback_days of the date being processed, most recent first. ACTIVE

Neither figure exists anywhere in this corpus and both are needed before the
first call. digest.max_articles is 3 and digest.lookback_days is 7.

Three is read off ARCHITECTURE.html section 07's own cost figures rather than
chosen. The rotation at about $1.30 a year and a full secondary year at roughly
$18, against Haiku 4.5 at $1.00 per million input and $5.00 per million output,
28 candidates and 252 sessions, leave about 1,800 input tokens per candidate,
which at section 07's five to eight hundred tokens an article is three. It also
agrees with the S3 and S5 screen blocks, which give three headlines each.

Seven days is the window the digest's recency is about. Section 07 asks for what
happened recently and the apparent cause of a sharp move, and
s5.news_gate_min_articles already operates at three articles in seven days, so
the two thresholds read the same window. D-60's ninety days is a different
measurement, of whether a name is covered at all, and is not this.

digest.lookback_days and s5.news_gate_min_articles read the same seven days
and are separate keys deliberately. One is what the digest summarises and the
other is when S5's two news conditions fail open, and they agree today because
seven days is the right window for both questions rather than because they are
one question. Harmonising them into a single key, which is the tidy-looking
edit a later session will reach for, makes the gate and the digest no longer
independently movable, and that coupling would be inherited rather than
chosen.

Most recent first, ties broken on the source string ordinally, so the selection
is deterministic when a name carries more than three articles in the window.
Every one of the seven names phase P probed except two carried more than three
in ninety days and NVDA.US carried 1,000, so the tie-break is reached in
practice rather than in principle.

Test: a candidate with five articles inside the window and two outside sends
exactly three, and they are the three most recent. Two articles sharing a
published_at select on the source string, asserted rather than observed.

Done when: both keys are in CONFIG_REFERENCE.md with a verified consumer, both
are seeded at 5.3, and 5.1's measured article-length distribution is recorded
beside the 1,800-token derivation.
```

### D-134, the four digest outcomes

Blocks 5.2 and 5.8.

```
D-134 news_digest distinguishes four outcomes and a null digest_text means no
article was available to read. ACTIVE

The column is nullable and provider and model_name are NOT NULL [D-29], so a
row exists only where a link answered or was asked. That leaves four states and
they must stay separable, because D-60's no-digest disqualifier bites on one of
them and not on its neighbour:

  no row             the ticker was not a candidate that night, or the run
                     halted before the digest step
  digest_text null   a link was selected and there was nothing to send: no
                     article inside the window, or every article carrying a
                     null body [D-131]. This is no digest available in D-60's
                     sense
  NO MATERIAL NEWS   a link read the articles and returned the escape hatch
                     prompts/digest-instruction.md defines. This is a reading
                     rather than an absence and D-60 does not bite on it
  prose              an ordinary digest

The middle two are the pair that gets collapsed, and collapsing them makes S5's
disqualifier fire on thinly covered small caps, which is the population the
design exists to reach and the asymmetry D-60 was written to prevent.

news_digest gains a CHECK asserting model_name is non-empty rather than merely
non-null, because an empty string satisfies NOT NULL and records nothing. The
provider vocabulary is closed by a CHECK to the chain's link names, which is
GateReasons' and AlertTypes' shape and is deliberately the same shape.

Test: four fixtures, one per state, and a reader that separates them. The
no-article case and the NO MATERIAL NEWS case are asserted to differ, which is
the assertion a collapse would fail.

Done when: migration 0022 carries both CHECKs, SCHEMA.md's news_digest section
states the four states, and a Core vocabulary type holds the provider names in
one place with a test that the database holds the same list.
```

### D-135, `attribution.digest_provider` is written null and has no writer

Blocks 5.2.

```
D-135 attribution.digest_provider stays null. news_digest.provider is the
record and no third component writes attribution. ACTIVE

The column exists in 0001 and nothing can fill it. C14 inserts the attribution
row at 18:30 and the digest is produced at 18:33, so the provider is not known
when the row is written; C21 owns the update and only of the nine return
columns; and SCHEMA.md says in terms that no third component writes here at
all, which is a stronger claim than the exception it replaced. Adding C33 as a
second claimant of attribution.Update would also collide with C21 on the
table-and-operation pair WriteOwnershipConformanceTests asserts over.

Nothing is lost. news_digest is at ticker by day, attribution is at ticker by
day, and the provider sits on the first at the same grain, so VALIDITY.md
section 5's mitigation, that digest_provider is recorded and the rotation gives
a paired sample, is met by the join rather than by the copy.

This is D-124's disposition of slot_filled applied to a second column for the
same reason: null says unknown truthfully, and a value stamped on a row no
later pass may rewrite is worse than an absence.

The 74,767 frozen rows are unaffected and could not be otherwise [INVARIANT 4].

Test: a reader test asserting no registered component declares
attribution.Update other than ForwardReturnFiller, built the way 4.2's view
reader test was so it does not pass vacuously.

Done when: SCHEMA.md's attribution section states the column is written null
and why, in the same checkpoint as 0022.
```

### D-136, what a link is, and who seeds the chain

Blocks 5.3, 5.5, 5.6 and 5.7.

```
D-136 A link is an implementation of one interface, the order comes from
local_model_config, and seed.ps1 seeds both rows. ACTIVE

ARCHITECTURE.html section 07 asks for a chain rather than a primary with a
fallback branch, so that failover works in both directions with no
rarely-executed code path. In code that means one interface with a health
check and a digest call, a list built from local_model_config ordered by
provider_order and filtered on enabled, and a caller that takes the first
healthy link. Nothing in the caller knows which link is preferred.

local_model_config is seeded rather than written by a stage. SCHEMA.md names
the UI as its writer [D-51] and the UI is phase 9, so until then the two rows
arrive through ConfigSeeder alongside the config keys, which is what seed.ps1's
header already anticipates: the portfolio registry from D-36 and the digest
provider chain from D-25 are still to come, in the phases that build them. The
insert is ON CONFLICT DO NOTHING on provider_order, so a second run changes
nothing, and this adds no writer to the registry: SCHEMA.md already carries the
precedent that a table's writer may be configuration rather than a stage.

last_health_check and last_loaded_model stay null and gain no writer in this
phase. C32's Writes cell in section 03 says nothing, via C27, and a health
check is emitted through the run log exactly as C07's abort is. Phase 9
inherits the question of whether the UI fills them, and the run health screen's
last successful digest run reads run_log until it does.

Test: a chain whose first link is unhealthy answers on the second, and a chain
whose first link is unhealthy and is then made healthy answers on the first
again within one run, with no code path that names either link.

Done when: local_model_config carries two rows after seed.ps1, the chain is
built from them and from digest.chain rather than from a literal, and
ConfigReferenceMd's Consumer column for digest.chain names the composition
site that was actually read.
```

### D-137, retry once, then fall through

Blocks 5.7.

```
D-137 A malformed or over-long response is retried once on the same link and
then falls through to the next. Nothing is truncated. ACTIVE

Section 18 states the rule and no document says what malformed or over-long
mean, so both are stated here and neither is left to a call site.

Over-long is a response whose token count exceeds digest.max_tokens, which is
150. The cap is requested on the call and asserted on the response, because a
provider that ignores max_tokens returns a long answer with a normal status.

Malformed is an empty response, a response that is only whitespace, or a
transport failure. It is deliberately not a content judgement: a digest that
reads oddly is not malformed, and a step that decided which summaries were good
enough would be judging, which INVARIANT 7 forbids.

Never truncate. A half-sentence digest is worse than none, because the
researcher receives it as a complete fact and the validator cannot check prose.
A link that fails twice is treated as unhealthy for the remainder of that run,
so a dead primary costs two calls rather than fifty-six.

The retry count is not a config key. Section 18 states one retry as behaviour
rather than as a tuning parameter, and a key here would invite raising it,
which trades a visible fall-through for an invisible delay.

Test: a link returning an empty body twice falls through and the digest records
the second link; a link returning 200 tokens against a cap of 150 is retried
and then falls through, and no row anywhere holds a truncated digest.

Done when: the fall-through is visible in the record, meaning news_digest
carries the second link's provider and model_name and the run log carries a
line naming the first link and the reason.
```

### D-138, the rotation's selection is a hash rather than a Random

Blocks 5.10.

```
D-138 The two candidates routed to the secondary are chosen by a stable hash of
the run date and the ticker, not by System.Random. ACTIVE

D-27 says chosen by the date seed and CLAUDE.md section 6 says Random is seeded
from the run date, so a seeded Random is the reading that first suggests itself.
It is rejected here for one reason: the sequence a seeded System.Random produces
is a runtime implementation detail, and a framework upgrade that changed it
would silently change which pair the rotation compared, splitting the paired
sample D-27 exists to build without anything saying so. That is the exact class
of failure CLAUDE.md section 1 is about.

The rule instead: order the night's candidates by an FNV-1a hash of the
invariant yyyy-MM-dd date string concatenated with the ticker, ties broken on
the ticker ordinally, and take the first digest.rotation_count. The hash is
written in this repository, so it cannot move underneath the record.

The rotation runs regardless of primary health [D-27], including on a night the
primary is down, in which case the rotation pair is indistinguishable from the
fall-through except that was_rotation is true on those two rows. That is what
was_rotation is for.

If the candidate set is smaller than digest.rotation_count the whole set
rotates and nothing is padded.

Test: the same date produces the same pair across runs and across processes; two
adjacent dates over an identical candidate set produce different pairs; and
was_rotation is true on exactly digest.rotation_count rows on a healthy night.

Done when: guards.ps1's new Random check still passes with no exclusion added,
because nothing constructs one.
```

### D-139, what the gate halts

Blocks 5.11 and 5.12.

```
D-139 No healthy link halts the run at C33. Every stage after it does not run,
and C28 ConcentrationMonitor is moved ahead of C29 in the evening order so the
diversity guarantees are still measured on a halted night. ACTIVE

INVARIANT 15 and section 18 both state the halt in terms of the researcher and
of orders, neither of which exists in phase 5. The observable here is narrower
and must be stated rather than assumed: the run halts at C33, NightlyRun
returns Completed false, and no stage after C33 executes.

That has a consequence the documents do not address. C28 runs at 19:00, after
C33 at 18:33, and it is what raises the megacap-share and distinct-ticker
alerts. On a halted night the candidate set exists and is exactly as
concentrated as it is, and losing its alert on the nights something else is
already wrong is the wrong direction. C28's 19:00 in section 04 is a clock time
rather than a dependency: it reads candidate_set, position and security_daily
and nothing the digest step writes.

So the evening order becomes C14, C28, C29, C33, and section 04's schedule is
amended in the same checkpoint. The alternative, leaving C28 after C33 and
accepting that a halted night raises no concentration alert, is defensible and
is not taken, because the monitor exists so the guarantees are measured rather
than assumed and a night that halts is not a night they stopped mattering.

Test: a chain with both links unhealthy halts, produces no news_digest row, and
still produces the C28 alerts a healthy night over the same candidate set
produces. Registered as the FIXTURES.md forward item, a night where no digest
provider is healthy and the run halts.

Done when: NightlyRun.EveningOrder carries the new order, RUNBOOK.md's nightly
cycle table carries it, and ARCHITECTURE.html section 04's timing is amended by
a human in the same checkpoint.
```

### D-140, the secondary link, and the cost ledger it forces

Blocks 5.6 and 5.14.

```
D-140 The secondary link is Haiku 4.5 at model id claude-haiku-4-5 through the
official Anthropic SDK, and C26 CostLedger is built in phase 5 rather than
phase 6. ACTIVE

The model id is a config key rather than a literal, digest.secondary_model_id,
so that a model change is a config version and a splittable history rather than
a code edit [CLAUDE.md section 12].

The SDK rather than raw HttpClient. EodhdClient is hand-rolled because that
provider has no SDK and because the rate limiter and the unit allowance are
this system's own; neither applies here. The Anthropic package is added to
Directory.Packages.props, which is the one place a version is declared.

C26 moves. BUILD_PLAN.md phase 6 carries the obligation that the cost ledger
must be recording per model before the first full night, not after, and phase 6
is where it sits because phase 6 was believed to be the first phase that spends
money. D-27 makes phase 5 the first: two candidates a night go to the secondary
regardless of primary health, so the first ordinary night this phase runs makes
a paid call. The obligation's condition is met one phase early and the
obligation moves with it rather than the money going unrecorded for a phase.

What C26 records here is one row per digest call that reached the secondary:
date, model_id, portfolio_id null, the four token counts from the response
usage, cost computed from a config-held price, and was_batch false. cost is
numeric [INVARIANT 16] and the price keys are decimal.

It is deliberately not the whole of C26. The cache hit rate, the annual budget
alert and the validator rejection counts are phase 6 and phase 10 concerns and
none of them has an input yet. What phase 5 builds is the writer and the row.

Test: a rotation night writes exactly digest.rotation_count cost_ledger rows,
the token counts match the response, and the sum against a hand-computed figure
reproduces. A local-only night writes none.

Done when: the annual forecast from one real night's rotation is recorded in
PROGRESS.md beside section 07's $1.30, as the first figure in this corpus that
moves from estimated to measured on a model call.
```

### D-141, the `events` point-in-time question, closing 1 owed to 5

Blocks nothing in code. Closes a carried obligation.

```
D-141 A backfilled events row is not point-in-time for an earnings date, no
first-seen column is added, and the blackout stays inert over history. ACTIVE

BUILD_PLAN.md has carried this from phase 1: calendar/earnings sends no date on
which a schedule became public, announced_date is populated for dividends and
null for earnings and splits, and the table has no first-seen column, so a
backfilled row and a live-accumulated one are indistinguishable.

Three things settle it and none of them is new work.

3.10 already loaded no earnings, deliberately, so events carries no earnings for
any backfilled date. There is nothing to separate.

Live accumulation is point-in-time correct by construction, because a row
appears the night the calendar first lists it. That property is a fact about how
the rows arrive rather than something the schema records, and adding a
first_seen column would record it going forward while saying nothing about the
rows already there.

And the exposure is bounded rather than open: a schedule is usually published
two to four weeks ahead, gates.earnings_blackout_days_before is 5, and a
blackout narrower than the publication lead behaves the same either way.

So: no column, no backfill, and C12's earnings blackout is inert over the whole
frozen window and live from the first night it accumulates. The consequence is
stated rather than left to be found: the 74,767 frozen attribution rows were
selected under a gate with one of its five conditions never firing, uniformly
across survivors and delisted names alike, and any analysis that compares
frozen-window selection against live selection is comparing four gates against
five.

Done when: the carried obligations table marks the row closed by this decision,
and PROGRESS.md carries the four-gates-against-five consequence where a reader
of the frozen record will meet it.
```

### D-142, whether `earnings_history` can serve the blackout

Blocks nothing in code. Closes a question phase 3 handed here.

```
D-142 earnings_history cannot serve C12's earnings blackout, and the reason is
structural rather than a gap in the data. ACTIVE

Phase 3 left this open in terms: whether earnings_history, which C03 populates
over the widened pool [D-96], can serve the gate events cannot.

It cannot. The blackout needs the next earnings date, which is forward-looking,
and every read of earnings_history is keyed on report_date <= date [D-96,
INVARIANT 12]. That rule is what makes the table honest and it is precisely the
rule that removes every forward row. Lifting it to reach a scheduled future
report would read a date out of a payload fetched today, with nothing recording
when it became public, which is the same lookahead D-141 declines for events
arriving through a second table.

So the two decisions are one finding seen twice. A forward earnings date is
point-in-time only where the arrival of the row is itself the evidence of
publication, and that is true of live accumulation and of nothing else this
system has.

earnings_history keeps its purpose, which is D-96's: last_two_earnings_surprises
and whatever a drift screen would need if D-90 ever registers one. Neither is a
forward date.

Done when: the carried obligation from phase 3 is marked closed by this
decision and D-90's third option, widen the backward window and backfill
earnings history first, carries a pointer here saying that route does not fix
announced_date.
```

---

## 5. The measurement 5.1 takes, and what it settles

**Phases 1 and 3 both opened on an endpoint sweep and both times it changed what the phase
built.** 1.9 found that reading a date twice inside one run cannot detect accretion, which
contradicted the build plan and closed at D-70. 3.1 found that form4 counts more rows than
it sends. Phase 5 reaches two providers this corpus has never called and one endpoint it has
called once, so it opens the same way.

**Nothing in 5.1 writes to the store and nothing in it is a stage.** It runs from a scratch
file-based app outside the repository, as phase P's probe and both sweeps did, and its
transcript goes to `docs/evidence/phase-5/` with the token never printed. The scratch app is
deleted at 5.2, as phase P's was at 0.1, and the transcript is what survives.

**Six things it measures, and what each one settles.**

| # | Measured | What it settles | What a surprise would change |
|---|---|---|---|
| 1 | The `news` payload's full field set for one small cap and one megacap, and whether `content` carries a body, a lead paragraph or a truncation | D-131's column, and whether the design's own cost argument applies to what gets built | A `content` that is a 200-character teaser makes the digest a summary of teasers, and §07's five-to-eight-hundred-token figure does not describe what would be built. **This reading halts the build before 5.2**, per the stopping rule below |
| 2 | The character and token length of a body, distribution over at least 40 articles across at least 6 tickers | §3's 1,800-token derivation, so `digest.max_articles` becomes measured rather than derived | A median outside 300 to 800 tokens moves the cap rather than the budget, and the cap is then a decision rather than a seeded value. **Report and wait**, per the stopping rule below |
| 3 | The unit cost of one `news` call, read off `/api/user` before and after, and whether the call pages | Whether 28 calls a night is inside the daily allowance the nightly run already spends | A per-article rather than per-call weight changes nothing about the design and changes the allowance arithmetic in `CONFIG_REFERENCE.md` |
| 4 | The local server's OpenAI-compatible surface: `/v1/models`, one `/v1/chat/completions` at 1,800 tokens in and 150 out, the loaded model name as the server reports it, and latency over 10 calls | C32's client, `digest.health_timeout_ms` at 5,000, and whether the 18:33 to 18:35 window in `RUNBOOK.md` holds | Twenty-eight sequential calls exceeding two minutes is a `RUNBOOK.md` amendment rather than a defect, and it is better known before the order is wired than after |
| 5 | One `claude-haiku-4-5` call through the SDK with the real instruction and three real articles, and the `usage` block it returns | D-140's cost row, and the first measured figure against §07's $1.30 | A response `usage` shape different from what the ledger's four columns expect is a schema question, not a code question |
| 6 | Both links given the identical instruction over the identical three articles, side by side | Whether the two summarisers differ enough to matter, which is D-53's question asked once rather than answered | Nothing is decided from one pair. D-53 says explicitly not to act before a quarter of data exists, and this is a sanity read rather than a result |

**Row 6 is a read and not a measurement, and the distinction is the point.** D-53 is open on
whether the local model stays local once measured, and the answer comes from the rotation's
paired sample after a quarter. Looking at one pair on one night and forming a view is exactly
what `CLAUDE.md` §11 warns against, so it is recorded in the transcript and no decision cites
it.

### What stops, and what only gets recorded

**A measurement that can invalidate the phase's central argument needs a stopping rule, not an
instinct.** Rows 1 and 2 can, and "a finding for the operator rather than a thing to work
around" is the right direction and is not a rule: it says what not to do and leaves what to do
to whoever is holding the keyboard at 2 a.m.

**The pattern is 4.13's.** The measurement is taken one checkpoint before the thing it would
change is committed, and the build reports and waits rather than proceeding on the reading it
prefers. 4.13 computed the persistence measure before 4.14 froze anything, for exactly this
reason.

| Reading | What the build does |
|---|---|
| `content` carries an article body | Record the distribution and continue to 5.2 |
| **`content` carries a teaser, a lead paragraph, a truncation, or is absent on a material share of rows** | **5.2 does not run.** The build stops, records what the field actually holds with three verbatim examples and their lengths, and reports. D-131's column and the whole of 5.2's schema rest on this, and a digest over teasers is a different product from the one §07 prices. **The build does not choose a fallback**, and the three that will suggest themselves are named so they are recognised as out of scope: digesting titles, fetching the article from `link`, and asking the model to work with less |
| Median body between **300 and 800 tokens** | `digest.max_articles` is seeded at 3 and the measurement is recorded beside §3's derivation, which is what makes 3 measured rather than derived |
| **Median body above 800 tokens** | **Report and wait.** Three articles then exceeds the input budget §07 priced, so the cap is a decision rather than a seeded value. It changes the evidence every digest carries and therefore splits history [`CLAUDE.md` §12], which is not a build session's call |
| **Median body below 300 tokens** | **Report and wait**, and this direction is the one that gets waved through. Three short articles fit the budget with room, so nothing fails and the cap could rise. Raising it is still a design change about how much evidence a digest carries, and a build session choosing 5 because 5 fits is the same act as choosing 2 because 2 is cheaper |

**Rows 3 to 6 gate nothing and two of them owe something before a later checkpoint.** If 28
sequential local calls exceed the 18:33 to 18:35 window, that is a `RUNBOOK.md` amendment owed
before 5.12 rather than a defect, and it is better known before the order is wired than after.
If the secondary's `usage` block does not fit `cost_ledger`'s four token columns, that is a
schema question owed before 5.14. Neither stops 5.2.

**Nothing here is a licence to stop on anything else.** A measurement that merely disagrees
with an estimate is recorded and the build continues, which is what `CLAUDE.md` §3 asks for
and is what 5.1's other four rows are.

---

## 6. Checkpoints

Fourteen, one commit each, `Phase 5 / 5.n - what it did`. Run `ci.ps1` per checkpoint, which
is the per-checkpoint verification since 1.12 and the only thing that cannot reach the test
step off a stale binary.

**The order follows five facts, in decreasing force.**

**The phase's inputs have never been read.** Two providers and one endpoint, none of them
called by this system, and the design's central cost argument rests on a number none of them
has yet produced. So 5.1 is a sweep and nothing is built against an assumed payload.

**Schema is free exactly once.** `headline` and `news_digest` both hold zero rows today. All
of D-131's column, D-132's grain and D-134's two CHECKs land in one migration at 5.2, with
`SCHEMA.md` in the same checkpoint, the parity tests running in both directions.

**A link must be exercisable alone before a chain can mean anything.** 5.5 and 5.6 each build
one link and each is proved against its real provider on its own, so 5.7's chain is composing
two known-good things rather than debugging two unknowns through a selector. This is why the
chain is a checkpoint of its own and not part of either link.

**The gate is built before the order is wired, not after.** 5.11 proves the halt against a
chain with both links down while the pipeline still ends at C28; 5.12 then inserts C29 and C33
into the evening order and runs one real night. Wiring first would mean the first exercise of
the halt is a live night, which is the one place it cannot be observed cleanly.

**And the phase has one checkpoint whose output a person can open, at 5.9.** Phase 3.5 exists
because three weeks of phase 3 produced complete records and nothing anyone could open, and
the rule taken from it is that a phase puts its openable checkpoint early. **5.9 is not early
and cannot be**, which is stated rather than left to be noticed: the panel reads `headline`
and `news_digest`, and the second holds nothing until 5.8 writes it. The earliest honest
position is immediately after the write, and that is where it is.

**Two positions were considered and both are worse.** Folding it into 5.8 makes it the last
work in a checkpoint whose done-when is the write, which is what gets cut when 5.8 runs long;
it also spans two projects and two conformance surfaces, the Api and §03's C36 Reads cell,
and a commit spanning two checkpoints means the checkpoints were drawn wrong. Building an
articles-only half at 5.5 and extending it at 5.9 puts a debugging tool in reach two
checkpoints sooner, which is genuinely tempting while 5.7 and 5.8 are being built, and it is
rejected because a panel that shows the articles and not what was made of them is not
openable, it is a query with a URL. **If the operator prefers the earlier half, that is a
scope call and not a build one**, and it costs one extra checkpoint and one extra amendment
to the same two documents.

**What 5.9 must not be is 5.8's verification step.** The draft of this plan had it there,
described as reading one candidate's digest whole beside the three articles it was built
from. That is a panel, and left in the verification list it happens once and leaves nothing
behind, so the first digest that reads oddly is the one nobody can inspect without writing a
query. §11 now names the panel rather than the manual read.

| # | Scope |
|---|---|
| 5.1 | The sweep, before any component and before any schema. §5's six measurements, from a scratch app outside the repository, transcript to `docs/evidence/phase-5/`, token never printed. **The article-length distribution is recorded against §07's five-to-eight-hundred-token estimate**, which is what makes `digest.max_articles` measured rather than derived. Nothing is committed under `src/` |
| 5.2 | All schema, before any component and before a single row. Migration `0022`: `headline.content`, `news_digest`'s non-empty `model_name` CHECK and its closed `provider` vocabulary, and `attribution.digest_provider`'s disposition stated. `SCHEMA.md` in this same checkpoint, both parity directions green, `ExpectedMonetary` asserted unchanged. The 5.1 scratch app is deleted here. Blocked on D-131, D-132, D-134 and D-135 |
| 5.3 | Config: the `digest.*` namespace seeded including the two keys D-133 adds and D-140's model id, `s5.no_digest_disqualifier_min_articles_90d` seeded at last, and `local_model_config`'s two rows through `ConfigSeeder`. `ConfigSeeder.Keys.Count` moves from 95 deliberately and the test moves with it. Every key resolved at 2021-01-04 and at the frontier. Blocked on D-133, D-136 and D-140 |
| 5.4 | C29 HeadlineIngestor. Reads `candidate_set` and the news endpoint through `EodhdClient`, writes `headline` under D-132's run scope. **A night with no candidates writes no rows and that is correct**, asserted through `ZeroRowsExpected` rather than tripping the zero-row halt, which is C14's warm-up case one stage later. Blocked on D-131 and D-132 |
| 5.5 | C32 LocalModelClient. `IReadOwner` over `local_model_config` behind an empty write set, C36's shape [D-109], so any write throws before a connection opens. Health, loaded model name and latency, emitted through the run log. **Proved against the real local server**, and against it stopped. Blocked on D-136 |
| 5.6 | The secondary link, through the Anthropic SDK at `claude-haiku-4-5`. Same interface, same instruction text read from `prompts/digest-instruction.md`. **It reads no store and is therefore not a registry component**, which is where §8's B4 lands. Blocked on D-136 and D-140 |
| 5.7 | The chain. Ordered from `local_model_config` and `digest.chain`, first healthy answers, D-137's retry and fall-through, and **no code path that names either link**. A link failing twice is unhealthy for the rest of the run. Blocked on D-136 and D-137 |
| 5.8 | C33 NewsDigester and the write. D-133's selection, D-134's four outcomes, provider and model name on every row. **Its declared read set names `candidate_set` and `headline` and nothing about a score**, which is §2's first rule made structural rather than remembered. Blocked on D-133 and D-134 |
| 5.9 | **C36 gains a fifth panel, the digest panel**, and it is this phase's openable checkpoint. For one ticker on one date: the articles selected with their publication dates and bodies, the pool they were selected from, the instruction file's version, the digest text, the provider and model that produced it, and which of D-134's four outcomes the row is. It reads `headline` and `news_digest`, declares both, computes nothing, and where a figure would have to be derived it shows the inputs and says so [D-109, 3.5's two rules]. `ARCHITECTURE.html` §03's C36 Reads cell and §20's U-screen list both gain the two tables, by a human, in this same checkpoint. Blocked on D-131, D-133 and D-134 |
| 5.10 | The rotation. D-138's hash, `was_rotation` on exactly `digest.rotation_count` rows, present on a night the primary is healthy and on a night it is not. **Two adjacent dates over one candidate set produce different pairs**, which is what a constant selection would fail. Blocked on D-138 |
| 5.11 | The gate. Both links unhealthy halts at C33 with no digest row and no stage after it running, and C28 moved ahead of C29 so the concentration alerts still fire. Both `FIXTURES.md` forward items registered here. Blocked on D-139 |
| 5.12 | The evening order wired, C29 at 18:32 and C33 at 18:33, and one real night run end to end. **The observable is a chain of counts**: N candidates, N headline sets, N digest rows, `digest.rotation_count` of them marked rotation, and one of each of D-134's outcomes if the night supplies one. `RUNBOOK.md`'s cycle table amended with the order. Blocked on D-139 |
| 5.13 | The local model precondition, run by the Worker ahead of C33 rather than on a clock [amended 2026-08-25, operator direction, replacing the 15:30 readiness check the row previously described]. **`digest.warm_timeout_ms` bounds a probe whose job is to make the model resident.** LM Studio loads on request, so a probe generous enough to cover a load causes one, and the cold load measures 41 to 52 seconds. **`digest.health_timeout_ms` stays at 5,000 and is not widened**: it bounds the chain's own health check, which asks whether the link is ready now, and two questions get two bounds. Healthy, C33 runs unchanged. **Not healthy with input redirected, C33 runs unchanged and halts** as INVARIANT 15 requires, so nothing waits in CI or in an unattended run. **Not healthy with a terminal attached, the command prints the endpoint, the model config names and the probe's own reason, then waits on a line of input and probes again**, Ctrl-C abandoning the night with nothing written. **It writes nothing and decides nothing**, and the pipeline is untouched: the probe sits ahead of the stage, so D-137's fall-through and INVARIANT 15's gate keep their meaning and a stage still either completes or fails the run. `digest.readiness_check_et` has no consumer under this shape and its retirement is a separate decision |
| 5.14 | C26 CostLedger, the digest half. One row per secondary call with the response's four token counts and a cost computed from a config-held decimal price. **The night's rotation cost annualised and recorded in `PROGRESS.md` beside §07's $1.30**, which is this corpus's first measured figure on a model call. Blocked on D-140 |

**5.14 is the checkpoint most likely to be struck, and it should be struck deliberately rather
than skipped.** If the operator prefers C26 to stay in phase 6, the consequence is that phase
5 spends money for a phase with no ledger row against it, and phase 6's carried obligation
then reads as met when the first recorded call is not the first paid one. Either answer is
defensible; only the unstated one is not.

---

## 7. Definition of done

`BUILD_PLAN.md` gives four lines. Three are evaluable as written and one is not, and that is
said here rather than discovered at sign-off, because a done-when line the phase cannot
evaluate is not a definition of done [phase 4 §3].

1. **Digests are produced for a night and recorded with provider and model name.** Evaluable.
   Measured as a count of `news_digest` rows against the night's candidate count, with the
   four D-134 outcomes tabulated rather than pooled, and `provider` and `model_name` non-null
   and non-empty on every row by construction.
2. **Stopping the local server falls through to the secondary and the fallthrough is visible
   in the record.** Evaluable. Visible means two things and both are asserted: the digest rows
   carry the second link's provider, and the run log carries a line naming the first link and
   the reason it was passed over. A fall-through recorded only in the digest rows would be
   indistinguishable from a night the chain was configured the other way round.
3. **Stopping both halts the run before any researcher call and produces no orders.** **Not
   evaluable as written.** There is no researcher and no order in phase 5, so the clause names
   two things that do not exist. The phase-5 reading is stated in D-139 and is narrower: the
   run halts at C33, `NightlyRun` returns not completed, no stage after C33 executes, and no
   `news_digest` row is written. The line as written becomes evaluable in phase 7 and should
   be re-asked there rather than marked met here.
4. **The rotation is present every night regardless of primary health.** Evaluable, and it is
   the one line with a trap in it: it holds on a healthy night by construction and the night
   that matters is the unhealthy one, where every row carries the secondary and only two of
   them carry `was_rotation`. Both nights are asserted.

**Two further lines this plan adds, both because the phase cannot be read without them.**

5. **A re-run of C29 over one date is byte-identical**, asserted as a set comparison in both
   directions, which is D-132's grain rule made observable.
6. **The rotation pair is stable across processes and moves between adjacent dates**, which is
   D-138's hash made observable and is what a `System.Random` would eventually fail silently.
7. **One candidate's digest can be opened beside the articles it was built from**, without
   writing a query, at 5.9. This is the phase's openable deliverable and it is a done-when
   line rather than a nicety, because a line that is not one is what gets cut when the phase
   runs long. Phase 3.5 is the whole argument and it is not restated here.

**Invariants at risk:** 7, 10 and 15, which is what `BUILD_PLAN.md` names, and **6 and 11 are
added here.** **7** because the digest step is one prompt edit from scoring a candidate and
the instruction file is the enforcement point. **10** because `headline` gains a component
claiming two operations and `attribution.digest_provider` invites a third writer. **15**
because the gate is the phase. **6** because the instruction text reaching the model must be
byte-identical across every call within a night, which is the same property the prefix has one
phase later, and a locale-formatted date or an unordered article list inside the prompt breaks
it the same way. **11** because a health check is the most natural place in this system to
reach for a clock, and `last_health_check` is a column shaped exactly like an invitation to.
**16 is asserted unchanged** by every checkpoint except 5.14, which adds `cost` reads rather
than columns, `cost_ledger.cost` already being `numeric` and already counted in
`ExpectedMonetary`.

---

## 8. What blocks the phase, and what is only reported

**One item here stops the phase and six do not, and they are separated rather than numbered
into one list.** A reader scanning this section for what to do first should not have to reach
the seventh entry to find the one that prevents 5.4 from starting.

### The blocker

**B1. The phase's stated scope does not name C29, and the phase cannot be built without it.**
`BUILD_PLAN.md` phase 5 lists the local client, the Haiku client, the chain, the rotation and
the gate. `headline` has no writer in any phase in that document, and C33 reads `headline`,
so the digest step has nothing to condense. This plan puts C29 at 5.4 and **the scope line
needs the words**.

**A build session cannot close this and must not try.** Phase scope is authored [`CLAUDE.md`
§13] and a session that widened its own scope to add a component would be doing the thing §3
forbids. **It stops 5.4, and 5.4 stops 5.8**, because a digest over an empty `headline` is
D-134's no-article outcome for every candidate on every night, which is a phase that
completes and measures nothing.

**It is also the only item in this document that is closed by adding words rather than by
deciding anything.** C29 exists in `ARCHITECTURE.html` §03 with its runs, reads and writes
already stated; nothing about it is undesigned. What is missing is a line in a plan.

### Reports, six

**B2. `ARCHITECTURE.html` §01 and §03 put C29 in different layers.** The layer map has
HeadlineIngestor in LAYER 1 Ingest; the component catalogue says it "runs inside the select
layer because candidates do not exist until then". Both are human-edited. It decides one
thing, which folder the file goes in under `CLAUDE.md` §4, and nothing else. **Report and
build to §01**, the document whose subject is which layer a component is in, with §03's
sentence read as a statement about when it runs. Do not edit either.

**B3. C33's Reads cell names `digest_provider`, which is not a table.** `SCHEMA.md` has no
such table and `CONFIG_REFERENCE.md` has no such key; `digest_provider` is a column name, on
`attribution` and, as `provider`, on `news_digest`. `ReadDeclarationConformanceTests`
intersects each cell against `SCHEMA.md`'s table list and drops everything that is not a
table, so this passes silently, which is the blind spot `PROGRESS.md` item 20 already records
for the seven cells naming endpoints. **The cell most likely means `local_model_config`.**
Report it; the fix is a human edit to §03.

**B4. The secondary link has no component number.** §01's paragraph on the four components
outside the layers names C26, C27, C28 and C32 and stops. `RegistryNameTests` asserts every
registered component is named in §03, so a registered secondary client would fail it. D-136
routes around this by making the secondary a plain implementation that reads no store and is
therefore not a registry component, and the asymmetry between the two links is then in what
they read rather than in the failover path. **That is a workable answer and it is not the
same as the catalogue being right.** Report it.

**B5. `local_model_config.last_health_check` and `last_loaded_model` have no writer that can
run.** The declared writer is the UI [D-51], the UI is phase 9, and C32's Writes cell says
nothing. D-136 leaves both columns null and routes health through the run log, which is
consistent with the catalogue and with D-124's treatment of `slot_filled`. The consequence is
that §20's U6 panel line, "last successful digest run", reads `run_log` rather than this
table until phase 9 decides otherwise. Report it against phase 9.

**B6. `RUNBOOK.md`'s prerequisites already assume this phase.** "At least one digest provider
is healthy" sits in the unattended-night list against a chain that does not exist. Nothing to
fix; named so a reader does not take it as evidence the chain is built.

**B7. `digest.readiness_check_et`'s consumer is documented as LocalModelClient and the check
concerns the chain rather than one link.** On a night the local server is down and the
secondary is healthy, a readiness check that reports only the local link reports a problem
where there is none, and the reverse is worse. 5.13 builds it over the chain and
`CONFIG_REFERENCE.md`'s Consumer column is filled from what was actually read. Report the
document's attribution.

---

## 9. What phase 5 does not do

**No backfill, of headlines or of digests.** §3 gives three reasons and the first is
sufficient on its own.

**No dossier, no researcher, no proposal, no order.** C15 at 18:35 is phase 6 and does not
enter `NightlyRun.EveningOrder` here. That array's own comment says the order is declared
once and in full "including stages later checkpoints build", and today it stops at C28 with
five catalogued evening components absent, so the not-registered status it describes has
never been exercised. Phase 5 adds the two stages it builds and leaves the rest alone;
whether the array should carry every future stage is a question for whichever phase next
extends it and is reported rather than settled here.

**No judgement of digest quality.** A digest that reads oddly is not malformed [D-137], and
a step that decided which summaries were good enough would be judging [INVARIANT 7]. The
rejection surface here is empty, transport and length only.

**No answer to D-53.** Whether the local model stays local is answered from the rotation's
paired sample after a quarter, and §5's row 6 is a sanity read that no decision cites.

**No verdict on whether digest source changes outcomes.** That needs an outcome, which needs
phase 7, which needs a year [`VALIDITY.md` §3].

**And no alert type.** §18 gives the digest chain two conditions and both are handled by
falling through or halting, neither of which is an `alert` row. `AlertTypes` stays at two and
migration `0021`'s CHECK is untouched, which is worth stating because the comment in that file
invites a phase to extend it and this phase declines.

---

## 10. Authored items owed, and files

**Owed before the first checkpoint, and none of it is a build session's** [`CLAUDE.md` §13]:
the twelve decisions in §4; the phase 5 scope line in `BUILD_PLAN.md` gaining C29, which is §8's blocker B1 and is owed before 5.4 rather than before the phase;
the fourteen checkpoints in §6 entering `BUILD_PLAN.md`; and, if 5.14 is adopted, C26 moving
out of phase 6's carried obligation and into phase 5's scope.

**Owed during, and human-edited only:** `ARCHITECTURE.html` §04's evening timing if D-139 is
taken; §03's C33 Reads cell if B3 is acted on; and §03's C36 Reads cell plus §20's U-screen
list at 5.9, both of which the panel needs and neither of which a build session may write.

**Files this phase touches.**

| Path | What changes |
|---|---|
| `src/StockResearcherLab.Api` | `C36 RecordInspector` gains the digest panel and two declared reads, at 5.9. The Api still references no Pipeline and cannot |
| `docs/BUILD_PLAN.md` | Phase 5 scope and checkpoints; two carried obligations closed by D-141 and D-142; one added for phase 9 [B5] |
| `docs/DECISIONS.md` | D-131 to D-142; D-90's third option gains a pointer to D-142 |
| `docs/SCHEMA.md` | `headline`, `news_digest`, `attribution` and `local_model_config` sections, all at 5.2 or 5.3 |
| `docs/CONFIG_REFERENCE.md` | The `digest.*` table's Consumer column filled from the composition code, `digest.lookback_days` and `digest.secondary_model_id` added, `s5.no_digest_disqualifier_min_articles_90d` verified |
| `docs/FIXTURES.md` | The two forward-listed phase 5 items registered, plus the four D-134 outcome fixtures and D-138's stability pair |
| `docs/RUNBOOK.md` | The cycle table's order if D-139 is taken; the recovery section gains the digest re-run judgement from §3 |
| `docs/PROGRESS.md` | Everything measured: 5.1's six rows, the night's counts, the annualised rotation cost |
| `docs/METRICS.md` | `news_digest_150t`'s entry, which the document does not currently carry |
| `prompts/digest-instruction.md` | Nothing. It is product, it is written, and code starts reading it at 5.6 |
| `Directory.Packages.props` | The Anthropic SDK version, if D-140 is taken |
| `seed.ps1` | Its header's "still to come" sentence, half of which stops being true |

---

## 11. Verification

**Per checkpoint:** `ci.ps1`, run against a worktree at HEAD after the checkpoint is committed
and before it is pushed.

**Per checkpoint, beyond CI**, each against the real provider or the real store rather than a
fixture. 5.1: the transcript, checked for token leakage before committing. 5.2: `migrate.ps1`
twice, and both parity directions over the changed tables. 5.3: every seeded key resolved at
2021-01-04 and at the frontier, and `local_model_config` read back. 5.4: one candidate's
headline rows read whole, and the same date run twice. 5.5: the local server up, then stopped,
then restarted, within one process. 5.6: one real Haiku call and its `usage` block. 5.7: the
chain with each link down in turn. 5.8: the night's digest rows counted by
D-134 outcome. 5.9: **one candidate opened in the panel**, its three articles and its digest
read together, which is the read 5.8's verification used to describe and is now a thing that
stays. 5.10: the same date run in two processes. 5.11: both links down. 5.12: the night's
count chain. 5.14: the cost row against a hand-computed figure.

**Structural, and asserted rather than reviewed:** write ownership over the registry with
`ExpectedOwners` moved deliberately; `DeclaredAccess` throwing on C32's empty write set and on
C33 reaching for a score table; the read-declaration conformance in both directions;
`SchemaParityTests` and `guards.ps1` in both directions with `ExpectedMonetary` unchanged;
`ConfigSeeder.Keys.Count` moved with its test; `guards.ps1`'s `new Random` check passing with
no exclusion added [D-138]; and `AlertTypes` unchanged at two.

**At sign-off:** the review runs in a session that has not committed here and asks
`BUILD_PLAN.md`'s three questions [D-67]. Three things are read at sign-off rather than
measured extra, per `CLAUDE.md` §3: the four D-134 outcome counts over however many nights
have accumulated, which says whether the no-article case is rare or ordinary and therefore how
much D-60's disqualifier will bite in phase 6; the fall-through count, which is the first
evidence about the operational failure §20 calls the most likely one in this system; and the
annualised cost against §07's $1.30, which is a query against `cost_ledger` if 5.14 is adopted
and is not answerable if it is not.

---

## 12. Two decisions the measurement forced, drafted at 5.2 and not authored

**These are not in §4 because §4 was written before anything ran.** Both come out of 5.1
and neither could have been written in advance: the first because §07's article-size
estimate had never been measured, the second because nothing in this corpus knew the local
model would be a reasoning model. Numbering continues from D-142.

**Status is the same as §4's.** Authored content under `CLAUDE.md` §13, drafted here in
`CLAUDE.md` §15's amendment format so they paste into a build prompt, and not a build
session's to enter.

### D-143, the digest's input is capped by tokens and `digest.max_tokens` splits

Blocks 5.3's second commit, 5.7 and 5.8. Supersedes D-133's `digest.max_articles`.

```
D-143 The digest's input is capped by tokens rather than by article count, and
digest.max_tokens splits into an input key and an output key. ACTIVE
D-133 SUPERSEDED IN PART: its lookback window stands, its article cap does not.

THE MEASUREMENT IS THE ARGUMENT. 5.1 read 275 article bodies over the seven days
ending 2026-08-12, counted by the model that would digest them: median 1,248
tokens against ARCHITECTURE.html section 07's five to eight hundred, p25 935,
p75 1,974, p95 4,777, and a longest body of 14,099. A count cap prices nothing
across that range. Three articles is roughly 900 tokens or roughly 14,000
depending which three, and D-133 derived its three from a cost figure, so the
cap it set is the one thing in that decision the measurement contradicts.

digest.max_input_tokens is 6,000. Articles are ordered most recent first,
ties broken on the source string ordinally as D-133 already says, and are added
while the next one whole would not exceed the cap. The first article below the
cap that does not fit ends the selection; the loop does not skip it to find a
smaller one behind it, because a selection that reorders on size is no longer
the most recent articles and no document describes what it would be.

A MINIMUM OF ONE IS SENT EVEN WHERE THAT ONE EXCEEDS THE CAP. p95 is 4,777
tokens and the longest body measured is 14,099, so a candidate whose only recent
article is an earnings-call transcript is a real case rather than a contrived
one. Without the minimum that candidate produces no digest, which under D-134 is
the null-digest_text state, which under D-60 is "no digest available" and
disqualifies the name. That would make a long article and an absent article the
same fact, and D-134's second state would be standing in for a fourth thing.

THIS IS NOT TRUNCATION AND THE DISTINCTION IS LOAD-BEARING. D-137 forbids
truncating a response: a half-sentence digest reaches the researcher as a
complete fact and the validator cannot check prose. This decision selects how
many whole articles to send. No article is ever partially transmitted, no input
is cut mid-sentence, and the model always receives complete documents. Reading
the prohibition as covering both is how a rule about output quality becomes a
rule about input volume, and it is the confusion that makes the minimum-of-one
look like a violation when it is the opposite.

THE ANNUAL FIGURE BECOMES A CEILING RATHER THAN AN EXPECTATION, WHICH IS WHAT A
BUDGET NEEDS. At 6,000 input tokens and 150 output tokens a candidate, 28
candidates and 252 sessions, Haiku 4.5 at $1.00 per million input and $5.00 per
million output: input 42.34 million tokens at $42.34, output 1.06 million at
$5.29, so A FULL YEAR RUN ENTIRELY ON THE SECONDARY IS AT MOST $47.63, against
section 07's $18. The rotation's own ceiling is 2 of 28 of that, $3.40 a year,
against section 07's $1.30.

CEILING AND NOT EXPECTATION, stated because the difference is the point. The
measured median candidate reaches nowhere near 6,000 tokens: at three median
articles it is about 3,700 and most nights most candidates will be under the cap
with articles to spare. What 6,000 buys is that no night can cost more than the
figure above, which a count cap could not promise at any number.

SIX THOUSAND, and the three things that fix it. It admits three median articles
with room, four at p25 and one at p95, so the ordinary candidate is unconstrained
and the tail is bounded. It keeps the ceiling inside the same order of magnitude
as the figure section 07 priced rather than a different one. And it is under the
smallest context window in the chain by a wide margin, Haiku 4.5's being 200K,
so the cap is a budget decision rather than a technical limit and can move
without anything else moving.

digest.max_tokens SPLITS AND THE OLD NAME IS RETIRED. digest.max_output_tokens
is 150 and is the digest's own length, which is what section 07 and
CONFIG_REFERENCE.md have always meant by that number and which belongs to the
design rather than to the provider. digest.max_input_tokens is 6,000 and is this
decision's. One name for two quantities is what let them be conflated at 5.1,
where the API parameter capped the completion and the design meant the digest,
and on a reasoning model those turned out to be different numbers with nothing
saying so.

Test: a candidate whose three most recent articles sum under the cap sends three;
one whose second article would cross it sends one and does not reach past it for
a third that would fit; one whose only article exceeds the cap alone sends that
article and produces a digest rather than D-134's null state. The token count
used for selection is asserted against a counted value rather than a character
estimate.

Done when: both keys are in CONFIG_REFERENCE.md with a verified consumer,
digest.max_articles and digest.max_tokens are gone from that document as clean
edits with the prior wording in CHANGELOG.md, D-133's entry in DECISIONS.md
carries the partial supersession, and the $47.63 ceiling is in PROGRESS.md
marked as a ceiling beside section 07's $18.
```

### D-144, the request shape is a `local_model_config` column

Blocks 5.3's second commit, 5.5 and 5.7.

```
D-144 How a link must be asked is a local_model_config column, not a config key
and not a client literal. ACTIVE

local_model_config gains request_options, jsonb, nullable. It holds the
provider-specific parameters a link's request must carry beyond the ones every
link takes, and it is null for the secondary, which needs none.

WHY THE COLUMN AND NOT A CONFIG KEY. This is a property of the endpoint's loaded
model rather than of the digest step. Swap the loaded model and the setting
changes with it; a digest.* key stays behind, describing a model that is no
longer there, and does so silently because nothing reads a key against the model
it was written for. local_model_config is already the row that says where a link
is and whether it is enabled, and this is the same kind of fact.

WHY NOT A CLIENT LITERAL. A literal cannot differ per link, and the two links
already need different requests: reasoning_effort is an OpenAI-compatible
parameter that this endpoint honours and the secondary has no use for. A literal
would also put a provider's parameter name inside a client that section 07 says
is written against the OpenAI-compatible surface rather than against a product.

WHAT IT HOLDS TODAY, recorded because a column with no stated content is a
column the next session guesses at. For the local link: reasoning_effort set to
none. Measured at 5.1 against qwen3.6:latest on Ollama, where that produced a
243-character digest in 74 completion tokens, and where max_tokens 150 and
max_tokens 2,000 both produced zero characters of content. The native surface's
think:false produced the identical 243 characters, which is what says the
setting is the model's behaviour rather than one endpoint's spelling of it.
chat_template_kwargs.enable_thinking set to false was accepted and ignored,
returning a response byte-identical to the call without it, so it is recorded
here as not working rather than left for the next session to try.

THE FAILURE THIS PREVENTS, WHICH IS WHY THE COLUMN EXISTS RATHER THAN THE
PARAMETER BEING SET SOMEWHERE. A reasoning model returns HTTP 200, a normal
usage block, a finish_reason, and zero characters of content. Nothing errors.
Under D-137 an empty response is malformed, so the chain retries once, gets
another empty response, marks the local link unhealthy for the run, and digests
every candidate on the secondary. Every night. With the local server running,
answering in under two seconds, and reporting healthy. The record is not silent,
news_digest.provider saying haiku on every row being exactly what D-29 exists
for, but nothing raises anything and the year costs the full-secondary ceiling
rather than the local one. This is CLAUDE.md section 1's failure class: the run
completes, the numbers look plausible, and what was measured is not what was
meant to be measured.

A HEALTH CHECK THAT READS A STATUS CODE IS THEREFORE INSUFFICIENT, and that
follows from the paragraph above rather than being a separate rule. A link is
healthy when it returns non-empty content to a small fixed probe under
digest.health_timeout_ms, not when it returns 200. The probe's text is fixed and
is not the digest instruction, so a health check costs nothing and cannot be
confused with a digest.

Test: 5.5 asserts a link answering 200 with empty content is unhealthy, against
a fabricated response rather than against the live server, because the live
server can be made to produce that state today and cannot be relied on to keep
producing it. 5.5 also asserts the live local link is healthy with
request_options applied and unhealthy with them removed, which is the same
assertion against the case that produced the finding.

Done when: migration 0023 adds the column, SCHEMA.md's local_model_config
section states what it holds and that it is null for the secondary, the seeder
writes the local link's row with reasoning_effort none, and PROGRESS.md carries
the enable_thinking result as a measured negative rather than an untried option.
```

**One thing both decisions leave alone, named so it is not read as settled.**
`ARCHITECTURE.html` §07's table names LM Studio and the machine runs Ollama. Neither
decision touches that: D-144 is about what a link's request carries and is indifferent to
which product serves it, and `local_model_config.endpoint` is a row precisely so the server
can differ. It is reported in `PROGRESS.md` rather than fixed, §07 being human-edited only.
