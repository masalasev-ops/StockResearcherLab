# Shadow screens and screen retirement — the design brief

**Target** the screen lifecycle: registration, shadow running, promotion, retirement.
Owed to phase 4, which builds the mechanism, and due before phase 3, which creates the
history a candidate screen could otherwise be selected on.
**Authored** 2026-08-11, ahead of phase 3's checkpoints rather than at a sign-off,
because the timing is the point [`CLAUDE.md` §11].
**Read against** `ARCHITECTURE.html` §02, §03, §05, §06, §10, §12, §13, §16, §18, §19
and §20, `SCHEMA.md`, `DECISIONS.md` D-1 to D-83, `VALIDITY.md` and the migrations
`0001` to `0005`, all at HEAD `2cad4cc`.
**Status** `DRAFT`. Everything below is scope and questions. The decisions it produces
are authored into `DECISIONS.md` by a human and the `ARCHITECTURE.html` edits are
applied by a human, not from here [`CLAUDE.md` §13].

**This is the only copy, and it is here rather than in `prompts/spent/` because the work
has not been done yet.** `CLAUDE.md` §3 archives a prompt to `spent/` after the code, as
implemented. A copy was filed there first, with status `ISSUED` on the reading that the
directory holds every prompt issued whatever its state, and it was removed once §3 was
corrected. It goes to `spent/` verbatim when `docs/SCREEN_LIFECYCLE.md` lands, and this
file is what it is archived from, so the body below is kept as issued and is not reworded.
§"What was settled" at the foot is the only part that is not the brief, and it is the part
that does not travel to the archive.

**`prompts/README.md` does not describe this directory.** It opens "Two different kinds
of thing live here and they follow opposite rules" and `BuildPlans/` is a third. Carried
from the phase 2 plan, still open, and reported rather than edited because that README is
authored prose stating a rule [`BUILD_PLAN.md` carried obligations, 2 to the next
authored pass on `prompts/README.md`].

---

## The brief as issued

Design only. Nothing is built and no decision is authored here. The deliverable is
a draft for authoring, in the shape METRICS.md took at 2.1: a document that states
the rules, plus decision text ready for a human to author into DECISIONS.md.

**Why this happens before phase 3 rather than inside phase 4.** The mechanism is
phase 4 work, because that is when screens exist. The decisions are due now,
because the history a candidate screen could be selected on comes into existence
when phase 3 backfills. Register the family after that history is sitting there
and nothing distinguishes a screen chosen for its mechanism from one chosen
because it worked on the data. Nothing in this design is chosen against a
measurement, and no measurement exists to choose against, which is the point
[`CLAUDE.md` §11].

### What a shadow screen is, and what changes to make one possible

A shadow screen is scored like a live screen and allocated no slots. It writes to
`screen_score_daily`, accumulates a trailing distribution in `screen_history`, and
inherits D-9's floor, the 98th percentile of its own trailing distribution. It has
no threshold of its own to register.

Most of this exists. `screen_score_daily` is already ticker by screen by day and
already scores every ticker rather than a shortlist, which is why it is the largest
table. `screen_history` is already keyed on screen. Establish what actually has to
change and state it precisely:

- how a screen is registered as live or shadow, and where that registration lives
- what the screen engine does differently, which should be reading a flag and
  nothing else
- what the candidate assembler does differently

**The part that needs the most care.** If the assembler writes an attribution row
for every name any screen surfaced, then attribution stops meaning "the candidate
set" and starts meaning "everything any screen surfaced". That is a change to what
an existing table means, and this project's characteristic defect is a reader that
was not found.

Enumerate every reader of `attribution`, current and planned across every later
phase, and say for each what it does with a shadow row. Where a reader means
"candidate", say what the filter is. Where a reader would be wrong without a
filter, say so plainly rather than assuming the filter will be remembered.

Consider and cost the alternative, a separate shadow store, and say why it is
rejected if it is. The argument to weigh it against: attribution already stores the
size bucket, sector and regime as they were on the day, and the forward return
filler already fills every row it finds, so a shadow's returns would be computed by
the same code at the same time as a live screen's rather than by a second path that
has to agree with the first.

### The retirement rule

State it so that it could be applied by someone who was not here.

- the measure, and why absolute alpha is not it
- the minimum sample before the question may be asked at all
- a sustained-fail requirement, so one bad quarter cannot retire a screen
- what happens on a fail

**One question decides whether this rule means anything.** Registering before
phase 3 means a shadow's sample floor is met almost immediately, because four years
of history arrive at once rather than accumulating. Backfilled history is
simulated, and a screen that works in backfill and fails live is the most common
way this kind of thing fails. Say whether backfilled observations count, and say it
separately for two purposes: a shadow's trailing distribution, which it needs
history to have at all, and a promotion or retirement decision, which may need
evidence from nights that actually happened.

### The family, and the two kinds in it

Two kinds, registered and judged differently.

**Successors** sit on the same axis as a named live screen and are judged head to
head against it. That comparison is paired, over the same names on the same dates,
so it needs far fewer observations than judging a screen against nothing. A
successor differs by mechanism, not by parameter: trend measured on price against
trend measured on earnings revisions is a mechanism; the same trend measured over
a different lookback is a parameter, and parameter variants are what the tuner
already does with slots.

**Additions** are orthogonal to all five and are judged against the family with a
correction, because the best of four looks better than four independent things.

Four additions are proposed, each with its mechanism stated as a reason it should
work before any data. Net share issuance and its dilution mirror; post-earnings
drift using the earnings-day return as the surprise proxy; accruals, meaning the
divergence between accounting earnings and cash; and fundamental momentum, meaning
revenue and margin trajectory rather than level.

Their inputs exist in the schema as merged: `shares_outstanding`; `net_income` with
`cash_from_operating`; `total_revenue`, `cost_of_revenue` and `operating_income`;
and `events` with `price_daily`. Confirm each against the schema rather than
trusting this list, and say if any is absent.

Low volatility is excluded deliberately. It is documented, derivable and orthogonal,
and it tilts large, which is the opposite of the structural job §20 assigns this
part of the design. Neglect measured by turnover is excluded as a conditioning
variable rather than a screen, since it says where other effects are stronger
rather than which names to buy.

**Say which live screens have no successor in this family and what that means.**
The four are all additions, so some seats are uncovered. Naming which, and saying
that a retirement on an uncovered axis is a design decision rather than a swap, is
part of the design rather than an omission in it.

### The correction, and the slot arithmetic

The correction need not be a p-value adjustment and probably should not be. What it
must do is acknowledge that four candidates were run. Shrinkage toward the family
mean does that and the tuner already shrinks, so propose the promotion bar stated
against the shrunk figure using machinery that exists, or say why that does not
work.

For the slots: state what happens to a retiring screen's allocation. The simplest
rule is that they return to the pool and the tuner redistributes at the next
monthly run with the floor and cap untouched. Check the arithmetic before proposing
it. Forty slots with a floor of four and a cap of twelve gives five screens a range
of twenty to sixty and four screens sixteen to forty-eight, but three screens give
twelve to thirty-six, which cannot reach forty. Say where the quota system stops
working and what the rule is at that point.

### The database consequence

State the storage delta rather than leaving it to be discovered. Four shadows take
`screen_score_daily` from five screens to nine.

And say what it does to the read pattern, which matters more than the size.
`screen_score_daily` has no index beyond its primary key and that key is ticker
first, while the nightly read is every score for one date. Once a night that is
fine. Phase 3's backfill would do it once per backfilled date. Say whether that is
a problem, whether it is one shadows cause or only amplify, and what would fix it.

### Done when

- the design document states the mechanism, the retirement rule, the family with
  each member's mechanism and inputs, the correction and the slot rule, and could be
  applied by someone who was not part of this conversation
- every reader of `attribution` is enumerated with what it does with a shadow row
- the backfill-versus-live question is answered separately for distribution and for
  promotion
- every proposed input is confirmed against the schema, and any absent one is named
- no decision is authored, no code is written, no threshold is chosen against a
  measurement, and the document says so
- decision text is drafted, ready to author, and names which decisions it would
  become

### The architecture changes this needs, drafted rather than applied

`ARCHITECTURE.html` is human-edited only [`CLAUDE.md` §13]. Draft every edit
here, in a form a human can apply without deciding anything; do not touch the file.
Under D-73 these are clean edits with the citation kept at the point of change and
the prior wording recorded in `CHANGELOG.md`, and under D-83 a count is replaced by
the rule that governs it rather than by a new count.

**Three component descriptions in §3.**

C13 ScreenEngine currently says it runs the five screens from config and maintains
each screen's trailing 250-day distribution for its floor. The second half already
does what a shadow needs and does not change. The first half does. State what it
becomes.

C14 CandidateAllocator currently says size quota per screen, no backfill, dedup
across screens, writes attribution. This is where the meaning of an attribution row
changes, so its description carries the load: say what it writes a row for and what
the candidate set is drawn from.

C22 ScreenTuner currently reallocates the forty slots between screens on
peer-relative hit rate and alpha, shrunk, with a floor of four and a cap of twelve,
reading attribution and writing config_rows.

**That third one is a design question rather than a wording change, and it needs
answering before the description can be written.** The tuner reads attribution, so
once attribution carries shadow rows it sees them, and it must not allocate slots
to a shadow. But the measure it already computes per screen, peer-relative hit rate
and alpha, is the shadow evaluation. Computing it twice in two components is the
duplication this project has spent three phases removing.

Decide and justify: does the tuner compute the measure for every registered screen
and allocate slots only among the live ones, which is one computation with a filter
on the allocation half, or does shadow evaluation belong somewhere else. If
somewhere else, say what reads what and why the duplication is worth it. If the
tuner, say whether its write set changes and where a shadow's measured performance
is stored, since `config_rows` is not a place for it.

**The store matrix in §16.** Check what it says `attribution`'s grain is and whether
that description survives a row existing for a name no portfolio ever considered.
Correct it if it does not.

**Nine occurrences of "five screens".** With a family registered, "the screens"
becomes ambiguous between five live and nine registered, and the slot arithmetic
depends on the first. Audit every one and say for each whether it means live,
registered, or the design's five as a fixed fact. Where it means live, say so.
Where the number is incidental rather than load-bearing, D-83 applies and it should
name the rule instead. "40 slots" appears twice and "forty slots" once; check those
against the same question, since the arithmetic is what breaks at three screens.

**Where the narrative goes.** Shadow screens and retirement are new concepts and
neither exists in the document. Propose which section they belong in and why, and
draft the prose. Include the retirement rule, the two kinds of shadow, the
mechanism-not-parameter line for successors, and the fact that some axes have no
successor so a retirement there is a design decision rather than a swap.

**Done when:** every edit is drafted with its exact current text, its replacement,
and the `CHANGELOG.md` entry it needs; the tuner question is answered with its
reasoning rather than left open; all nine occurrences of "five screens" and all
three slot counts are accounted for individually; the narrative section is drafted
in full and its placement argued; and `ARCHITECTURE.html` is unmodified in the
working tree, which `git status` shows.

---

## What was settled while the brief was being answered

Not in the record copy. Three rounds of amendment were issued against the design as it
was presented, and this is where the settled positions sit so the next reader does not
have to reconstruct them from the archive. Each is carried into
`docs/SCREEN_LIFECYCLE.md` with its reasoning.

**The family is three kinds under two statistical treatments.** Two of the four proposed
additions, accruals and net share issuance, are already S1 ranking inputs as
`accruals_inv` and `share_count_change_inv` and are S1 rubric disqualifiers, so they are
not orthogonal. They are kept as a third kind, **extractions**: a screen whose sole
mechanism is a metric that is already one of several inputs to a live screen, where it
can be outvoted. Extractions and successors are both paired against a named incumbent, so
there are two treatments and not three, and only additions carry the multiplicity
correction. Fundamental momentum is defined on `revenue_growth_4q_trend` alone, dropping
`gross_margin_4q_change`, which is an S1 input and would have made one screen an
extraction and an addition muddled together.

**The slot pool is fixed at 40 and D-7's 2/3/3 is restated as a proportion.** Large takes
`floor(slots / 4)` and the remainder splits mid and small with the extra to small, which
is exactly 2/3/3 at eight and integral at every count from four to twelve. The gap it
closes is pre-existing rather than created by retirement, because the tuner has been able
to reach any count from four to twelve since it was designed and 2/3/3 is defined at eight
alone. Feasible live-screen range is four to ten and both ends are reachable.

**The retirement horizon is 21 days and the reason is stated.** It is the horizon
`VALIDITY.md` §4 already pre-registers the primary claim at, and D-34's 40-day time stop
means an edge appearing only at 63 days is one this portfolio cannot hold to.

**Depth is equalised at evaluation, on both sides.** A shadow writes at
`tuner.slot_cap` for the widest record, and every comparison truncates the screen under
test and the peers it is measured against to one depth by `rank_within_screen`.

**The provenance sense of "live" is renamed to "prospective".** `live` names a screen
state and was also naming where an observation came from, and the promotion rule used both
senses in one sentence.

**A promotion or retirement splits the primary claim, not just the screen's own record**,
because it changes the rubrics and the dossier, which are on `CLAUDE.md` §12's list. So
none executes until the primary claim has reached its pre-registered sample, and any due
are bundled into one boundary.

**Post-earnings drift's registration is a fork, not a finding**, because its input has no
backfillable history: `events.earnings_backward_days` is 7 and `announced_date` is null
for earnings. It carries its own decision number.
