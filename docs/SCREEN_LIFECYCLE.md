# SCREEN_LIFECYCLE.md

How a screen enters this system, how it is judged while it is in, and how it leaves.
Registration, shadow running, promotion and retirement, in a form someone who was not
part of the conversation could apply.

**This document is the design behind D-84 to D-90, and those decisions are authored.**
It was produced against `prompts/spent/design-screen-lifecycle.md`. §11 is the index
into the register, which is where the decisions live; §12 is the `ARCHITECTURE.html`
edits, and they are applied. `CHANGELOG.md` carries the prior wording of every one,
and `BUILD_PLAN.md`'s carried obligations carry every part of this document that is
code, against the phase that owes it.

**No code is written and no threshold below was chosen against a measurement.** No
measurement exists to choose against: phase 3 has not run, screens are phase 4, and the
tuner is phase 8. That is the reason the work happened now rather than inside phase 4
[`CLAUDE.md` §11]. Every figure below is derived from an existing config key, from
arithmetic over the slot pool, or from a pre-registered threshold already in
`VALIDITY.md` §4. Where a figure is none of those it is marked **DRAFT FIGURE** and says
what it rests on. One is.

**This document states the design and shows the working. `DECISIONS.md` states the
rules.** Where the two differ the register is right, and nothing here is a second copy
of a ruling [D-76, D-77, D-83].

**FINDING, carried rather than closed.** D-73 sorts the corpus into specs, which take
clean edits, and records, which keep their strikes, and it names four documents in the
first class and two in the second. This document is in neither list, as `METRICS.md` was
not when it was written. It reads as a spec, since it is read to know the current state
of the lifecycle, so edits to it are clean and their prior wording goes to
`CHANGELOG.md`. That is a reading and not a decision, and closing it is an authored
amendment to `CLAUDE.md` §13 [`CLAUDE.md` §13].

**Read against** `ARCHITECTURE.html` §01 to §20, `SCHEMA.md`, `DECISIONS.md` D-1 to
D-83, `VALIDITY.md`, `BUILD_PLAN.md`, `CONFIG_REFERENCE.md`, `METRICS.md`,
`GLOSSARY.md`, `RUNBOOK.md` and migrations `0001` to `0005`, all at HEAD `2cad4cc`.
The `ARCHITECTURE.html` line numbers §12 cites are that revision's, before its own
edits moved them.

**Read by** phase 4, which builds the registration flag and the allocator behaviour;
phase 8, which builds the evaluation; and phase 9, which renders U4.

---

## 1. What a shadow screen is

A shadow screen is scored exactly like a live screen and allocated no slots.

It writes `screen_score_daily` for every ticker on every date, accumulates its trailing
distribution in `screen_history`, and inherits D-9's floor unchanged, the 98th
percentile of its own trailing 250-day score distribution. It has no threshold of its
own to register, because D-9's floor is self-referential and therefore already defined
for any screen that has a distribution.

What it does not do is write `candidate_set`. That is the whole of the difference in
one sentence, and it is why `candidate_set` remains the unambiguous definition of the
candidate set after this change: no shadow row ever appears there.

It does write `attribution`, and §4 is the whole of what that costs.

### 1.1 Three states, and they are a config value

`screens.<id>.state` takes one of `live`, `shadow` or `retired`.

- `live` scores, holds slots, and surfaces candidates.
- `shadow` scores and surfaces nothing.
- `retired` does not score. The row stays in `config_rows` at its final version so
  that history written under it remains interpretable, which is the same reason
  `security` keeps a delisted row rather than deleting it [D-48].

Config rows are append-only and versioned, so a promotion is an insert at version + 1
and every attribution row already carries the `config_version` in force when it was
written [`CLAUDE.md` §8, D-72]. That is what lets a promoted screen's shadow history
and its live history be segmented rather than pooled, and it exists already rather than
being added for this.

**A screen is a config row, so a shadow screen is a config row.** Nothing about
registering one is a deployment [`ARCHITECTURE.html` §05, `CLAUDE.md` §5].

### 1.2 The provenance sense of "live" is renamed to "prospective"

`live` above names a screen state. It was also being used to name where an observation
came from, as against a backfilled one, and §5's rule needs both senses in one
sentence. An observation from a night that actually happened is **prospective**. An
observation produced by the phase 4 screen backfill is **backfilled**. `live` from here
means only the screen state.

---

## 2. What actually has to change

Most of the mechanism exists. `screen_score_daily` is already ticker by screen by day
and already scores every ticker rather than a shortlist, because D-9's floor cannot be
computed without scoring everyone. `screen_history` is already keyed on screen. The
changes are these and no others.

| Component | Change |
|---|---|
| C13 ScreenEngine | Iterates every screen whose state is `live` or `shadow` instead of a fixed five. Reads the flag and does nothing else differently. Floors, distributions and the set-based per-date shape are untouched |
| C14 CandidateAllocator | Applies the size quota and the no-backfill rule to live screens as now. For each shadow it takes the shadow's top `tuner.slot_cap` under the same proportional quota, writes those into `attribution`, and writes nothing into `candidate_set` |
| C22 ScreenTuner | Computes its existing measure for every registered screen and allocates slots among the live ones only. Gains one write, `screen_evaluation` |
| Everything else | Unchanged. Every other component's change is a read filter, and §4 enumerates them |

**C13's change is a flag and nothing more, and that is a design constraint rather than
an observation.** A screen engine that behaved differently for a shadow would be
producing scores from a different code path than the live screens it is meant to be
compared against, which is D-58's and D-69's objection in a new place: a screen whose
scores come from a different population, or a different path, than the screen it is
measured against is not measurable against it.

**C14's change is where the meaning of an existing table moves**, and §4 is that
section.

### 2.1 The depth a shadow is recorded at

A shadow writes at `tuner.slot_cap`, which is 12.

A shadow has no slot allocation, so it has no natural depth, and the choice is between
recording nothing until it is promoted and recording the widest defensible record. The
cap is the widest depth any live screen can ever reach, so a shadow recorded at the cap
can be truncated down to any live screen's depth at evaluation and never needs to be
widened after the fact. Recording narrow and widening later is the reconstruction
INVARIANT 4 forbids.

The proportional quota in §8.1 applies at that depth: at 12 slots it is 3 large, 4 mid
and 5 small, and D-8's rule that an unfilled slot stays empty applies unchanged.

### 2.2 Depth is equalised at evaluation, on both sides

Every comparison truncates the screen under test **and the screens it is measured
against** to one depth by `rank_within_screen`.

The depth for a date is the smallest number of rows any screen in that comparison
recorded on that date. Taking the minimum rather than a fixed number is what keeps D-8
intact: a live screen that filled three of four slots recorded three that night because
nothing else cleared its floor, and comparing its three against a shadow's twelve
measures depth rather than mechanism.

Truncating only the shadow would be the same error in the other direction. A screen's
top three names are better than its top twelve on any screen that ranks at all, so an
untruncated incumbent measured against a truncated challenger is a comparison of depths
wearing the name of a comparison of screens.

**`rank_within_screen` exists** on `screen_score_daily` and is written by C13
[`0001_snapshot.sql:238`]. It is not on `attribution`, and §4.4 says what carries it.

---

## 3. Registration happens before phase 3, and that is the point

The mechanism is phase 4 work, because that is when screens exist at all. The decisions
are due now, because the history a candidate screen could be selected on comes into
existence when the backfill runs.

Register the family after four years of scored history is sitting there and nothing
distinguishes a screen chosen for its mechanism from one chosen because it worked on
the data. Every member of the family in §7 states its mechanism as a reason it should
work before any number exists, and no number exists [`CLAUDE.md` §11, `VALIDITY.md`
§4's opening].

**A correction to the brief's premise, which does not change the conclusion.**
`BUILD_PLAN.md` phase 3 is "Backfill of ingest and compute" and phase 4 carries "Then
backfill screen scores over the five years using the two-pass approach". Screen scores
are therefore backfilled in phase 4, not phase 3. The timing argument is unaffected,
because phase 3 is what makes the phase 4 screen backfill possible and the registration
has to precede both. What it does change is where the storage and read-pattern cost in
§9 lands, which is phase 4.

---

## 4. The attribution question

This is the part that needs the most care, and it is the reason a separate shadow store
was weighed at all.

If the allocator writes an attribution row for every name any screen surfaced, then
`attribution` stops meaning "the candidate set" and starts meaning "everything any
screen surfaced". That is a change to what an existing table means, and this project's
characteristic defect is a reader that was not found.

### 4.1 The alternative, costed, and why it is rejected

A separate `shadow_attribution` table would leave `attribution` untouched and every
reader of it correct by construction. That is a real benefit and it is the only one.

Against it:

- **The forward return fill would exist twice or read twice.** C21 ForwardReturnFiller
  owns the update of the nine return columns and fills every row it finds. Against two
  tables it either becomes two code paths that have to agree, or one path with a
  widened write set across two tables. Two paths that must agree on acquisition,
  delisting and bankruptcy handling is exactly the case `ARCHITECTURE.html` §13 says
  needs an explicit rule from day one, and having it in two places is how the second
  copy drifts.
- **Point-in-time context would be duplicated.** `attribution` already stores
  `size_bucket`, `sector` and `regime` as they stood that night. A second table either
  repeats those columns, in which case two writers freeze the same facts and can
  disagree, or omits them, in which case a shadow's evaluation cannot be segmented by
  regime and the comparison against a live screen is not like for like.
- **The evaluation would be a join across two tables instead of one aggregate.**
  `ARCHITECTURE.html` §19 is explicit that the tuner and the calibration report are one
  `GROUP BY` over `attribution` and that this is why they need no parallelism. A
  comparison that has to union two tables to reach one population loses that property
  for no gain.
- **It is a second store for the same fact.** D-76, D-77 and D-83 each removed a second
  statement of one fact from this corpus. A second table holding the same grain, the
  same context columns and the same nine return columns is that shape again.

**So `attribution` carries shadow rows.** What follows is the cost of that decision
paid in full rather than deferred.

### 4.2 The grain does not change, and the primary key is why

`attribution`'s primary key is `(ticker, date)` [`0001_snapshot.sql:290`]. A ticker
surfaced by a live screen and by a shadow on the same date is one row, not two.

That is not a constraint to work around. It is the correct shape, because the forward
return of a name on a date is one number however many screens surfaced it, and
`screens_surfacing` is already a `text[]` holding every screen that did
[`0001_snapshot.sql:273`].

So the grain stays "ticker by day surfaced". What changes is the **population**: rows
now exist for tickers that no live screen surfaced. And `screens_surfacing` and
`score_per_screen` gain shadow entries on rows that were already there.

A grain of ticker by date by screen was considered and rejected. It multiplies the
table by the overlap factor, breaks D-40's "one row per candidate per night", and
stores the same forward return several times, which is a fact stored more than once and
therefore a fact that can disagree with itself.

### 4.3 `surfaced_as`, and why it is stored rather than derived

A new column `surfaced_as`, `NOT NULL`, `CHECK (surfaced_as IN ('candidate','shadow'))`.
`candidate` where at least one live screen surfaced the name that night, `shadow` where
only shadows did.

**It is derivable and it is still stored**, for the reason `regime_label` is `NOT NULL`
with a `CHECK` rather than computed on read [D-80]. Deriving it means resolving which
screens were live on that date and re-reading `screens_surfacing` against that set, and
the live set moves: the day a shadow is promoted, every historical row it appears on
would change label under a derivation. That is today's definitions applied to a past
date, which is the thing INVARIANT 4 exists to prevent, arriving through a column
nobody thought of as a write.

The constraint rather than a writer-side check, for D-80's reason exactly: this column
segments every analysis in §4.5, so a drifted value lands in its own bucket in every
one of them without ever erroring. A writer-side check protects one writer; a
constraint protects the column.

### 4.4 `score_per_screen` carries the rank

`score_per_screen` is `jsonb` and holds each surfacing screen's score frozen as it
stood. It gains the rank alongside the score, so the shape becomes screen id to an
object of `score` and `rank`:

```
{"S2": {"score": 87.4, "rank": 3}, "X-FM": {"score": 91.2, "rank": 1}}
```

§2.2's truncation needs `rank_within_screen` and the alternative is a join from
`attribution` to `screen_score_daily`, the largest table in the system, on every
evaluation. The rank is frozen at write time either way, since C13 never rewrites a
past date, so the join is correct and merely expensive. Carrying it on the row is what
keeps the evaluation the single-table aggregate `ARCHITECTURE.html` §19 relies on.

This is a shape change to an existing `jsonb` column with no rows in it yet, so it
costs nothing now and cannot be done cheaply later.

### 4.5 Every reader of `attribution`, and what it does with a shadow row

Current and planned, across every later phase. Sources: `ARCHITECTURE.html` §03 Reads
column, §15's screen table, §15's figure 11 read path, and §12's abstention note.

| Reader | Phase | Means | What it does with a shadow row |
|---|---|---|---|
| C21 ForwardReturnFiller | 8 | everything surfaced | **Fills it.** No filter. This is the reason the design uses `attribution` at all: one filler, one set of acquisition and delisting rules, filling live and shadow rows in the same pass |
| C22 ScreenTuner | 8 | everything surfaced | **Reads it.** Computes the measure for every registered screen; §6.5 has the allocation filter, which is on the allocation half rather than the measurement half |
| C23 LessonWriter | 8 | candidates | **Wrong without a filter.** A lesson is written into the researcher's cached prefix. A lesson derived from names no researcher ever saw is a statement about a population the researcher does not operate on, delivered to it as though it were about its own work |
| C24 CalibrationReporter | 8 | candidates | **Wrong without two filters, and they are different.** See §4.6 |
| C31 ReadModelBuilder | 9 | both | **Both.** Candidate rollups and equity-curve context are candidates only; the screen family panel behind U4 is everything surfaced. Two read models, not one with a flag |
| U1 Tonight's run | 9 | n/a | Reads `candidate_set`, `proposal` and `order` and not `attribution`. Unaffected, and named because it is the screen a reader would expect to be affected |
| U2 Candidate detail | 9 | candidates | **Wrong without a filter.** Its layout is dossier text, digest, both models' verdicts and forward returns. A shadow row has no dossier, no digest and no proposal, so it renders as a candidate with four empty panels |
| U4 Screens | 9 | both | **Reads both, and this is where the family is shown.** Its description changes from the five screens to every registered screen with its state, which §12.4 drafts |
| U7 Primary claim | 9 | candidates | **Wrong without a filter, and it is the most dangerous one.** U7 is the pre-registered primary claim. `VALIDITY.md` §4 requires at least 1,000 observations on each side of BUY against PASS, and every shadow row is a name with no verdict on either side. Left unfiltered it does not corrupt the separation, it corrupts the counts, and the counts are the pre-registration |
| Abstention analysis | 9 | candidates | **Wrong without a filter.** `ARCHITECTURE.html` §12 measures abstention as what the candidates the researcher declined went on to do. A name it was never shown was not declined |
| C28 ConcentrationMonitor | 4 | n/a | Reads `candidate_set`, `position` and `security`. Unaffected by construction, because shadows write no `candidate_set` row. Named because megacap share and distinct-ticker count are candidate-set properties and a reader would reasonably check |
| C15 DossierBuilder | 6 | n/a | Reads `candidate_set` and not `attribution`. Unaffected |
| `VALIDITY.md` §3's counts | n/a | candidates | The ~7,000 candidates a year and ~1,400 per screen are candidate rows. §9.2 states what the table's row count becomes and that these figures are unchanged in meaning |
| `RUNBOOK.md`, never re-run the attribution write | n/a | everything surfaced | Applies to shadow rows identically and needs no wording change. A re-run would apply today's screen definitions to a past date whether the screen holds slots or not |
| `WORKED_EXAMPLE.md` | n/a | candidates | Traces one candidate. Unaffected, and its attribution row would carry `surfaced_as = 'candidate'` |

~~**Eight readers mean "candidate"**~~ [corrected, Q.6] **Seven readers mean "candidate"
and would be wrong without a filter. Three mean everything surfaced. Three that a reader
would expect to appear do not read the table at all.** Stating the third group is
deliberate: a reader auditing this later needs to know they were checked rather than
missed.

**The table above is the specification and the count is read off it** [D-128]. The
enumeration marks seven rows "candidates": C23, C24, U2, U7, the abstention analysis,
`VALIDITY.md` §3's counts and `WORKED_EXAMPLE.md`. The other two figures in this sentence
were right, which is what made the wrong one hard to see. `ScreenLifecycleCountTests`
now counts the Means column and holds both sentences against it, so the prose cannot
drift from the table again without failing.

### 4.6 The two filters are different, and conflating them is the trap

A **candidate** row can carry a shadow screen id in `screens_surfacing`. That happens
whenever a live screen and a shadow both surface the same name, and it is common enough
to be met by anyone reading the table rather than rare enough to ignore: **1,223 of the
34,932 candidate rows frozen over 2021-01-11 to 2026-08-12 carry a shadow id, which is
3.5 percent** [phase 4 sign-off].

**The lower the co-surfacing rate, the more a reader who conflates the two filters gets
wrong, not less.** The screens are near-independent, 0.5 percent of allocated candidates
carrying more than one live screen against the roughly 4 percent five independent
top-two-percent rankings would give [§06], so a name two screens both surface is an
uncommon and therefore informative event. A per-screen report that groups on every id in
`screens_surfacing` does not dilute a common case; it opens a calibration bucket for a
shadow out of exactly the rows that carry the most signal. This paragraph cited §06's
estimate of 10 to 15 percent until the phase 4 sign-off review, and that estimate was
withdrawn against measurement at Q.9.

So there are two filters and they do different work:

- `surfaced_as = 'candidate'` selects the **population** a reader means.
- Filtering `screens_surfacing` or `score_per_screen` to live screen ids selects the
  **grouping** a per-screen report means.

C24 CalibrationReporter needs both. D-45 reports calibration per screen, and grouping a
candidate row by every id in `screens_surfacing` would open a calibration bucket for a
shadow that has no proposals in it, which renders as a screen with a Brier score of
nothing rather than as a screen that was not judged.

C22 ScreenTuner needs the population unfiltered and the grouping unfiltered, and
filters only at the point of allocating slots. That is §6.5.

### 4.7 Making the filter structural rather than remembered

~~Eight readers~~ [corrected, Q.6] Seven readers that must each remember a `WHERE` clause
is seven chances to forget one, and forgetting it produces no error. A view is the
mechanism that removes the choice:

```
CREATE VIEW candidate_attribution AS
    SELECT * FROM attribution WHERE surfaced_as = 'candidate';
```

Every reader that means "candidate" reads the view. Reading the table becomes a
deliberate act rather than the default, which inverts which mistake is easy
[`CLAUDE.md` §5]. The view is read-only, so it introduces no write-ownership question
and the conformance test over the stage registry is untouched.

A test asserting that each candidate-meaning reader's declared read set names the view
rather than the table is what makes it hold, and it is the same shape as phase 8's
existing "a test asserts the tuner's query never touches the SPY column".

---

## 5. Backfilled against prospective, answered separately for the two purposes

The question the whole rule turns on. Registering before the screen backfill means a
shadow's sample floor is met the moment the backfill finishes, because four years of
observations arrive at once rather than accumulating. A screen that works in backfill
and fails live is the most common way this kind of thing fails.

### 5.1 For a shadow's trailing distribution: backfilled observations count, and must

D-9's floor is the 98th percentile of the screen's own trailing 250-day score
distribution. Without backfilled scores a newly registered shadow has no distribution
for its first 250 sessions and therefore no floor, which means no record at all for its
first year.

The condition is D-58's and D-69's, restated: **backfilled scores count toward the
distribution only where the screen's inputs are backfillable to the same definition the
live screen will use.** A floor drawn from a backfill population the live screen does
not share is the defect D-58 removed short interest for and D-69 is open on. §7.5 is
the one member of the family that fails this condition and what happens to it.

### 5.2 For a promotion or a retirement: prospective observations only

Backfilled observations may be computed, must be reported, and must be labelled
backfilled. They do not count toward the sample floor in §6.2 and cannot satisfy the
sustained-fail requirement in §6.3.

Three reasons, in order of weight:

1. **`CLAUDE.md` §11 prohibits tuning screens on forward returns before the researcher
   has judged anything**, because it optimises toward whatever captured market beta in
   the backfill window, which in most recent windows means trend and large caps. A
   promotion is a stronger action than a slot move and the prohibition binds harder,
   not less.
2. **The rule would otherwise mean nothing.** Four years arrive at once, so a floor
   counting them is met on registration day. A sample floor satisfied before the thing
   being sampled has happened is not a floor.
3. **`VALIDITY.md` §5 and §6.** Five years of backfill is one macro environment with
   one interest rate path, and §6 names regime confounding as stated rather than
   mitigated. A promotion is a permanent change to the candidate generator made on one
   regime's evidence.

### 5.3 Two documents that appear to say otherwise, read

Both were checked rather than assumed, and neither licenses a promotion on backfill.

`VALIDITY.md` §6 says "Backfill is never used to evaluate the researcher, only the
screens". That row's threat is model training contamination and its subject is the
researcher. It says backfill is unusable for the researcher; it does not say backfill
is sufficient for a screen, and it is not a statement about promotion, which did not
exist as a concept when it was written.

`BUILD_PLAN.md` phase 8's definition of done includes "the tuner moves slots on
backfilled data and respects the floor of four and cap of twelve". That is a build
verification criterion, and it has to be: phase 8 runs before any prospective night
exists, so there is nothing else for the tuner to be tested against. Verifying that the
tuner works on backfilled data is not authorisation to act on what it says.

**This is the clause a human should look hardest at.** It is the one place this design
reads two authored documents against their surface, and if the reading is wrong it is
§5.2 that changes and nothing else.

---

## 6. The retirement rule

Stated so it could be applied by someone who was not here.

### 6.1 The measure, and why absolute alpha is not it

The measure is **peer-relative forward return at 21 days**, meaning
`return_21d_vs_peers`, and the hit rate computed on the same column. It is the measure
C22 ScreenTuner already computes.

**Absolute alpha is not it, and neither is anything read from the SPY column.** The
universe runs to $300M and the quotas guarantee 15 of 40 slots to small caps. Measured
against a large-cap index in a large-led market every small-cap candidate posts
negative alpha regardless of how well it was chosen, so a retirement rule reading SPY
would retire the sentiment and flow screens, which are the two that structurally tilt
small and the two doing the most to keep this system off megacaps [D-42, INVARIANT 5].
A rule that can retire a screen is strictly more destructive than a rule that can cut
its slots to four, so INVARIANT 5 binds on it at least as hard.

**Why 21 days and not 5 or 63.** It is the horizon `VALIDITY.md` §4 already
pre-registers the primary claim at, so the retirement rule and the claim the system
exists to test read the same column at the same horizon rather than two. And D-34's
40-day time stop means an edge that appears only at 63 days is one this portfolio
cannot hold long enough to collect. The 5 and 63 day columns are reported and are not
the measure, exactly as `VALIDITY.md` §4 requires the same sign at 5 and 63 without
making either the test.

### 6.2 The minimum sample before the question may be asked at all

**1,000 prospective observations for the screen under test**, counted as attribution
rows carrying that screen's id, truncated to §2.2's depth.

It is `VALIDITY.md` §4's pre-registered per-side count, used unchanged rather than
invented. The same document puts a screen at roughly 1,400 candidates a year, so the
floor is reached in something under a year of prospective running and the rule is
neither unreachable nor immediate.

For a paired comparison, meaning a successor or an extraction against its named
incumbent, the floor is **250 paired observations**, where a paired observation is one
ticker on one date carrying both screens' ids after truncation.

**DRAFT FIGURE, and what it rests on is an assumption rather than a preference.** The
variance of a paired difference is `2σ²(1 - ρ)`, so a paired test matches an unpaired
one's power at `n_paired = n_unpaired × (1 - ρ)`. Against the unpaired floor of 1,000
above, 250 is exactly `ρ = 0.75`. That is not an observation about the number; it is the
assumption the number already makes, and stating it is what makes the number
falsifiable. A reader who thinks a shadow and its incumbent overlap less than that now
knows precisely which quantity to disagree about, and by how much the floor moves if
they are right: at `ρ = 0.5` the floor is 500, and at `ρ = 0.9` it is 100.

**The assumption is observable, and revising the floor on the observation is not
result-shopping.** The realised correlation between a shadow's per-observation
peer-relative return and its incumbent's is computable the moment both have run over
the same names on the same dates, which is what pairing already requires. Measuring it
measures **the precision of the instrument, not the answer the instrument gives**: `ρ`
says how many observations a paired test needs and says nothing whatever about whether
the shadow beat the incumbent. So re-setting the floor on a measured `ρ` is the one kind
of after-the-fact adjustment `CLAUDE.md` §11 does not forbid, and this paragraph exists
so that the first person to measure `ρ` knows that before they measure it rather than
after.

**What would still be result-shopping**, stated so the permission is not read wider than
it is. Re-setting the floor after seeing the paired difference. Or setting it from a `ρ`
measured over the same window the promotion is then decided on, which lets the floor be
chosen by the data it will be applied to. The measurement that revises this floor is a
property of the pair, taken before the comparison it sizes is read, and recorded as a
`config_rows` version like every other value that moves [`CLAUDE.md` §8].

### 6.3 The sustained-fail requirement

**Three consecutive monthly evaluations.** C22 runs monthly, so this is a quarter, and
one bad quarter cannot retire a screen because a quarter is what it takes to nominate
one at all.

Each evaluation is over the trailing window that satisfies §6.2, not over the month
alone. A rule evaluating one month at a time asks a question of about 115 observations,
which is noise wearing a monthly cadence.

### 6.4 What happens on a fail

A fail **nominates**. It does not execute.

1. C22 writes the evaluation and the nomination into `screen_evaluation` (§6.6).
2. The nomination is visible on U4 and is not acted on.
3. Execution waits for the next config boundary in §6.7.
4. At the boundary, an operator either executes the nomination or records why not. The
   rule nominates; a human retires.

A screen may leave a nomination by passing an evaluation, which resets the consecutive
count to zero.

### 6.5 The tuner question, answered

**Does the tuner compute the measure for every registered screen and allocate slots
only among the live ones, or does shadow evaluation belong somewhere else?**

**The tuner. One computation, with the filter on the allocation half.**

The reasoning. The measure C22 already computes per screen, peer-relative hit rate and
alpha with shrinkage, is the shadow evaluation. Computing it in a second component is
one fact stated twice, which is the shape D-76, D-77 and D-83 each removed from this
corpus, and two implementations of one measure is exactly the case where the second
drifts and nobody can say which is right. C22 also already reads the whole of
`attribution`, so it needs no new read to see shadow rows.

INVARIANT 2 is not in the way. It says screens never read each other and each sees only
its own config and the percentile store. C22 is not a screen, and it has read every
screen's results since it was designed.

D-43 and INVARIANT 14 are not in the way either, and the distinction matters. D-43's
"touches nothing else" is about what the tuner **tunes**: it moves screen slots and
never risk caps. Recording a measurement is not tuning. INVARIANT 14 says risk caps are
operator configuration and are not tunable, and nothing here touches a risk cap.

**Its write set changes, and it gains one table rather than a column somewhere.**
`config_rows` is not the place for a measurement: it is versioned configuration whose
current value is `MAX(version)` for a key, and a measurement written there would resolve
as config for a simulated date [INVARIANT 13]. `screen_history` is the wrong grain and
the wrong writer, being screen by **day** and owned by C13 [INVARIANT 10]. `calibration`
is per model per screen per report and owned by C24.

### 6.6 `screen_evaluation`

Grain: screen by evaluation date. **Writer: ScreenTuner**, insert only.

`screen_id`, `evaluation_date`, `state`, `observations_prospective`,
`observations_backfilled`, `comparison_depth`, `hit_rate_vs_peers`,
`alpha_21d_vs_peers`, `alpha_shrunk`, `paired_against`, `paired_delta`,
`consecutive_fails`, `verdict`, `config_version`.

`verdict` is one of `hold`, `nominate_promote` or `nominate_retire`, `NOT NULL` with a
`CHECK`, for D-80's reason.

`paired_against` is the incumbent screen id for a successor or an extraction and null
for an addition and for a live screen.

**The table is not optional and the sustained-fail rule is why.** §6.3 asks whether a
screen failed three evaluations running. That question cannot be answered by
recomputing history, because screen definitions and slot allocations drift and a
recomputation applies today's definitions to a past evaluation, which is D-40's
objection arriving in the learning layer. The consecutive count has to be a record.

It also gives a promotion or a retirement the evidence it was made on, at the time it
was made, which is what `CLAUDE.md` §11 means by recording a decision before the number
that would justify it exists.

### 6.7 Nothing executes until the primary claim has its sample, and any due are bundled

A promotion changes the rubrics and the dossier. A retirement does the same in reverse:
`ARCHITECTURE.html` §07 puts five screen rubrics in the cached prefix and gives each
screen its own candidate block, so the set of screens is the set of rubrics.

`CLAUDE.md` §12 lists the rubrics and the dossier among the changes that invalidate
comparisons across the boundary. So a promotion or a retirement does not merely split
the retiring screen's own record. **It splits the primary claim.**

Two rules follow:

- **No promotion and no retirement executes until the primary claim has reached its
  pre-registered sample**, which is `VALIDITY.md` §4's at least 1,000 observations on
  each side of BUY against PASS at 21 days, with `VALIDITY.md` §4's minimum evaluation
  period of 12 months regardless.
- **Every nomination due at that point is bundled into one boundary.** Two boundaries
  three months apart produce three segments of history where one produces two, and
  `CLAUDE.md` §12 says to bundle changes that invalidate comparisons for exactly this
  reason.

This is also what stops the family being a slow drift. Four shadows promoted one at a
time over two years is four boundaries and five incomparable segments.

### 6.8 The rule in one place

A screen is nominated for retirement when, at three consecutive monthly evaluations:

- its shrunk 21-day peer-relative alpha is at or below zero, **and**
- it has at least 1,000 prospective observations at §2.2's depth, **and**
- its hit rate is at or below the live family's median hit rate over the same window
  and the same depth.

A screen is nominated for promotion under §8.2's bar. Execution of either waits for
§6.7's boundary.

---

## 7. The family

Three kinds under two statistical treatments.

**Successors** sit on the same axis as a named live screen and are judged head to head
against it. The comparison is paired, over the same names on the same dates at the same
depth, so it needs far fewer observations than judging a screen against nothing.

A successor differs by **mechanism, not by parameter**. Trend measured on price against
trend measured on earnings revisions is a mechanism. The same trend measured over a
different lookback is a parameter, and parameter variants are what the tuner already
does with slots.

**Extractions** are a screen whose sole mechanism is a metric that is already one of
several inputs to a live screen, where it can be outvoted. An extraction is paired
against the screen it was extracted from, on the same terms as a successor.

**Additions** are orthogonal to every live screen and are judged against the family
mean, because the best of four looks better than four independent things.

**Extractions and successors are both paired against a named incumbent, so there are two
statistical treatments across three kinds.** The treatments differ in what the bar is
stated against and in which standard error sizes it, and not in whether §8.2's
multiplicity correction applies. It applies to both, counted within each family
separately and over the members eligible at that evaluation rather than the members
registered, because promoting whichever of k paired shadows wins selects the maximum of
k exactly as promoting whichever of k additions wins does. Pairing shrinks the standard
error; it does not remove the selection across tests.

### 7.1 Two of the four proposed additions are not orthogonal, and are reclassified

Verified against `ARCHITECTURE.html` §05's ranking table, line 305. S1 ranks on
`fcf_yield`, `ev_ebit_vs_own_5y`, `roic_4q_change`, `gross_margin_4q_change`,
`net_debt_ebitda_inv`, `accruals_inv` and `share_count_change_inv`.

So `accruals` and `share_count_change` are already S1 ranking inputs. They are also S1
rubric disqualifiers: §07's S1 row disqualifies "Accruals in the top quintile" and
"Share count rising more than 5 percent a year".

They are therefore **extractions**, not additions. The mechanism claim for each is not
that the metric is new but that it is currently one of seven inputs and can be outvoted
by the other six, and a screen ranking on it alone cannot be.

### 7.2 The four members

| ID | Kind | Paired against | Mechanism, stated as a reason it should work before any data | Inputs |
|---|---|---|---|---|
| X-NSI | Extraction | S1 | Net share issuance is a management action rather than an accounting outcome. A company retiring shares is returning capital and signalling that management thinks the shares are cheap; one issuing them is diluting and often funding a burn. As one of seven S1 inputs it can be outvoted by six valuation and quality measures on exactly the names where dilution is the whole story | `valuation_daily.share_count_change`, from `fundamental_snapshot.shares_outstanding` |
| X-ACC | Extraction | S1 | Accruals measure the divergence between accounting earnings and cash. A business whose earnings are not cash is one whose earnings will revert, and the divergence is visible before the reversion. As one of seven S1 inputs the same six can outvote it, and S1's own rubric names accruals as its characteristic failure | `valuation_daily.accruals`, from `fundamental_snapshot.net_income` and `cash_from_operating` |
| X-FM | Addition | none | Fundamental momentum is the trajectory of the business rather than its level, which is D-11's principle applied to revenue rather than to returns. S2 measures momentum in the price; nothing measures it in the operating numbers, and the two lead each other at different times | `valuation_daily.revenue_growth_4q_trend`, from `fundamental_snapshot.total_revenue` |
| X-PEAD | Addition | none | Post-earnings drift is the oldest documented anomaly in equities and its mechanism is under-reaction: prices adjust to a surprise over weeks rather than at the announcement. The earnings-day return is the surprise proxy, because it is the market's own reading of the surprise and needs no estimate | `events` where `event_type` is earnings, joined to `price_daily`. **See §7.5** |

### 7.3 Fundamental momentum drops `gross_margin_4q_change`, deliberately

The obvious second input for X-FM is `gross_margin_4q_change`, and it is an S1 ranking
input [§05 line 305]. Including it would make one screen an extraction and an addition
at once, which is two statistical treatments applied to one screen, and there is no
sensible way to correct such a thing.

So X-FM is defined on `revenue_growth_4q_trend` alone.

**One caveat, stated rather than glossed.** `revenue_growth_4q_trend` is not an S1
ranking input, so X-FM is orthogonal as a screen. It is in §07's dossier fixed core and
S1's rubric disqualifies "Revenue declining while margins improve", so it is not
orthogonal to what the researcher already weighs. That is a limit on what a promotion
would buy rather than a reason not to register it: screens decide what the researcher
sees and rubrics decide what it does with it, and those are different components
[INVARIANT 7].

### 7.4 Every input confirmed against the schema

Checked in the migrations rather than in `SCHEMA.md`, which states its column lists are
"the load-bearing ones, not exhaustive".

| Input | Where | Status |
|---|---|---|
| `fundamental_snapshot.shares_outstanding` | `0002_statement_fields_and_grains.sql:75` | Present |
| `fundamental_snapshot.net_income` | `0002:58` | Present |
| `fundamental_snapshot.cash_from_operating` | `0002:65` | Present |
| `fundamental_snapshot.total_revenue` | `0002:52` | Present |
| `fundamental_snapshot.cost_of_revenue` | `0002:53` | Present, and unused by the family as reduced in §7.3 |
| `fundamental_snapshot.operating_income` | `0002:55` | Present, and not substitutable for EBIT [D-82] |
| `valuation_daily.accruals` | `0001:191-207`, typed at `SCHEMA.md` §Types | Present and computed |
| `valuation_daily.share_count_change` | same | Present and computed |
| `valuation_daily.revenue_growth_4q_trend` | same | Present and computed |
| `events.event_type`, `event_date` | `0001:152-160` | Present |
| `price_daily.adj_close` | `0001:50-59` | Present |
| `events.announced_date` | `0001:157` | **Present and null for earnings.** See §7.5 |

**Three of the four members need no new column, no new ingest and no new compute.**
X-NSI, X-ACC and X-FM each rank on a `valuation_daily` column that C09 already writes
and C11 already percentiles. Their entire cost is a config row and their share of §9.

### 7.5 X-PEAD's registration is a fork, not a finding

Its input has no backfillable history, and that is a fact about the ingest rather than
about the screen.

- `events.earnings_backward_days` is **7**, verified consumer EventsIngestor
  [`CONFIG_REFERENCE.md:79`, `EventsIngestor.cs:60`]. The events store reaches seven
  days into the past, so there is no multi-year earnings history to backfill against.
- `announced_date` is null for earnings, because `calendar/earnings` sends none
  [`BUILD_PLAN.md` carried obligations, 1 to 5]. `events` has no first-seen column, so
  a backfilled row and a live-accumulated one are indistinguishable and a rescheduled
  report overwrites its old date without trace.

So §5.1's condition fails for X-PEAD and only for X-PEAD. Its backfilled distribution
would come from a different population than its live one, which is precisely what D-58
removed short interest for and what D-69 is open on for `inst_ownership_change`. This
is the third instance of that pattern in this system and the first one found before
anything was built on it.

**The fork, and it carries its own decision number because it is a different question
from the other three.**

- **Fork A, register X-PEAD as a shadow with live accumulation only.** It has no floor
  and no record for its first 250 sessions, then accumulates normally. Its promotion
  clock starts 250 sessions after the other three and it is never comparable to them on
  a backfilled window.
- **Fork B, widen `events.earnings_backward_days` and backfill earnings history first.**
  That is an ingest change owed to phase 3 or later, it does not fix `announced_date`,
  and phase 5 already owns the question of whether a backfilled `events` row is
  point-in-time correct at all.
- **Fork C, do not register X-PEAD.** The family is three, all of whose inputs are
  computed today, and the anomaly is revisited when `events` can support it.

Not chosen here. It is a decision and `CLAUDE.md` §13 makes it a human's.

### 7.6 Which live screens have no successor, and what that means

**S1 has two extractions and no successor. S2, S3, S4 and S5 have neither.**

Four of the five live seats are uncovered, and naming that is part of the design rather
than an omission in it. The family is entirely additions and extractions, so it does
not replace any mechanism. It only adds mechanisms and sharpens one.

**A retirement on an uncovered axis is a design decision rather than a swap.** Retiring
S2 removes trend from this system; nothing in the family measures trend. Retiring S3
removes the only screen that reads attention against a ticker's own baseline, which
D-12 calls structurally anti-megacap. Retiring S5 removes the only screen that looks at
names that have fallen. §8.3's arithmetic says the pool survives the first such
retirement and blocks the second, which is a constraint and not a judgment.

**D-69 is where this arrives first, and it arrives from data rather than from
performance.** D-69 is open on whether S4 survives an unbackfillable institutional
ownership source, it is owed to phase 4, and this document is due before phase 3. If S4
goes under D-69 it goes with no successor in this family, so the flow axis is uncovered
from that moment. That is worth knowing while D-69 is still open rather than after.

### 7.7 Two mechanisms excluded deliberately

**Low volatility.** Documented, derivable from `atr_pct` which already exists, and
orthogonal to all five. It is excluded because it tilts large, which is the opposite of
the structural job §20 assigns this part of the design. A screen whose known property is
a megacap tilt, registered into a system whose single named risk is convergence onto
megacaps, is a cost paid against the design's own purpose.

**Neglect measured by turnover.** Excluded as a conditioning variable rather than as a
screen. It says where other effects are stronger rather than which names to buy, so its
natural use is as an interaction inside another screen's ranking and not as a screen
with slots of its own.

---

## 8. The correction, and the slot arithmetic

### 8.1 D-7's 2/3/3 restated as a proportion, and why that is not a change to D-7

D-7 gives a ceiling of eight slots per screen under a 2 large, 3 mid, 3 small quota. The
tuner has been able to move a screen to any count from four to twelve since it was
designed [D-43], and 2/3/3 is defined at eight alone. **That gap is pre-existing and is
not created by retirement.** It is stated here because retirement makes it reachable
more often, not because retirement causes it.

The proportion, which reproduces 2/3/3 exactly at eight and is integral at every count
from four to twelve:

```
large = floor(slots / 4)
rest  = slots - large
mid   = floor(rest / 2)
small = rest - mid          (the extra goes to small)
```

| Slots | Large | Mid | Small | Large share |
|---|---|---|---|---|
| 4 | 1 | 1 | 2 | 25.0% |
| 5 | 1 | 2 | 2 | 20.0% |
| 6 | 1 | 2 | 3 | 16.7% |
| 7 | 1 | 3 | 3 | 14.3% |
| **8** | **2** | **3** | **3** | **25.0%** |
| 9 | 2 | 3 | 4 | 22.2% |
| 10 | 2 | 4 | 4 | 20.0% |
| 11 | 2 | 4 | 5 | 18.2% |
| 12 | 3 | 4 | 5 | 25.0% |

**Two properties, and both are why this proportion rather than another.**

The large share never exceeds 25 percent at any count, so the sum of `floor(s/4)` over
any allocation summing to 40 is at most 10. **D-7's megacap bound of ten of forty holds
at every live-screen count**, rather than only at five screens of eight.

The guaranteed small-cap places are minimised at exactly today's configuration.
`small/slots` is lowest at eight slots, so five screens of eight gives 15, and every
other feasible allocation gives 16 to 20. **D-7's small-cap floor of fifteen is a floor
across the whole reachable space** rather than a figure that happens to hold today.

### 8.2 The correction

**The tuner's existing shrinkage does not do this job, and saying so is the answer to
the question rather than a dodge.** D-43's shrinkage is 0.8 old slots to 0.2 implied
slots. It shrinks the **action** toward the status quo. A multiplicity correction has to
shrink the **estimate** toward a prior, and a shadow has no old slot count to be shrunk
toward. Applying D-43's weights to a shadow is arithmetic on two quantities that are not
the ones the formula is about.

**What is proposed instead acknowledges that k candidates were run and has no free
parameter.**

The promotion bar is stated against a reference, and the margin it must clear is the
expected maximum of k draws:

```
required margin = sqrt(2 * ln(k)) standard errors
```

**It is one rule, and the two families differ only in what k counts and which standard
error is used.**

| | Reference the bar is stated against | k counts | Standard error |
|---|---|---|---|
| **Addition** | The live family's mean 21-day peer-relative alpha over the same window at the same depth | Eligible additions at that evaluation | Of the addition's own estimate |
| **Successor or extraction** | Zero, the paired difference against its named incumbent | Eligible paired shadows at that evaluation | Of the paired difference |

**Eligible, not registered, and the distinction is the whole of what k means.** A
multiplicity correction is for the tests that were run and would have been acted on. An
addition below §6.2's sample floor could not have been promoted whatever it did, so it
was never one of them, and counting it would penalise the screens that were actually in
contention for the existence of one that was not. **k is the count of screens of that
kind meeting §6.2's floor at that evaluation**, read off `screen_evaluation`'s
`observations_prospective` rather than off the registry.

It is not gameable in either direction. The floor is fixed in `config_rows` and cannot
be lowered to admit a rival or raised to exclude one without a version that says so, and
eligibility is a recorded fact per evaluation rather than an assertion made at the
boundary. Registering a screen and leaving it below the floor buys nothing, since it
raises no bar and can win nothing.

| k | Margin, standard errors |
|---|---|
| 1 | 0.00 |
| 2 | 1.18 |
| 3 | 1.48 |
| 4 | 1.67 |

**Pairing removes the selection within a test, not the selection across tests.** That is
why the paired family carries the correction too, and it was the first reading here that
it did not. Running k paired tests and promoting whichever wins selects the maximum of
k, exactly as running k additions does. What pairing buys is a smaller standard error,
which is §6.2's floor, and a smaller standard error makes the same margin easier to
clear rather than unnecessary.

`sqrt(2 ln k)` is the expected maximum of k independent standard normal draws, which is
the exact statement of "the best of four looks better than four independent things". It
rises with k rather than being a fixed penalty, so registering more candidates raises the
bar for all of them, which is the incentive the correction is for. And it needs only a
standard error, which the tuner must compute in order to shrink anything at all.

**It is inert at k = 1 in both families, and that is correct in both.** One addition run
alone is not a selection from a set and needs no correction; one paired shadow run alone
is the same statement about the same quantity. At the family as registered, two
extractions and two additions, the margin is 1.18 standard errors on each side, because
k is counted within a family rather than across the registry: an addition and an
extraction are not competing for the same seat, and pooling them would penalise each for
the other's existence.

**So D-90 moves the additions bar, and under eligibility it moves it about a year later
than registration.** X-FM and X-PEAD are the two additions. Under fork A, X-PEAD cannot
rank until it has a trailing distribution to draw D-9's floor from, so it surfaces
nothing and accumulates no prospective observations for its first 250 sessions, and
reaches §6.2's floor roughly a year behind X-FM. Registering it therefore costs X-FM
nothing over that year, because k counts eligible additions and X-PEAD is not one; the
bar rises to 1.18 at the evaluation where both are eligible, which is the first
evaluation at which there is genuinely a selection of two. Fork C leaves k at one
indefinitely and the addition bar inert, which is the rule behaving correctly rather
than a hole in it.

**Not a sign alone**, for the reason §6.7 already gives. A promotion costs a rubric, a
changed prefix hash and a bundled break in the primary claim's comparability, so it must
not fire on an edge that noise would produce. A bar of "the paired difference is
positive" fires at one standard error of nothing about half the time it is asked.

### 8.3 Where the quota system stops working

The pool is fixed at 40. `tuner.slot_floor` is 4 and `tuner.slot_cap` is 12
[`CONFIG_REFERENCE.md:292-293`]. So n live screens can sum to 40 only when
`4n <= 40 <= 12n`.

| Live screens | Reachable range | 40 reachable |
|---|---|---|
| 3 | 12 to 36 | **No** |
| 4 | 16 to 48 | Yes |
| 5 | 20 to 60 | Yes |
| 10 | 40 to 120 | Yes, at the floor exactly |
| 11 | 44 to 132 | **No** |

**Feasible live-screen range is four to ten, and both ends are reachable**: ten screens
at the floor of four, four screens averaging ten.

Two bounds follow, and both are computed from existing keys rather than stored:

- **A retirement leaving fewer than `ceil(40 / tuner.slot_cap)` live screens, which is
  four, does not execute.** It becomes a design decision requiring a promotion in the
  same boundary, or an authored change to the pool size, the cap or the floor.
- **A promotion taking the count above `floor(40 / tuner.slot_floor)`, which is ten,
  does not execute** for the same reason in the other direction.

### 8.4 What happens to a retiring screen's allocation

**The slots return to the pool and the tuner redistributes at the next monthly run, with
the floor and the cap untouched.** No special case, no held-back allocation, no
proportional carve-up written for this.

The simplest rule survives the arithmetic check above, which is why it is proposed:
between four and ten live screens the tuner's ordinary constrained redistribution can
always reach 40, so a returning allocation is a normal monthly move rather than an
exception. At three it cannot reach 40 at all, and §8.3 blocks the retirement before the
tuner is ever asked.

A promoted screen enters at `tuner.slot_floor`, which is four, and the tuner moves it
from there on its own record. Entering at the floor rather than at an average keeps the
promotion's cost bounded while its prospective record is still the shortest in the
family.

---

## 9. The database consequence

### 9.1 `screen_score_daily`

The rule rather than a count: **rows scale linearly with the number of screens whose
state is `live` or `shadow`**, because every such screen scores every ticker every day.
`SCHEMA.md` puts the table at ~1.4 GB after a five-year backfill at five screens, and
states that sizes are estimates rather than measurements.

| Registered and backfilled | Estimate | Delta |
|---|---|---|
| 5, today | ~1.4 GB | |
| 8, family of three with X-PEAD forked out | ~2.2 GB | +~0.8 GB |
| 9, family of four | ~2.5 GB | +~1.1 GB |

Annual growth moves with it, from about 700 MB a year across the whole store to roughly
930 MB at nine screens. `SCHEMA.md`'s own Totals section already names this table and
"adding a sixth screen" as the thing that moves these numbers in a way nothing else in
the design does, so the direction is anticipated and the magnitude is what is new.

### 9.2 `attribution`

Bounded rather than estimated. Each shadow contributes at most `tuner.slot_cap` rows a
night before deduplication against the candidate set and against the other shadows, so
four shadows add at most 48 rows a night on top of roughly 28 candidates. Over about 250
sessions a year that is at most 12,000 additional rows against roughly 7,000 today, so
the table holds at most about 19,000 rows a year and at most about 24 MB after a
five-year backfill against 9 MB today.

**`VALIDITY.md` §3's counts are unchanged in meaning.** The ~7,000 candidates a year and
~1,400 per screen are counts of `surfaced_as = 'candidate'` rows, which is what the
`candidate_attribution` view selects.

### 9.3 The read pattern, which matters more than the size

`screen_score_daily`'s primary key is `(ticker, screen_id, date)`
[`0001_snapshot.sql:239`] and the table carries no other index. The nightly read is
every score for one date, so the leading column of the only index is the one the query
does not constrain.

**It is a problem, shadows amplify it rather than cause it, and it lands in phase 4.**

- **Amplify rather than cause.** The read pattern is already wrong at five screens. Nine
  multiplies the rows scanned by 1.8 and changes nothing about the shape.
- **Phase 4, not phase 3.** `BUILD_PLAN.md` phase 3 backfills ingest and compute; phase
  4 carries the screen backfill. Once a night the mismatch is affordable. Once per
  backfilled date across roughly 1,260 dates is where it is felt.
- **`ARCHITECTURE.html` §19's two-pass backfill is partly protected already.** Pass two
  computes floors "in a single window function over the now-complete score table", which
  is a sequential scan and does not want a per-date index. It is pass one's write
  ordering and the allocator's per-date read that the key order penalises.

**What would fix it**, in the order they should be tried:

1. An index on `(date, screen_id, rank_within_screen)`. It serves the allocator's per-
   date per-screen top-N read directly and it is the smallest change. Its cost is a
   second index on the largest table in the system, which is not free.
2. Declarative range partitioning by `date`. It makes the per-date read a single
   partition scan, makes the backfill's writes cheaper because each date lands in its
   own partition, and it removes the index cost. Its cost is that partitioning is a
   schema decision that is far cheaper before the table has 1.4 GB in it than after.

**The recommendation is to decide this at phase 4 and before the screen backfill runs,
not after**, because both fixes are cheap on an empty table and the second is
substantially not cheap on a full one. It is named here rather than left to be
discovered because a design that adds four screens to that table without saying so is
handing phase 4 a problem it did not make.

---

## 10. Config keys this document introduces

Every one is a value that could plausibly be tuned and none is a literal at a call site
[`CLAUDE.md` §8]. Consumers are stated as proposed and are `NOT BOUND` until a phase
wires them, per `CONFIG_REFERENCE.md`'s own rule that an unverified entry is worse than
an absent one.

| Key | Default | Proposed consumer | Why it is a key |
|---|---|---|---|
| `screens.<id>.state` | `live` for S1 to S5 | ScreenEngine, CandidateAllocator | The registration itself. Three values, and the whole of C13's behavioural change |
| `lifecycle.min_prospective_observations` | 1000 | ScreenTuner | §6.2's floor, taken from `VALIDITY.md` §4 |
| `lifecycle.min_paired_observations` | 250 | ScreenTuner | §6.2's paired floor, which encodes `ρ = 0.75`. **DRAFT FIGURE**, and the one key here a measurement is expected to revise, which is the reason it is a versioned key rather than a constant |
| `lifecycle.consecutive_evaluations` | 3 | ScreenTuner | §6.3's sustained-fail requirement |
| `lifecycle.evaluation_horizon_days` | 21 | ScreenTuner | §6.1's horizon. A key rather than a literal so a test can assert it matches `VALIDITY.md` §4's pre-registered horizon, which is the same reason `tuner.benchmark_column` is a key |
| `lifecycle.counts_backfilled_observations` | `false` | ScreenTuner | §5.2. A key so that the prohibition is visible in configuration and a test can assert it is false, rather than being a condition somebody has to remember not to relax |

**No key is introduced for the slot proportion in §8.1**, because it is arithmetic over
`screens.<id>.slots` and introduces no value. `screens.quota_large`, `quota_mid` and
`quota_small` at 2, 3 and 3 [`CONFIG_REFERENCE.md:201-203`] are the proportion's output
at eight slots, and whether they stay as keys, become the proportion's inputs, or are
retired is a phase 4 question this document does not close.

**No key is introduced for the multiplicity correction**, because `sqrt(2 ln k)` has no
parameter and k is counted off `screen_evaluation`, within a family rather than across
it, from rows that met `lifecycle.min_prospective_observations` or
`lifecycle.min_paired_observations`. The correction has no threshold of its own for the
same reason D-9's floor has none: it is defined against a quantity the system already
records.

---

## 11. The decisions, authored

**D-84 to D-89 are in `DECISIONS.md` under Screen lifecycle, and D-90 is in its Open
section.** Authored 2026-08-11 from the text this section drafted, human-directed and
before `ARCHITECTURE.html` was touched, because the cells cite them [`CLAUDE.md` §13].

**The drafted bodies are not repeated here.** The register is the copy, and a decision
stated twice is a decision that can disagree with itself, which is the defect D-76, D-77
and D-83 each removed from this corpus. What follows is the index, so that a reader of
this document knows which decision carries which part of it and where the reasoning
went.

| Decision | Status | The rule | Worked out in |
|---|---|---|---|
| **D-84** | `ACTIVE` | A screen has three states and a shadow screen is one of them | §1, §2 |
| **D-85** | `ACTIVE` | Shadow screens write `attribution` and never `candidate_set` | §4 |
| **D-86** | `ACTIVE` | Backfilled observations count toward a shadow's distribution and never toward a promotion or a retirement | §5 |
| **D-87** | `ACTIVE` | A screen is retired on sustained peer-relative underperformance, and the tuner is what measures it | §6, §8.2 |
| **D-88** | `ACTIVE` | A promotion or a retirement splits the primary claim, so none executes before the claim has its sample and all due are bundled into one boundary | §6.7 |
| **D-89** | `ACTIVE` | The slot pool stays at 40, D-7's 2/3/3 is restated as a proportion, and the feasible live-screen range is four to ten | §8.1, §8.3, §8.4 |
| **D-90** | `OPEN` | Whether post-earnings drift is registered, and on what history | §7.5 |

**Where this document and the register differ, the register is right.** This one states
the design and shows the arithmetic; that one states the rule and the reason. The
sections above are the working, not a second copy of the ruling.

---

## 12. The `ARCHITECTURE.html` edits, applied

`ARCHITECTURE.html` is human-edited only [`CLAUDE.md` §13]. These were applied
human-directed, on 2026-08-11, after D-84 to D-90 were authored and not before, because
an edit citing an unauthored decision is a document constraining the code on the
strength of something that does not exist.

Under D-73 they are clean edits: the superseded text is deleted, the decision citation
is kept at the point of change, and the prior wording is recorded in `CHANGELOG.md`,
which carries one entry per decision. Under D-83, a count is replaced by the rule that
governs it rather than by a new count.

**This section is kept as the specification of what changed and why**, with each edit's
prior text beside its replacement. `CHANGELOG.md` records the removals as the corpus
log; this records the reasoning that chose each one, which is what a later reader asking
why a cell reads as it does will want. The line numbers are those of HEAD `2cad4cc`,
before these edits moved them.

### 12.1 §03, C13 ScreenEngine

Line 204. Current text of the `muted` span:

> Runs the five screens from config. Maintains each screen's trailing 250-day
> distribution for its floor

Replacement:

> Runs every screen whose state is `live` or `shadow`, from config <span class="rmv">D-84</span>. Maintains each screen's trailing 250-day distribution for its floor

The second sentence is unchanged and is quoted so the cell reads as it did. The first
half is the whole of C13's change: it reads a flag and does nothing else differently.

### 12.2 §03, C14 CandidateAllocator

Line 205. Current text of the `muted` span:

> Size quota per screen, no backfill, dedup across screens, writes attribution

Replacement:

> Size quota per screen, no backfill, dedup across screens <span class="rmv">D-84, D-85</span>. Writes `candidate_set` from the live screens only, and one `attribution` row per name any registered screen surfaced, labelled `candidate` or `shadow`. A shadow is recorded to `tuner.slot_cap` under the same quota

This cell carries the load, because it is where the meaning of an attribution row
changes. It now says what it writes a row for, and it says what the candidate set is
drawn from, and those are two different populations for the first time.

### 12.3 §03, C22 ScreenTuner

Line 214. Current text of the `muted` span:

> Reallocates the 40 slots between screens on peer-relative hit rate and alpha, shrunk,
> floor 4 and cap 12

And the Writes cell, currently `config_rows`.

Replacement span:

> Measures every registered screen on peer-relative hit rate and alpha, shrunk, and reallocates the 40 slots among the live ones, floor 4 and cap 12 <span class="rmv">D-87</span>. Nominates a promotion or a retirement; executes neither <span class="rmv">D-88</span>

Replacement Writes cell:

> config_rows, screen_evaluation

The 40 here is load-bearing and stays. §8.3's arithmetic is what makes four to ten live
screens the feasible range and the pool size is one of its three inputs, so it is a
number the design rests on rather than a count of a set [D-83].

### 12.4 §15, U4 Screens

Line 779. Current text of the Shows cell:

> The five screens with current slot allocation, fill rate, hit rate, mean peer-relative
> alpha and current floor. History of how the tuner has moved slots. Recent candidates
> per screen and how they went.

Replacement:

> Every registered screen with its state, current slot allocation, fill rate, hit rate, mean peer-relative alpha and current floor <span class="rmv">D-84</span>. Shadows show the same figures with no allocation, their prospective and backfilled observation counts separated, and any standing nomination <span class="rmv">D-86, D-87</span>. History of how the tuner has moved slots. Recent candidates per screen and how they went.

And the Reads cell, currently `config_rows, screen_history, attribution`, becomes:

> config_rows, screen_history, attribution, screen_evaluation

This is the one screen that reads `attribution` rather than `candidate_attribution` and
means it. §15's density rule one, that no number appears without its comparison and its
sample size, is why the observation counts are separated rather than summed: a shadow's
backfilled count and its prospective count are the two numbers §5 makes mean different
things.

### 12.5 §16, the store matrix

Line 837, `attribution`, grain cell:

> ticker × day surfaced

**The grain description survives and needs no edit.** The primary key is
`(ticker, date)` and a row is still one ticker on one day it was surfaced. What did not
survive is the implicit reading that "surfaced" meant "became a candidate", and that
reading lives in `SCHEMA.md`'s attribution section and in the `surfaced_as` column
rather than in this cell. §16's own note says this table states grain and size and
nothing about access, so stating the population here would be the third statement of a
fact D-76 removed from these columns once already.

**Two rows are added**, in the order the table already uses, which groups by layer:

| Store | Grain | After backfill |
|---|---|---|
| `screen_evaluation` <span class="tag new">NEW</span> | screen × evaluation date | tiny |

placed with the Learn stores beside `calibration`.

**The `screen_score_daily` size cell states the rate rather than a total, and that is
D-83 rather than a dodge.** It read `1.4 GB` against a grain of `ticker × screen × day`,
which is a figure that assumes five screens without saying so, and the number the family
implies is not knowable while D-90 is open: 2.2 GB at eight registered screens, 2.5 GB
at nine. Writing either would be a count that goes stale on the decision that is
explicitly still to be taken. The cell now reads `~280 MB per registered screen`, which
reproduces 1.4 GB at five, moves correctly at any count, and is the rule §9.1 states.
The grain cell reads `ticker × registered screen × day` for the same reason. The tfoot
total keeps its 5 GB, now stated as being at five registered screens, and gains what a
further one costs.

### 12.6 §06, the attribution grain card

Line 359. Not on the brief's list, and included because it is the same defect in a
section the C14 edit already touches. Current text:

> Around 7,000 rows a year, each tagged with its screen. Roughly 1,400 per screen, which
> is enough to say something about each within a year.

D-83 applies: two counts stated in prose that nothing checks, and both change the moment
a shadow is registered. The rule that governs the set, rather than a new pair of counts:

> One row per name any registered screen surfaced, tagged with every screen that surfaced it and labelled `candidate` or `shadow` <span class="rmv">D-85</span>. The candidate rows are the population `VALIDITY.md` §3 counts and reads its per-screen power from.

### 12.7 The nine occurrences of "five screens", which are eleven

**The brief's count of nine is a count nothing had checked, which is D-83's own defect.**
`grep -c "five screens"` returns 9. A case-insensitive, whitespace-tolerant sweep returns
11: `perl -0777 -ne 'while(/five\s+screens/gi){$c++} print $c'` over
`docs/ARCHITECTURE.html` returns 11. The two the case-sensitive count misses are lines
252 and 281, both of which begin a sentence with "Five screens".

Every one is audited below. **Live** means the count of screens holding slots and moves
on promotion or retirement. **Design's five** means a statement about the design as
built, which stays true of that moment whatever the registry later holds. **Incidental**
means the number carries no weight in the sentence and D-83 says to state the rule.

**All eleven are resolved and nine changed.** The two that did not are lines 357 and
358, whose sentences were rewritten to state a general property and which name five
screens of eight as the configuration that property is tightest at. That is the phrase
surviving inside a stronger claim rather than the audit missing it, and it is recorded
so a later whitespace-tolerant sweep returning two is read as this and not as a
regression.

| Line | Text, abbreviated | Sense | Action |
|---|---|---|---|
| 170 | "Three of the five screens need company fundamentals that a fund does not have" | **Incidental.** The universe criterion is that funds are excluded; how many screens need fundamentals is illustration | D-83. "Screens that rank on company fundamentals need a company behind the ticker" |
| 204 | C13, "Runs the five screens from config" | **Live, and wrong after D-84** because it must run shadows too | §12.1 replaces it |
| 252 | §04 figure 2, C13 node, "Five screens, each with its own ranking and its own floor" | **Live** | "Every registered screen, each with its own ranking and its own floor" |
| 281 | §05 opening, "Five screens, each with its own metric set, each blind to the others" | **Live**, and it is the sentence INVARIANT 2 is stated in | "Screens, each with its own metric set, each blind to the others." The sentence's subject is blindness and the count adds nothing to it |
| 284 | Figure 3 caption, "five screens running in parallel off one percentile store" | **Live** | "screens running in parallel off one percentile store" |
| 347 | §06 dedup node, "Union across the five screens" | **Live**, and specifically the live ones, since a shadow is not deduplicated into `candidate_set` | "Union across the live screens" |
| 357 | Megacap bound card, "Two large slots across five screens is 10 of a possible 40, or 25 percent" | **Design's five, and load-bearing.** §8.1 shows the bound of ten of forty holds at every count from four to ten, which is a stronger claim than the sentence makes | Rewrite to the general claim: "The large share is at most a quarter of any screen's slots, so at most 10 of the 40, which is close to the megacap share of total US market capitalisation" [D-89] |
| 358 | Small cap floor card, "Three small slots across five screens is up to 15 guaranteed places" | **Design's five, and load-bearing.** §8.1 shows fifteen is the minimum across the whole reachable space, reached at exactly this configuration | Rewrite: "The small share is at least 15 places across the pool at every reachable allocation, and is lowest at exactly today's five screens of eight" [D-89] |
| 779 | U4, "The five screens with current slot allocation" | **Live** | §12.4 replaces it |
| 847 | §16 note, "Every ticker is scored by all five screens every day... That is five rows per ticker per day rather than one" | **Live**, twice in one sentence, and it is the sizing argument | "Every ticker is scored by every registered screen every day... That is one row per ticker per day per registered screen rather than one" [D-84]. §9.1's rule is this sentence |
| 882 | §18, "All five screens return zero" | **Live** | "Every live screen returns zero". A shadow returning zero is not a data fault worth halting a run over, because it surfaces nothing either way |

### 12.8 The three slot counts

| Line | Text | Verdict |
|---|---|---|
| 214 | C22, "Reallocates the 40 slots between screens" | **Load-bearing and stays.** The pool size is one of the three inputs to §8.3's feasible range. §12.3 changes the sentence around it and keeps the 40 |
| 703 | §13 figure 10, C22 node, "Reallocates the 40 slots on peer-relative hit rate and alpha, shrunk 0.8 old and 0.2 implied, floor 4 and cap 12" | **Load-bearing and stays.** Add the allocation filter: "...among the live screens..." [D-87] |
| 726 | §13 note, "the quotas guarantee up to fifteen of forty slots to small caps" | **Load-bearing and stays, and §8.1 strengthens it** from a property of today's configuration to a property of every reachable one. Optional footnote rather than a required edit |

**Not on the brief's list and worth a human's eye.** Six further occurrences count the
same set by another name and a promotion or a retirement moves all of them: line 121's
"Five independent screens" in the lede, line 261's "all five rubrics", line 335's "five
ranked lists", line 372's "Five screen rubrics at roughly 600 tokens each", line 374's
heading "The five rubrics", and line 470's prefix node "Five rubrics". They are listed
rather than drafted because the rubric count is the reason D-88 exists: the prefix is
byte-identical within a night [INVARIANT 6] and its token budget is costed at ~4,600 in
§07 and §17, so changing the number of rubrics is the boundary D-88 defers, and these
six sentences are where a reader would find that out.

### 12.9 Where the narrative goes

**The rule gets one home, in §13 The learning loops, and §05 and §06 get one sentence
each in the component descriptions they already carry.**

The argument. Shadow running is C13's, the attribution write is C14's, and the
evaluation, the nomination and the boundary are C22's. A single new section describing
all three would restate what three sections already open up, which is the shape D-76,
D-77 and D-83 each removed. But splitting the **rule** across three sections leaves a
reader assembling it, and the rule is one thing.

So: the rule sits beside the component that computes it, which is C22 in §13; §05 and
§06 say what their own component does and point at it. One statement of the rule, two
statements of behaviour, no duplication.

**Rejected: a new section 21.** §20 is a closing reflection on where convergence kept
trying to reappear, and a mechanism section after it reads as an appendix. **Rejected:
§06.** The attribution change is the largest single consequence and it is tempting to
put the rule there, but the allocator does not evaluate anything and a rule stated
beside a component that does not apply it is where a rule goes stale.

Drafted prose, to be inserted in §13 after the "Why slot reallocation rather than weight
tuning" note and before "Three rules the filler needs from day one":

> **`<h3>` Screen lifecycle: shadow, promotion and retirement**
>
> A screen enters this system as a shadow. It is scored exactly like a live screen,
> writes `screen_score_daily` and `screen_history`, inherits the same 98th percentile
> floor, and is allocated no slots. It writes an attribution row for every name it
> surfaces to the depth of the slot cap, so its record is built by the same allocator and
> filled by the same forward return filler as a live screen's, and it writes nothing to
> `candidate_set`. Nothing it does can reach a portfolio.
>
> That is the whole point of the state. A screen registered live is a screen chosen by
> argument; a screen promoted from shadow is one chosen on a record it built without
> being able to affect anything.
>
> **There are two kinds of shadow and they are judged differently.** A successor sits on
> the same axis as a named live screen and is judged head to head against it, over the
> same names on the same dates at the same depth, which needs far fewer observations than
> judging a screen against nothing. A successor differs by mechanism and not by
> parameter: trend measured on price against trend measured on earnings revisions is a
> mechanism, while the same trend over a different lookback is a parameter, and parameter
> variants are what the tuner already does with slots. An addition is orthogonal to every
> live screen and is judged against the family mean instead.
>
> **Both kinds carry the same correction for the fact that several were run at once**,
> because the best of four looks better than four independent things and that is as true
> of four paired tests as of four unpaired ones. Pairing buys a smaller standard error,
> and a smaller standard error makes the margin easier to clear rather than unnecessary.
> The margin is inert where only one candidate of a kind is under evaluation, which is
> correct: one candidate is not a selection from a set.
>
> **The measure is peer-relative return at 21 days and never absolute alpha**, for
> INVARIANT 5's reason: against a large-cap index every small-cap candidate posts
> negative alpha regardless of how well it was chosen, and a rule that can retire a
> screen is more destructive than one that can cut its slots. Twenty-one days because it
> is the horizon the primary claim is pre-registered at, and because the 40-day time stop
> means an edge appearing only at 63 days is one this portfolio cannot hold to.
>
> **Backfilled observations count toward a shadow's distribution and never toward a
> promotion or a retirement.** A shadow needs the backfill or it has no floor for its
> first 250 sessions. But four years of history arrive at once when the screen backfill
> runs, so a promotion counting them would be decided on registration day, and a screen
> that works in backfill and fails live is the most common way this kind of thing fails.
>
> **A retirement or a promotion is nominated by the tuner and executed by an operator, at
> a boundary.** Nomination needs a sustained fail across three consecutive monthly
> evaluations over a sample that has reached its floor, so one bad quarter cannot retire
> anything. Execution waits, because the set of screens is the set of rubrics, so a
> change to it changes the dossier and splits the primary claim rather than only the
> screen's own record. Every nomination due at that point is bundled into one boundary,
> since four changes made one at a time produce five segments of history where one change
> produces two.
>
> **Some axes have no successor, and a retirement there is a design decision rather than
> a swap.** Retiring a screen whose mechanism nothing else in the registry measures
> removes that mechanism from the system. The slots return to the pool and the tuner
> redistributes them; the mechanism does not come back. And the arithmetic bounds it:
> with a pool of forty, a floor of four and a cap of twelve, the pool can only be spent
> by four to ten live screens, so the first such retirement is a judgment and the second
> is refused.

And in §05, appended to the opening paragraph after "so a sixth screen is a config row
and not a deployment":

> A screen's config row also carries its state, so a screen can be registered and scored
> without being allocated any slots. Section 13 has the lifecycle.

And in §06, appended to the "The attribution write happens here, not later" note:

> A shadow screen's names are written here too, labelled as shadow and absent from the
> candidate set, so a candidate screen accumulates the same record by the same code as a
> live one. Section 13 has the lifecycle.

---

## 13. The `CHANGELOG.md` entries, written

**Seven entries, one per decision, at `2026-08-11`.** They are in `CHANGELOG.md` and are
not repeated here, for the reason §11 gives: the log is the record, and a second copy of
a removal is a removal that can disagree with itself.

The set opens with a shared entry, `2026-08-11, D-84 to D-90, the screen lifecycle`,
carrying what the pass was, where the narrative went and why, the eleven-against-nine
audit, the three slot counts, and what was deliberately not touched. Then one entry per
decision, each quoting verbatim the prior wording of the edits that decision drove.

Three of the seven remove nothing and are written anyway. D-86 and D-88 are rules that
arrive as new prose, and D-90 is `OPEN` and changes no spec at all. An entry a reader
cannot find is the same as no entry, and a decision authored on a date is a corpus change
whether or not it deleted a sentence.

**`SCHEMA.md` gets no entry yet and that is deliberate.** D-85's `surfaced_as` and D-87's
`screen_evaluation` are declarations `guards.ps1` and `SchemaParityTests` hold against the
migrations and the live database in both directions. Declaring either before its migration
exists fails a check for a column that is correctly absent, so both are carried obligations
against the phase that migrates them, and the `SCHEMA.md` entry is written in that
checkpoint.

---

## 14. What this document does not decide

**The screens themselves.** Ranking directions, weights and the score function for each
family member are `screens.*` config rows and phase 4 authors them [D-6, INVARIANT 2].
The carried obligation from phase 2 to phase 4 about `_inv` naming a direction rather
than a column applies to X-NSI and X-ACC directly, since both rank on a metric S1 reads
inverted.

**Whether X-PEAD is registered.** D-90 is drafted `OPEN` and §7.5 has the three forks.

**Whether `screens.quota_large`, `quota_mid` and `quota_small` survive** as keys once
§8.1's proportion is the rule. They are the proportion's output at eight slots. Phase 4
decides whether they become inputs, stay as a check on the proportion, or go.

**The index or partitioning choice on `screen_score_daily`.** §9.3 states the problem,
that shadows amplify rather than cause it, that it lands in phase 4, and the two fixes
in the order to try them. Which one is a phase 4 decision taken with a measurement,
which is exactly the kind of decision this document must not take without one.

**Anything about how a boolean enters a screen score.** `base_breakout_flag` is carried
from phase 2 to phase 4 and no family member reads it, so it is untouched here and stays
open.

**D-69.** §7.6 states what an S4 retirement would mean given the family has no cover for
the flow axis. It does not answer D-69, which is a phase 4 decision on its own terms.

**What `ρ` actually is.** §6.2's paired floor of 250 encodes `ρ = 0.75` and says so. The
realised correlation between a shadow's per-observation peer-relative return and its
incumbent's is a measurement, and this document takes none. It states which measurement
would revise the floor, why taking it is sizing the instrument rather than choosing the
answer, and what would make the same measurement result-shopping instead.
