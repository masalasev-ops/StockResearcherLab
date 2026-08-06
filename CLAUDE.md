# CLAUDE.md

Rules for agents working in StockResearcherLab.

Read `ARCHITECTURE.html` before changing anything structural. Read `DECISIONS.md`
when a change touches a numbered decision. `BUILD_PLAN.md` says which phase is
current and what its definition of done is.

**Every rule in this file is justified by this system.** Nothing here is carried from
another codebase on the strength of what went wrong there. A rule whose cost is real
and whose benefit happened somewhere else gets negotiated away at the first
inconvenience, and it invites reasoning about a codebase that is not this one. If a
rule cannot be justified from `ARCHITECTURE.html`, `DECISIONS.md` or `VALIDITY.md`,
it does not belong.

---

## 1. What this system is, and why that changes how you work on it

This is a measuring instrument that happens to trade. Its purpose is to answer whether
an AI researcher selects better than mechanical screens, and the paper portfolios are
the apparatus rather than the objective [VALIDITY.md §1].

That has one consequence which governs almost everything else. **Most ways of breaking
this system produce no error.** The run completes, the numbers look plausible, and the
experiment is quietly worthless. A stray timestamp in the prefix triples the bill while
every output looks normal. Reading a fundamental one field to the left makes the quality
screens look excellent in backfill and ordinary live. Benchmarking against the wrong
column makes the tuner dismantle the screens that hold the design together.

So the standard here is not "does it work". It is "would I be able to tell if it did
not". Section 2 lists the cases where the answer is no unless something enforces it.

---

## 2. Non-negotiable invariants

Each of these can be broken by a change that looks entirely reasonable from inside one
file, and none of them fails loudly when broken.

1. **Absolute filters live only in the universe definition.** No component downstream
   narrows by rank, score or count. Ranking at ingest decides what can be discovered
   before anything has had a chance to be discovered [D-5].
2. **Screens never read each other.** Each sees only its own config and the percentile
   store. Sharing a computed value between them looks like removing duplication and
   destroys the independence the design rests on [D-6].
3. **An unfilled size slot stays empty.** Never backfill from a larger bucket. The empty
   slot is the diversity guarantee working, not a bug to fix [D-8].
4. **Attribution is written at shortlist time for every candidate, with scores frozen as
   they stand that night.** Never reconstructed later, because screen definitions and
   slot allocations drift and a reconstruction applies today's definitions to a past
   date [D-40].
5. **The learning loops read the peer-relative return column, never the SPY column.**
   Against a large-cap index in a large-led market every small-cap candidate posts
   negative alpha regardless of how well it was chosen, and the tuner would cut the two
   screens that keep this system off megacaps [D-42].
6. **The researcher prefix is byte-identical across every call within a night.** No
   clock reads, no unordered iteration, no locale-dependent formatting. A stray
   character breaks the cache and roughly triples the input bill silently [D-20].
7. **The digest step transforms evidence and never judges.** It summarises, extracts and
   classifies. It never scores a candidate, never expresses a view, and never filters
   the candidate set. Crossing this makes a research portfolio measure two models rather
   than one [D-28].
8. **Risk rules are identical across every portfolio.** Sizing, stops, sector cap,
   position cap, cash floor, bucket cap. If they differ the portfolios stop being
   comparable and the experiment is void [D-36].
9. **The researcher never sees a raw price series and never computes a ratio.**
   Everything arrives precomputed. It is a judge, not a calculator [D-16].
10. **Exactly one component writes each table per operation.** ~~Two exceptions are
    documented in `SCHEMA.md` and no third is permitted.~~ [amended, L.3] The stage
    registry declares component, table, operation and column set, and a test asserts
    no two components claim the same triple. Enumerating exceptions kept failing
    because the rule was stated wrongly, not because the design was wrong:
    attribution is inserted by the allocator and updated by the filler, proposal is
    inserted by the client and status-set by the validator, and order, fill and
    position have three components each owning a different transition.
11. **No ambient clock.** Inject `IClock`. Nothing reads system time outside the clock
    implementation. This is what makes replay and invariant 6 possible.
12. **Every fundamental read keys on filing date, never period end.** Period end is the
    natural-looking key and hands you quarterly numbers weeks before they were public
    [D-46].
13. **Config is resolved as of the simulated date, never as of now.** The tuner rewrites
    slot allocations monthly, so any analysis over history that reads current config is
    answering a different question than the one asked [D-43].
14. **Risk caps are operator configuration and are not tunable by the system.** The
    tuner moves screen slots and touches nothing else [D-43].
15. **The digest chain is a hard gate.** If no provider is healthy the run halts before
    any researcher call and no orders are produced. Continuing with digests absent puts
    a night of thinner evidence into the record looking identical to every night around
    it [D-26].
16. **Money is decimal, never `float` or `double`, in any monetary path.** `guards.ps1`
    greps for it.

---

## 3. Workflow

### Starting a phase

Read this file, then the phase in `BUILD_PLAN.md`, then the sections of
`ARCHITECTURE.html` it implements. Check the carried obligations table at the foot of
the build plan for anything owed to this phase by an earlier one.

**Archive the prompt before writing code, not after.** It goes to `prompts/spent/` as
issued. A prompt archived at the end is a prompt archived after you have already
learned things, and it stops being a record of what was asked.

### During

Work checkpoint by checkpoint and commit per checkpoint, not per phase. A phase that
lands as one commit cannot be bisected and cannot be partially reverted.

**A commit message opens with its checkpoint number**, so `4.3 candidate allocator
quota enforcement`. Checkpoints are numbered in `BUILD_PLAN.md`. Churn commits carry no
number and say what they are. A commit spanning two checkpoints means the checkpoints
were drawn wrong, so split it rather than the message.

Run the invariant tests continuously rather than at the end. They are cheap and they
are the only thing standing between a plausible-looking run and a worthless one.

**When two authored documents contradict each other and one of them is an invariant,**
follow the invariant, build what it requires, and report the contradiction. Do not
stall on it. Stop only when a decision or an invariant is contradicted by the work
itself and following it is not possible. In every other case, note it and continue.
Never edit an authored document to match what you built.

When something is merely wrong, missing or awkward without contradicting an authored
document, note it and continue. Section 15 covers what to raise and what to file.

**A build session never adds a measurement to its own scope.** If work appears to need
one before it can proceed, that is a finding to report and a human to decide on, not a
task to take on. Phase P is the only phase whose deliverable is measurement, and it
exists because nothing else did yet.

A question deferred to a later phase is answered by reading what that phase produced,
not by measuring anything extra. The coverage of a table is a query against it once
populated. The base rate of a field is a query against it once ingested. Both are read
at sign-off by whoever writes the decision.

### Finishing

The definition of done in `BUILD_PLAN.md` is a checklist rather than a description.
Every line runs and produces an observable result.

Then, in order:

- Update `PROGRESS.md` with what was built, the HEAD sha, test counts, and any figure
  that has moved from estimated to measured.
- Register new fixtures in `FIXTURES.md`.
- Fill the Consumer column in `CONFIG_REFERENCE.md` for every key this phase wired up,
  having actually read the composition code rather than inferring from the name.

### The review at sign-off

**Runs in a fresh session, not the one that built the phase.** The session that wrote
the code has already convinced itself, and asking it to check its own work produces
agreement rather than a check. A session that has committed here does not run it.

It writes what it found into `PROGRESS.md` and corrects nothing. The two steps and the
three questions the review asks are in `BUILD_PLAN.md` [D-67].

**Never edit a spent prompt** except to correct it to the text actually issued [D-63].
It is the only evidence of why the code looks the way it does. Nothing is reconciled
against it.

---

## 4. Project structure

```
StockResearcherLab.slnx
CLAUDE.md                     this file
README.md
appsettings.Secrets.example.json
guards.ps1                    CI greps for the invariants that are grep-checkable
migrate.ps1
seed.ps1
Directory.Build.props
Directory.Packages.props

docs/                         every document except README and CLAUDE
prompts/                      runtime prompts read by code at execution time
prompts/spent/                every prompt issued, archived verbatim

src/
  StockResearcherLab.Core       domain types, the stage abstraction, IClock,
                                as-of config resolution. No project references.
  StockResearcherLab.Data       Postgres access, migrations, every store.
                                References Core.
  StockResearcherLab.Pipeline   all stages, in folders by layer:
                                  Ingest/ Compute/ Select/ Decide/ Execute/ Learn/
                                plus the stage registry. References Core and Data.
  StockResearcherLab.Worker     the host that runs the nightly pipeline and the
                                backfill. References Pipeline.
  StockResearcherLab.Api        read-only query API. References Core and Data.
  StockResearcherLab.Ui         Blazor. References Api. [amended, O.3]
  StockResearcherLab.Tests

tools/
  probe                         phase P only, deleted or rewritten afterwards
```

**The Api never references Pipeline, and that is load-bearing rather than tidy.** It is
what structurally prevents the interface from invoking a stage, which is how the
read-only guarantee survives contact with a future feature request. If a UI need ever
seems to require a Pipeline reference, the answer is a read model, not a reference.

**Layers are folders inside one project rather than separate assemblies.** The real
boundary in this design is the stage contract and one-writer-per-table, both enforced by
the registry test, and assembly references would add build ceremony without adding
enforcement. The folder names match the layer names in `ARCHITECTURE.html`.

**`Core` has no project references.** Anything that needs a database, an HTTP client or
a clock implementation does not belong there.

---

## 5. The stage pattern

Every pipeline component is a stage. A stage declares its read set and write set as
data, takes a date and a config version, and is a pure function of those inputs. No
stage reaches outside its declared sets.

This exists to make section 2 enforceable rather than aspirational. The registry lets
one test assert that write ownership matches `SCHEMA.md`, which is invariant 10 checked
mechanically. Purity is what lets any night be replayed and produce identical output,
which is how accidental clock reads and unordered iteration get caught rather than
argued about.

**Prefer making a mistake impossible over documenting that it is wrong.** The five
screens, the portfolio registry, the digest chain and the slot allocations are
configuration rows. When a screen is a row, a screen cannot be added by writing a class.

**Parallelism has two partition keys and they are not interchangeable.** Ingest,
indicators and valuation partition by ticker. Percentiles and screen scores partition by
date, because a percentile on a given day needs every name in the cell on that day.
Getting this backwards produces wrong numbers rather than slow ones.

**Parallelism lives inside a stage, never across stages.** A partial run is worse than
no run, because the freshness guard and the zero-row halt both depend on a stage being
finished before the next reads it.

---

## 6. Coding conduct

### Determinism is a correctness property here

Invariants 6 and 11 both rest on it, and so does the ability to replay any night.
Two runs of the same stage over the same date and config version must produce
byte-identical output.

- No `DateTime.Now` or `DateTime.UtcNow` outside the clock implementation.
- **Never let `Dictionary` or `HashSet` enumeration order reach output.** That order
  is unspecified and can differ between runs of the same binary. Sort explicitly
  before rendering, writing or hashing.
- `CultureInfo.InvariantCulture` on every number and date format that reaches a file,
  a prompt or the database. A decimal comma in the prefix breaks the cache and the
  run still completes.
- `Random` is seeded from the run date. The arbitration tie-break and the random
  portfolio both have to reproduce exactly.
- `Guid.NewGuid()` never appears in anything that affects output or ordering.
- Parallelism must not affect results. If a stage's output depends on which worker
  finished first, it is wrong even when the numbers look right.

### Null means unknown, never zero

A great deal of this system's data is legitimately absent. Sentiment for a thinly
covered name. Fundamentals before a company's first filing. Cash and burn for a large
cap, where they are deliberately not sent. Short interest the provider does not have.

**Defaulting an absent value to zero is a silent correctness bug**, because zero is a
real value carrying meaning. A sentiment z-score of zero says attention is exactly at
its own baseline. Absent says nobody knows. The screens treat those differently and
so would the researcher.

Nullable reference types on, warnings as errors. When a metric is null the rule for
what happens is in the screen definition or the rubric, never in a `?? 0` at the call
site.

### Dates

`DateOnly` for trading dates. `DateTime` only where a genuine instant is meant, which
is essentially the run log and the cost ledger.

All market semantics are US Eastern. A trading date is the label the exchange gave a
session, not a timezone conversion of a timestamp, and treating it as the latter
produces off-by-one errors that survive every test written against a single timezone.

### Set-based over row-by-row

The percentile and screen stages are single statements with window functions over a
whole date, not loops issuing a query per ticker. That is what makes them
date-partitioned and correct, and the speed is a side effect.

Opening a connection inside a loop over tickers usually means the partition key is
wrong. See section 5.

### Fail closed

A stage completes or it fails the run. Do not catch mid-stage and continue with
partial rows: a partial run is worse than none, and the next stage cannot tell the
difference between a short table and a real one.

Two exceptions exist and both are enumerated in `RUNBOOK.md` rather than being general
tolerance. A per-candidate model failure records a PASS and continues. A digest
provider failure falls through the chain.

### Names match the architecture

A component called ScreenEngine in `ARCHITECTURE.html` is `ScreenEngine` in code.
Table names in `SCHEMA.md` are the table names. Config keys in `CONFIG_REFERENCE.md`
are the key strings.

This is the cheapest traceability available and it costs nothing to maintain.

### Build what is needed now

No abstraction over a second data provider until there is one. No repository
interface with a single implementation. No pipeline framework beyond the stage
registry.

The digest chain is not a counter-example: it has two real implementations from the
first day and the chain is what invariant 15 is enforced through. The test is whether
the second implementation exists, not whether one can be imagined.

---

## 7. Claims about the code

Most of this codebase does not exist yet, which means there is nothing to contradict a
confident statement about it. That is the specific risk here.

**Confirm any claim about this code with a direct read of the actual lines.** Not from a
grep, which tells you a string exists somewhere but not what the code does with it. Not
from memory of a read earlier in the session.

**A grep used as verification must be whitespace-tolerant, and the pattern is stated
alongside the result** [N.11]. Prose in this corpus is hard-wrapped, so a phrase that
breaks across a line will not match a line-anchored pattern. That failure is silent: the
sweep reports no hit and reads as a pass.

**Print the HEAD sha and recent commits before analysing a branch.** Analysis against a
stale checkout produces findings that were fixed already.

**A chat artifact is not a repository artifact.** Something agreed in conversation does
not exist until it is committed here. Before citing a rule, a decision or a config key,
confirm it is actually in the repository.

**A code comment is not a record either.** What makes something a record is where it
will be read. An obligation noted beside the code that has it, and nowhere the planning
for that work will look, is not recorded. Put it in `BUILD_PLAN.md` carried obligations,
then read it back off disk.

**State uncertainty as uncertainty.** Almost every figure in `ARCHITECTURE.html` and
`VALIDITY.md` is a design estimate rather than a measurement, and `PROGRESS.md` has a
table for the difference. Do not repeat an estimate as though it were measured.

---

## 8. Configuration

Config rows are append-only and versioned. Current is `MAX(version)` for a key. A change
inserts version + 1, which is what lets a later behaviour change be explained after the
fact.

**No magic numbers at call sites.** A value that could plausibly be tuned is a key in
`CONFIG_REFERENCE.md`, not a literal in a constructor.

**`CONFIG_REFERENCE.md` records the verified consumer**, meaning someone read the
composition code and confirmed the binding. Not the assumed consumer. An unverified
entry is worse than an absent one, because it lets an audit conclude a value is wired up
when nothing reads it.

**Every attribution row carries the config version in force when it was written.** That
is the only thing that lets history be segmented rather than pooled after a screen
definition changes, and without it a year of data becomes uninterpretable the first time
the tuner runs.

---

## 9. Tests

**Every invariant in section 2 that can carry a test has one.** An invariant nobody can
break in CI is decoration. Specifically: the prefix is a snapshot test, point-in-time is
a test that no fundamental is readable before its filing date, write ownership is a test
over the stage registry, and the worked arbitration example in `ARCHITECTURE.html` is a
fixture that must reproduce exactly.

**Fixtures live in `FIXTURES.md` and are registered nowhere else.** ~~Do not enumerate
fixture names in build plan detail or in this file.~~ [narrowed, N.8] A second list is
what goes silently incomplete the moment one is registered in the other, so no other
document keeps one. Naming a single fixture where a document states what must be proved
is not a list and is expected: a definition of done that cannot name what it tests is
not a definition of done, and the forward list in `FIXTURES.md` cites those documents as
its own sources. A spent prompt may name a fixture freely, because it records what was
asked rather than what is true [moved from `FIXTURES.md`, N.10].

**Prefer testing through the public surface.** `InternalsVisibleTo` is permitted where
widening the public API purely for tests would be worse. The bar is that the public
alternative would be worse, not that it would be less convenient. Record each use at the
reference site.

---

## 10. Git and secrets

- **Never a whole-file rewrite** when an edit will do. A large diff hides the change
  inside churn.
- **Separate pure churn from content.** Formatting, line endings and renames go in their
  own commit, verified as whitespace-only.
- **Branch rather than commit to main** for anything not trivially reversible.
- **Inspect the `.gitignore` diff before committing it.**
- **No API key, token or credential enters the repository.** Secrets live in
  `appsettings.Secrets.json` beside the project that needs them, excluded by wildcard
  [D-55]. `appsettings.Secrets.example.json` is the committed template and is the only
  such file that may be tracked. The file sits in the working tree, so an ignore rule is
  the only thing between it and a commit. Check `git status --ignored` before the first
  commit of any new project, and never `git add -f` a path matching `*.Secrets.json`.
- **CI green before merge.**

---

## 11. Decisions and evidence

This system exists to measure something, so the rules about evidence are load-bearing
rather than good practice.

**Record a decision before the number that would justify it exists.** Changing a rule
after seeing that the change makes a result pass is result-shopping whatever the
reasoning says. `VALIDITY.md` §4 pre-registers the thresholds for exactly this reason.

**Never loosen a bound because a measurement missed it.** If breaches concentrate in one
regime that is a finding about non-stationarity. If a rate is inconsistent with how the
thing was constructed, fix the construction. Raising the bound is neither.

**Do not tune screens on forward returns before the researcher has judged anything.**
That optimises toward whatever captured market beta in the backfill window, which in
most recent windows means trend and large caps, and walks straight back into the
convergence this design exists to prevent [BUILD_PLAN phase 4].

**Read the candidate-level evidence before the equity curves.** The portfolio comparison
is underpowered for two to three years while the attribution table answers in one
[VALIDITY.md §3]. A conclusion drawn from portfolio returns early is drawn from noise
however convincing the chart.

**Exclude at the granularity of the doubt.** Doubt about an identity excludes the
security. Doubt about one quoted price excludes that quote.

---

## 12. Once live history is accumulating

- **A change to a screen definition, a rubric, the dossier format or a model splits
  history into halves that cannot be pooled.** It stays possible forever, but the
  analysis must then be segmented on config version rather than pooled.
- **Know what a change costs before proposing it.** Reporting and the learning loops cost
  a re-run of that stage. The candidate generator, the dossier, the rubrics and the risk
  layer invalidate comparisons across the boundary. Bundle those.
- **Do not stop a run to apply a fix.** File it.
- **Never re-run the attribution write.** Those rows carry scores frozen when first
  written, and a re-run applies today's definitions to a past date.

---

## 13. Documents

- **Prose states the rule; the decision number is a trailing bracket.** Test by deleting
  the bracket: the sentence must still read and still state the rule.
- **Every section describing a component carries a build-state marker.** Without one,
  shipped and aspirational prose read identically and every reader re-derives which is
  which.
- **`ARCHITECTURE.html` is the narrative, `DECISIONS.md` is the register.** One is read
  through, the other is looked up. Do not merge them or copy register content into the
  narrative.
- **Never renumber a decision.** A superseded entry keeps its number and gains a status
  pointing at its replacement.
- **Supersede visibly, never silently.** A fact removed from an authored document is
  struck and pointed at whatever replaced it, not deleted. A superseded decision keeps
  its number and gains a status naming its replacement. A corrected figure is struck
  with the correction stated. The runtime prompts in `prompts/` are the sole
  exception: the model reads them at execution time and cannot tell struck text from
  live, so removals there are clean deletions and the record lives in `DECISIONS.md`.

### Who writes what

**Authored content is not yours to write.** Decisions, invariants, phase scope, and any
prose stating a rule. If a build reveals one is wrong, missing or contradicted, report
it. Do not close it.

**Verified content is yours to write.** Test counts, HEAD shas, measured timings, row
counts, the Consumer column in `CONFIG_REFERENCE.md`, and everything in `PROGRESS.md`.
Correct these directly.

The test is whether the statement is a decision or an observation.

**`ARCHITECTURE.html` and `CLAUDE.md` are human-edited only.** The default behaviour is
to update documentation to match what was just built, which inverts the relationship.
These constrain the code. If the code disagrees with them, the code is wrong.

---

## 14. Prompts

**Every prompt issued is archived verbatim in `prompts/spent/` and ~~is never edited once
run~~ [amended, D-63] may be edited once run only to correct it to the text actually
issued.** A spent prompt records what was asked, not what should have been asked.
Changing it to what should have been asked destroys the only evidence of why the code
came out the way it did. Correcting it to what was issued restores that evidence, and
the header states that the correction happened and why [D-63].

Whenever a decision is added, amended or superseded, ask:

> **Does this decision change a phase prompt that has not been run yet?**

If yes, fix the unrun prompt. If it changes one already run, leave it alone and note the
drift in `PROGRESS.md` against that phase.

The runtime prompts in `prompts/` follow the opposite rule. They are product, code reads
them at execution time, and they are versioned and edited deliberately.

---

## 15. Working style

- **Do not return every discovery as a task.** A stream of new issues buries the
  important ones. Raise what is dangerous or what blocks the deliverable. File the rest.
- **Distinguish a blocker from a finding** explicitly when reporting.
- **Amendment format**: a short prose verdict first, then labelled clauses in a fenced
  block, scoped to their checkpoint, written in the plan's own style, each naming its
  test and definition of done, so they paste directly into a build prompt.
- **Own errors plainly.** State what was wrong, what the correction is, and whether
  anything downstream inherited it.

---

## 16. Style

- Standard keyboard punctuation only. No em dashes.
- Do not write "honest" or "honesty" about the system. State the mechanism.
- When editing text with existing formatting conventions, preserve them rather than
  normalising to your own.
- Around wording that exists for a reason you do not know, append rather than rewrite.

---

## 17. Keeping this file short

This file is loaded every session and competes with the task for attention. Past a few
hundred lines it is skimmed rather than read and it stops working.

Before adding anything, ask whether it is an invariant or a recent annoyance. Most are
the second. An invariant belongs in section 2 and needs a test. Everything else belongs
in `DECISIONS.md` or the phase prompt where it came up.
