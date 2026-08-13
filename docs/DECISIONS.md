# DECISIONS.md

The register. One entry per decision, each with the reason it was made. Reasons
are the point: they are what stops a later session re-litigating something already
settled, and what tells you whether a new circumstance actually invalidates the
decision or merely feels uncomfortable.

Numbers are never reused and never renumbered. A superseded entry keeps its number
and gains a status line pointing at its replacement.

Status values: `ACTIVE`, `SUPERSEDED BY D-n`, `OPEN`.

All entries below were settled during scoping on 2026-08-04 and 2026-08-05, before
any code existed. Entries carry no measurement behind them unless stated.

---

## Scope and universe

**D-1 The universe is the entire US market, not an index.** `ACTIVE`
Restricting to the S&P 500 or Russell 2000 would predetermine the answer to the
question the system exists to ask, since index membership is itself a selection
rule someone else made.

**D-2 Stocks only. ETFs are out of scope.** `ACTIVE`
Three of the five screens need company fundamentals a fund does not have, so ETFs
would enter through essentially one screen and distort both slot allocation and
per-screen attribution. The sector and position caps also compute on positions
rather than look-through exposure, so holding a stock and a fund containing it
would read as diversification. A separate tool will cover ETFs.

**D-3 Long only.** `ACTIVE`
Of five screens only trend inverts cleanly. Insider selling carries almost no
signal, expensive junk is not a timing signal, and shorting strength is how people
get destroyed. Borrow availability and cost are not in the data source, so a paper
short book would show returns on positions that could not have been opened. Net
exposure would also become a free variable dominating returns and confounding the
selection comparison.

**D-4 Universe criteria are absolute, not relative.** `ACTIVE`
Common stock and ADRs, excluding funds, trusts and SPACs. Market cap at or above
$300M. Price at or above $5. Twenty-day median dollar volume at or above $2M.
At least 250 trading days of history. At least four clean filing-date gaps observed
[added, D-62]. The dollar volume floor was raised from $1M because the participation
cap would otherwise reject a typical $8,300 position.

The filing-gap criterion arrived with D-62 rather than at scoping, and the list above
was complete only until then. It belongs here rather than in the fundamentals path
because it is an absolute filter, and INVARIANT 1 puts absolute filters in the
universe definition and nowhere else. UniverseBuilder computes the count for the date
it is building, from rows in `fundamental_snapshot` whose unknown reason is `none` and
whose effective filing date is at or before that date, and applies the exclusion
alongside market cap, price and volume. It is computed rather than stored because a
ticker has more clean gaps now than it had three years ago, and a stored total read
during backfill would admit names a live system on that date would have excluded
[M.1].

**D-5 Absolute filters live only in the universe definition.** `ACTIVE`
No component downstream narrows by rank, score or count. Raised after the news
ingest was found limiting itself to the top 400 names by prior screen score, which
made the sentiment screen structurally unable to discover anything it had not
already found. See INVARIANT 1.

---

## Candidate generation

**D-6 Five independent screens replace a single composite ranker.** `ACTIVE`
A single composite score ranking the universe converges on roughly the same
hundred well-known large caps every night, because one metric set decides
everything. Five screens each picking on their own logic, deduplicated, produce a
varied set by construction rather than by hope.

**D-7 Slot ceiling of eight per screen under a 2/3/3 size quota.** `ACTIVE`
Two large-and-above, three mid, three small. Caps megacaps at ten of forty, close
to their share of total US market capitalisation, and guarantees up to fifteen
small-cap places. The ceiling is a ceiling and never a target.

**D-8 An unfilled size slot stays empty.** `ACTIVE`
Backfilling from a larger bucket is how the diversity guarantee leaks back out.
See INVARIANT 3.

**D-9 Each screen's floor is the 98th percentile of its own trailing 250-day score
distribution.** `ACTIVE`
Self-referential rather than fixed, so it adapts to dispersion instead of going
dark in a quiet market. A screen returning nothing is expected behaviour.

**D-10 Percentiles are computed within size bucket by sector cells.** `ACTIVE`
Falling back to size bucket alone when a cell has fewer than fifteen members. A
$1B name should be scored against its peers, not against megacaps.

**D-11 Metrics favour change over level.** `ACTIVE`
Levels permanently favour incumbents. A company at 12 percent return on capital
rising from 8 is more interesting than one flat at 15.

**D-12 Sentiment article counts are z-scored against the ticker's own history,
never cross-sectionally.** `ACTIVE`
A cross-sectional count measures analyst coverage, which is a size proxy. Against
its own baseline it measures change in attention, which is the actual signal, and
is structurally anti-megacap.

**D-13 Mean reversion requires a stabilisation gate.** `ACTIVE`
Without it the screen gates on quality then ranks by whichever survivor fell
furthest, which is the falling-knife instinct one level below where it was
removed. Three conditions: price back above the 20-day average or 20-day relative
strength turning up, article count z-score back within 1.0 of baseline, and
7-day against 30-day sentiment not negative.

**D-14 The two news conditions in D-13 fail open below three articles in seven
days.** `ACTIVE`
Failing closed would delete the small-cap slots this screen exists to fill.
Thinly covered names therefore get a weaker gate, which is deliberate.

---

## The researcher

**D-15 The researcher judges each candidate independently against an absolute bar.**
`ACTIVE`
Not ranking a set and picking from it. Keeps stated probabilities comparable
across nights and gives the calibration loop something real to measure. Requires
one context per candidate, since a shared context anchors on the first strong name.

**D-16 The model is a judge, not a calculator.** `ACTIVE`
It never searches, never computes a ratio, and never sees a raw price series.
Everything arrives precomputed. See INVARIANT 9.

**D-17 Opus 5 is the primary research model.** `ACTIVE`
Chosen when the alternative was Sonnet at $40 against $64. Batch pricing later
took Opus to roughly $45, removing the cost pressure that made the choice close.

**D-18 The output is a probability, not an integer conviction score.** `ACTIVE`
Specifically the probability that the trade reaches its target before its stop.
Three consequences: the absolute bar acquires a defined value, since the model's
own stop and target imply a breakeven it must clear; calibration becomes scoreable
with a Brier score and a reliability diagram; and ties at the arbitration cutoff
become rare rather than routine. Made before go-live because switching scales later
invalidates accumulated history.

**D-19 The output carries a counter-argument of twenty words.** `ACTIVE`
Forces the model past its first conclusion, and gives the candidate detail screen
the one field worth reading when a decision later looks wrong. Roughly $4 a year.

**D-20 The five screen rubrics live in the cached prefix.** `ACTIVE`
A prefix token costs roughly a seventh of a candidate-block token, because the
prefix is cached and read many times while each block is fresh input paid once per
candidate. Everything shared belongs in the prefix; the candidate block carries
bare numbers with short keys and no explanatory text.

**D-21 Extended thinking is off.** `ACTIVE`
Thinking tokens bill at output rates, and output is the cost line that cannot be
compressed.

**D-22 Opus 5 runs through the Batch API with the one-hour cache.** `ACTIVE`
Nothing waits on the evening run, since orders do not fill until the next morning.
Batch halves every line. The one-hour cache is used rather than the five-minute one
because batch timing could otherwise spread the calls past the cache window, which
would cost more than not batching at all.

---

## Evidence given to the researcher

**D-23 Sentiment is pulled for the whole universe; headlines only for candidates.**
`ACTIVE`
Sentiment feeds the sentiment screen's ranking, so it must cover everything or the
screen is blind. Headlines exist only for the dossier, and only candidates reach
the dossier.

**D-24 News is condensed to a digest by a separate cheap model before reaching the
researcher.** `ACTIVE`
Full articles run five to eight hundred tokens each, so three per candidate sent to
Opus would cost around $64 a year and take the total past $137.

**D-25 The digest source is an ordered provider chain: local model primary, Haiku
4.5 secondary.** `ACTIVE`
Local is primary by choice rather than by cost, for independence from a hosted
service and because nothing leaves the machine. Haiku rather than DeepSeek Flash so
that a single vendor outage cannot take out both the digest fallback and a research
portfolio. Built as a chain rather than a primary with a fallback branch, so
failover works in both directions with no rarely-executed code path.

**D-26 The chain is a hard gate on the researcher.** `ACTIVE`
If no link is healthy the run halts before any researcher call and no orders are
produced. A night without digests is not comparable to a night with them, and
skipping a night is free. See INVARIANT 15.

**D-27 Two candidates a night always route to the secondary provider, chosen by the
date seed.** `ACTIVE`
A path that only executes during an outage is broken most of the time and is
discovered on the night it was needed. The rotation also builds a paired sample, so
after a quarter the question of whether digest source matters can be answered from
data. Roughly $1.30 a year.

**D-28 The local model transforms evidence and never judges.** `ACTIVE`
See INVARIANT 7. Crossing this line looks like an improvement, which is why it is
written down.

**D-29 Which model produced each digest is recorded.** `ACTIVE`
Both the provider and the loaded model name. Without it, a later shift in results
has two explanations and no way to separate them.

---

## Risk and execution

**D-30 Starting equity $100,000, risk 0.5 percent per trade.** `ACTIVE`
Gives positions near $8,300 at a typical 6 percent stop, which clears the
participation cap at the universe liquidity floor with room.

**D-31 Position size is risk divided by stop distance. Stop is the wider of twice
ATR(14) and the model suggestion, capped at 12 percent.** `ACTIVE`

**D-32 Caps: 8 open positions, 2 new entries per day, 10 percent cash floor, 20
percent single position, 30 percent sector, 4 of 8 in the large-and-above bucket.**
`ACTIVE`
The bucket cap is the portfolio-level counterpart to the candidate-level size
quota, and exists because the quota guarantees diversity in the candidate set and
nothing about what actually gets held.

**D-33 Arbitration when BUYs exceed the entry cap: drop anything the caps would
block, sort by stated probability, fill from the top, break ties with a date-seeded
coin flip.** `ACTIVE`
Screen rank was rejected because rank in one screen and rank in another are
positions in unrelated distributions, so comparing them requires a normalisation
that is a composite ranker rebuilt at the last step, and because it would import
the screens' opinion into the researcher's portfolio and contaminate the comparison
between them. A thinnest-bucket tiebreak was rejected because the diversity concern
belongs in the risk layer as a visible cap rather than buried in a tiebreak. The
neutral flip is what leaves a clean sample for evaluating the rule later.

**D-34 Exits are deterministic and fire from code.** `ACTIVE`
Stop, target, 40-day time stop, in that order. The researcher votes on
thesis-change exits only and is never argued out of a stop.

**D-35 Fills are at the next open with size-aware slippage, and orders above 1
percent of 20-day median dollar volume are rejected.** `ACTIVE`
Base slippage 5bp large, 10bp mid, 20bp small, plus 25 times participation.

---

## Portfolios and measurement

**D-36 Four portfolios on the identical candidate set: Research: Opus 5, Research:
V4 Pro, Screens, Random.** `ACTIVE`
Identical gates, sizing, stops and starting equity, so any difference between them
is selection. SPY is a reference line, not a portfolio.
Supersedes an earlier decision to run three. The economics changed: batch pricing
freed roughly $40 and the second research portfolio costs about $5, so the
comparison of frontier model against cheap model became close to free.

**D-37 Exposure matching applies to Screens and Random only.** `ACTIVE`
They do not judge, so unmatched they would stay permanently invested and win in any
rising market for reasons unrelated to selection. A research portfolio abstains on
its own terms, so the two are never matched to each other and the difference in
abstention is itself part of the measurement.

**D-38 Modularity applies to research portfolios only.** `ACTIVE`
Screens and Random have no model and nothing to swap. The research registry carries
name, provider, model id, whether it batches, whether it is primary, and state.
Exactly one research portfolio is primary and the controls match their entry count
to it, so retiring the primary is a flag move rather than a code change.

**D-39 Only research portfolios are candidates for retirement on performance.**
`ACTIVE`
Screens and Random are controls, and their underperformance is the measurement. If
Random beat the research portfolios after a year, retiring it would be deleting the
most important result the system had produced.

**D-40 Attribution is written at shortlist time for every candidate, with scores
frozen.** `ACTIVE`
Roughly 7,000 rows a year against about 250 trades, which is the difference between
a measurable per-screen sample and noise. It also records the counterfactual, which
is what makes abstention and arbitration evaluable at all. Cannot be reconstructed
later because screen definitions and slot allocations drift.

**D-41 Forward returns are stored three ways: raw, against SPY, and against the
name's size-and-sector peer cell.** `ACTIVE`

**D-42 The learning loops read the peer-relative column, never SPY.** `ACTIVE`
Measured against a large-cap index in a large-led market, every small-cap candidate
posts negative alpha regardless of how well it was chosen. The tuner would cut the
sentiment and flow screens, which are the two that structurally tilt small and the
two doing most to keep the system off megacaps, while the trend screen collected the
same effect as a reward. See INVARIANT 5.

---

## Learning

**D-43 The tuner reallocates slots between screens and touches nothing else.**
`ACTIVE`
Monthly, on peer-relative hit rate and alpha, shrunk 0.8 old to 0.2 implied, with a
floor of four slots and a cap of twelve. Slot reallocation was chosen over weight
tuning because per-screen results are interpretable and actionable while a weight
moving from 0.40 to 0.43 is neither. The floor stops a screen being switched off
before it has enough observations to judge; the cap stops collapse back into one
screen. Risk caps are operator configuration and are never tuned. See INVARIANT 14.

**D-44 Lessons require n of at least 30 and expire after six months unless
reconfirmed.** `ACTIVE`
Written monthly by one AI call over aggregate statistics, never over raw trades.
Maximum ten active.

**D-45 Calibration is reported per screen rather than pooled.** `ACTIVE`
Candidates surfaced by different screens receive different screen-specific evidence
blocks, so the comparison that holds evidence constant is within a screen.

---

## Data and infrastructure

**D-46 Every fundamental read keys on filing date, never period end.** `ACTIVE`
Period end hands you quarterly numbers roughly five weeks before they were
published, applied to every fundamental in the set. The quality and mean reversion
screens would look excellent in backfill and ordinary live. See INVARIANT 12.

**D-47 Five years of backfill before go-live.** `ACTIVE`
Screen floors need 250 days of history to mean anything, and the tuner and
calibration report need populated attribution from month one. Five rather than two
because the window must contain a real drawdown, which is the only condition under
which the quality and mean reversion screens converge on the same megacaps.

**D-48 The historical universe is reconstructed per date including delisted
tickers.** `ACTIVE`
Building it from symbols listed today deletes everything that went bankrupt,
delisted or was acquired, which is disproportionately the losers.

**D-49 Postgres, not SQLite.** `ACTIVE`
Not for size. Twelve million screen score rows recompute every time a screen
definition changes during development, and that job wants concurrent writers and
bulk loading. The nightly run would have been fine on either.

**D-50 C# throughout, with a read-only API and a Blazor front end.** `ACTIVE`
No maintained C# client exists for the data provider, so a thin typed HTTP client is
written here, which also keeps point-in-time discipline under direct control.

**D-51 The only write the UI can make is the local model connection config.**
`ACTIVE`
A deliberate exception to the read-only API, named so that the seam is visible if
more writes are proposed later.

**D-52 The project is named StockResearcherLab.** `ACTIVE`
An earlier candidate named fundamentals, which describes one screen of five and
would have pulled an agent toward treating fundamental metrics as primary.

---

**D-55 Secrets live in `appsettings.Secrets.json` beside the project that needs
them.** `ACTIVE`
A standard .NET configuration overlay, excluded from version control, requiring no
tooling beyond the configuration builder already in use. The casing is exact:
lowercase `appsettings`, capital `Secrets`. `appsettings.Secrets.example.json` is
committed as the template and is the only such file that may be tracked.

The tradeoff being accepted: the file sits inside the working tree, so a gitignore
rule is the only thing between it and a commit, whereas `dotnet user-secrets` stores
outside the repository and cannot be committed at all. The mitigation is a wildcard
ignore rather than a per-file entry, a re-include for the example, and a grep over
staged content before the first commit of any new project.

The ignore is a wildcard rather than a literal filename because gitignore is
case-sensitive on Linux and CI while Windows is not, and a literal would silently
stop matching if the casing ever drifted. Both `*.Secrets.json` and `*.secrets.json`
are listed for the same reason.

**D-56 Six source projects, with layers as folders rather than assemblies.**
`ACTIVE`
`Core` holds domain types, the stage abstraction, the clock and as-of config
resolution, and has no project references. `Data` holds Postgres access and every
store. `Pipeline` holds all stages in folders named for the layers, plus the stage
registry. `Worker` hosts the nightly run and the backfill. `Api` is read-only. `Ui` is
Blazor.

Layers are folders because the boundary that actually matters in this design is the
stage contract and one-writer-per-table, both enforced by the registry test. Separate
assemblies per layer would add build ceremony without adding enforcement.

**`Api` references `Core` and `Data` and never `Pipeline`.** That is the structural
form of D-51, and it is what stops the read-only guarantee eroding the first time a UI
feature would be easier with a stage call. If one appears to need it, the answer is a
read model.

**D-57 A filing date equal to its period end is treated as unknown, and the
row becomes readable only after period end plus 65 days.** `SUPERSEDED BY D-62`
The probe found names returning `filing_date == period_end` in 35 of 73
periods and 11 of the newest 12, with no nulls. [struck as unevidenced by H.3,
reinstated by H.4] H.3 struck this clause because the probe read the newest
eight quarters per name and could not have produced a figure spanning 73
periods, and H.3's replacement text then claimed the observed rate was higher
than 35 of 73, which was wrong. H.4 re-read every quarter and reproduced the
original exactly: RJET.US, 73 periods, 35 equal to `period_end`, 11 of the
newest 12, 0 nulls. The figure was unevidenced when written rather than
incorrect, and it now has a committed transcript behind it. The field is
populated, so nothing errors, and reading it as a filing date hands you the
quarter's numbers on the day the quarter closed. That is the silent-failure
class invariant 12 exists to prevent.

Imputing a plausible date would be fabricating a point in time, so the rule
substitutes the widest gap the probe actually observed, being 65 days across
56 clean quarters where the range was 19 to 65. The maximum is used rather
than the mean because being late costs freshness while being early costs
correctness, and point-in-time discipline is a promise never to be early.

Those 56 quarters were the newest eight per name. H.4 read every quarter the
provider returns and found 454 clean gaps ranging from -16 to 210 days, 38 of
them above 65 [PROGRESS.md, corrective pass H]. The constant stays at 65 here
because widening it is an authored amendment rather than a corrective pass's
to make. As the rule stands, the substitution is narrower than the observed
distribution and would read those 38 quarters before they were public, and the
negative gaps are a case this entry does not address at all.

The substitution is recorded on the row rather than applied invisibly, so
the rate is measurable and a provider change that made equality universal
would be visible rather than silently widening every read.

**D-58 Short interest is dropped from the flow screen.** `ACTIVE`
The probe settled that this provider has no short interest series and no
as-of date. Values are populated in Technicals, null throughout SharesStats,
no date key exists anywhere in the payload, and `historical=1` returns a
fixed object rather than a series. A one-month change is computable from
SharesShortPriorMonth; a series is not.

The screen therefore ranks on insider net dollar flow, distinct buyer count
and institutional ownership change. Three inputs rather than four.

Keeping a one-month change was rejected because it is not backfillable, and
a screen whose backfill scores come from a different metric set than its
live scores has a floor drawn from a distribution the live screen does not
share. A floor like that is not a floor. Identical inputs across backfill
and live is worth more than a fourth input.

Institutional ownership survives and compensates partly: Holders entries are
dated and carry change, so it is backfillable at quarterly grain.

**D-59 The freshness guard aborts below 40,000 rows and alerts between
40,000 and 45,000.** `ACTIVE`
The probe measured about 50,000 rows on settled days with a 0.35 percent
spread across three of them, one settled day 11 percent below its
neighbours, and part-settled sessions at 3,544 and 9,072 rows.

The two populations are an order of magnitude apart, so a wide floor
separates them with no false positives while a tight tolerance would fire on
the 11 percent day and teach you to ignore the alarm. The guard exists to
catch a part-settled or truncated file, not to detect listing churn.

The upper bound is deliberately absent. No failure mode produces too many
rows, and a band would add a second way to abort for no gain.

Supersedes the `from probe` placeholder in CONFIG_REFERENCE.md, which was
the only key in that document with no decision behind it.

**D-60 The mean reversion no-digest disqualifier applies only where coverage
existed to have been informative.** `ACTIVE`
The rubric disqualified a candidate when no digest was available and the
decline exceeded 35 percent. The probe found small caps carrying 2 to 44
articles in 90 days, so having no material news is the ordinary state rather
than a signal, and the clause as written would systematically reject the
names the screen's three small slots exist to fill.

The disqualifier now requires the ticker's trailing 90-day article count to
be at least 12, roughly one a week. Above that, silence after a large
decline is informative and disqualifies. Below it, absence carries no
information and the candidate is judged on its other evidence.

This is the same asymmetry D-14 accepted at the screen gate, applied at the
rubric. Thinly covered names get a weaker test, deliberately, because the
alternative is not testing them at all.

**D-61 Flow is ingested at each source's natural grain and consumed as
derived daily metrics.** `ACTIVE`
flow_daily declared a ticker-by-week grain when its remaining fields have
different natural ones. Insider transactions are per-event and dated;
institutional ownership is quarterly and dated. Neither is weekly and no
single grain fits both.

Two source tables at natural grain, insider_transaction and
institutional_holding, with the compute layer deriving insider_net_90d_usd,
distinct_buyer_count and inst_ownership_change into flow_daily at
ticker-by-day.

This mirrors what the design already does everywhere else, where price_daily
is ingested and indicator_daily is derived from it. Ingest grain follows the
source; consumption grain follows the screen.

Supersedes flow_daily's ticker-by-week declaration in SCHEMA.md.

**D-62 A filing date is unknown when it is null, equal to its period end, or
not at least one day after it. An unknown row becomes readable at period end
plus that ticker's own widest clean gap observed to date.** `ACTIVE`
Supersedes D-57, whose 65-day constant came from 56 gaps across the newest
eight quarters of seven names. Across 454 gaps the range is -16 to 210 days
and 38 exceed 65, so the rule as written would have read those 38 quarters
before they were public. That is the failure invariant 12 exists to prevent,
arriving through the rule written to prevent it.

A universal constant cannot work. Set to the observed maximum it makes every
well-behaved name wait seven months; set anywhere lower it is early on some
name. The distribution clusters: one ticker accounts for 23 of the 38
breaches. So the substitution is per ticker, drawn from that ticker's own
filing behaviour, which is the same self-referential grammar the screen
floors already use.

Only gaps observable before the read date count toward a ticker's widest.
Using its full history would decide today's readability from filing
behaviour that has not happened yet, which is lookahead wearing the costume
of a fix for lookahead.

A ticker with fewer than four clean gaps observed is excluded from the
universe rather than assigned a guess. Four is a floor for having any view
of a name's filing behaviour, not a claim about its sufficiency.

Three cases the eight-quarter window hid, all now covered by the same rule.
Nulls exist and were absent only from the newest quarters. Negative gaps
exist, being filing dates before their own period end, which is impossible
in fact and so is unknown rather than early. And equality remains what it
always was.

**Stated limitation: `filing_date_effective` is itself as-of, and is accepted
that way** [M.2]. It is computed at ingest from the ticker's widest clean gap
known at that moment. A backfill reading a row dated three years ago therefore
uses a window derived partly from filing behaviour that had not happened yet.

The direction is what makes this acceptable. A widest gap only grows, so a
window computed later is at least as wide as the one that would have been
computed then, and the affected rows become readable later than they truly
would have been rather than earlier. That is the conservative direction, and
this whole decision exists to prevent the other one. Recomputing the window at
read time would remove the effect and costs more than the distortion, on a
field every fundamental read filters on.

The universe exclusion is not affected: its count is computed against the date
being built rather than stored [M.1]. This limitation is confined to the width
of the substituted window on rows that have one.

**D-63 A spent prompt's body may be corrected to match the text actually
issued, and may never be changed for any other reason.** `ACTIVE`
Commit 2901ab2 rewrote four passages of an archived prompt four minutes
before P.1, on the grounds that the archive did not match what the session
was given. Nothing in the corpus authorised that, and CLAUDE.md section 14
states flatly that a spent prompt is never edited once run.

The edit was legitimate and the rule was incomplete. Prompts are composed
outside the repository and archived separately, so the archive and the
issued text can diverge by ordinary accident. An archive that records
something other than what was asked is not serving its purpose, and
correcting it restores the record rather than damaging it.

The distinction that makes this safe: correcting archive to issued text is
permitted; changing issued text to what should have been asked is not. The
first makes the record true, the second makes it flattering. Every such
correction states in the header that it happened and why.

This does not reopen the general rule. A body edit for any other reason
remains prohibited, and scope added mid-session is still a divergence
recorded in PROGRESS.md rather than a retroactive edit.

**D-66 Phase status is recorded in `PROGRESS.md` and nowhere else.**
`ACTIVE`
BUILD_PLAN carried a status field per phase, duplicating PROGRESS for all
twelve. The duplication crossed an authorship boundary, so the field could
only be updated by hand after the fact and went stale on the first phase
that ran. BUILD_PLAN holds what is decided; PROGRESS holds what happened.

**D-67 The sign-off procedure is two steps, and the grep-checkable
invariants are checked by CI rather than by a person.** `ACTIVE`
The five-step procedure was calibrated for phase P, whose deliverable was
measurements feeding decisions. Applied to phases whose deliverable is a
passing test suite it produced records rather than findings: across phases
P and 0 the reconciliation step and the corpus version bump caught nothing,
while the checks that did catch something were the code-against-design and
number-against-source ones. Phase 0's conformance pass found exactly one
invariant breach in the whole tree, which guards.ps1 now finds on every
push [O.1]. The cost of the removed steps was days per phase and eleven
correction passes on phase P alone. What replaces them is mechanical and
runs every time rather than once.

**D-64 ~~The row count alert threshold is not revised, and the question is
deferred until settled counts exist.~~ The absolute row-count floors stand.**
`ACTIVE`
The deferral is closed at the foot of this entry. The original reasoning is
kept rather than replaced, because it was the reasoning that turned out to be
right and a closure that deletes it reads as though the answer was obvious.
D-59 alerts below 45,000 rows. The probe's 2026-08-04 finished at 44,708,
which would alert, and that looks like a bound set too high.

It is not evidence of that. The day was read three times in one evening at
44,665, 44,686 and 44,708, rising each time, and the reads stopped rather
than converged. Its final count is unknown. A day still accreting says
nothing about where a threshold for settled days belongs, and revising the
bound on it would be loosening a bound because a measurement missed it,
which CLAUDE.md section 11 prohibits.

D-65's settledness check is what produces the evidence, since a count is
only meaningful once the day is known to be final. The question is asked
again after phase 1 has accumulated settled counts, read at sign-off. It is
a query against price_daily, not a measurement task.

**Closed at 1.9, and the floors stand.** 2026-08-04 finished at 50,228 rows.
The probe read it at 44,665, 44,686 and 44,708 and stopped rather than
converged. Settled days now read 50,029, 50,148, 50,204 and 50,228, a spread
of 0.4 percent.

D-59's floors are unchanged. 44,708 sat above the 40,000 abort floor and
inside the 40,000 to 45,000 alert band, so no movement of those floors would
have caught that day without also rejecting settled days. The absolute floors
answer "is this file catastrophically short". They were never the tool for
"is this file still filling", which is D-70's check.

**D-70's two values, seeded at 1.13.**

`freshness.settled_fraction` = 0.95. The separation corridor runs from 89.2
percent, the measured part-settled ceiling, to 99.7 percent, the lowest
settled observation. The part-settled side is measured across three readings;
the settled side is four consecutive summer sessions and its true spread is
unknown. The bound therefore sits near the measured side, six points above
it, leaving five points for settled variation not yet observed. The asymmetry
supports this rather than opposing it: a false fail costs one stale day and
self-corrects through the fallback, while a false pass runs the night on an
11 percent short universe and looks normal.

`freshness.settled_window_days` = 20. A median, not a mean, and 20 rather
than a handful, because a day loaded short is still in the trailing window
until C02's reload tops it up, and it would drag the reference down exactly
when the test should not loosen. A median over 20 is unmoved by one such day.
20 also matches the window already used for median dollar volume.

**Watch item, recorded rather than rediscovered.** The first low-volume
holiday week is what would move 0.95. If a genuinely settled session between
Christmas and New Year comes in below it, the value is too high and rises
with that observation recorded. Written here before the week arrives, because
`CLAUDE.md` §11 forbids loosening a bound because a measurement missed it,
and the difference between that and a bound set on an unobserved regime is
the reasoning being on record first.

**D-65 The freshness guard checks recency, completeness and settledness,
and these are three different things.** `ACTIVE`
ARCHITECTURE.html section 4 asserts the latest price date equals today.
Checkpoint 1.2 rejects the most recent available day as still accreting.
Both cannot hold, and the architecture is the half that is wrong: it was
written before the probe found a session accreting for hours and into the
following evening.

Recency. The newest date in price_daily is not older than the most recent
completed trading session. This catches a provider that has not updated and
a run that was missed. It does not assert today, which the settled-day rule
makes false.

Completeness. D-59's thresholds are unchanged, abort below 40,000 and alert
below 45,000, subject to D-64.

Settledness. ~~A date is unsettled if re-fetching it returns more rows than
are stored for it.~~ [superseded, D-70] Both figures already exist, so this is a comparison
rather than a threshold and no new bound is introduced. The probe's
2026-08-04 fails it three times within one evening.

Ingest loads the newest date passing all three. A date failing settledness
is re-read on a later run rather than discarded, because it is incomplete
rather than wrong.

**D-68 Every stage write is idempotent on the table's own grain.** `ACTIVE`
The rails define no unit of work. `StageData` opens a connection per call
and `StageRunner` wraps nothing in a transaction, so a stage that throws
leaves its writes committed while `run_log` records `rows_written` as
unknown. `ARCHITECTURE.html` section 4 states that any stage can be re-run
against an earlier night without side effects, and `CLAUDE.md` section 6
states that a stage completes or it fails the run. Neither was enforceable,
and phase 1 builds seven writing stages.

A re-run of any stage over any date replaces rather than duplicates. Where
a table carries a surrogate identity key the grain is declared as a unique
index in the migration that first writes it, because naming a conflict
target is not enough: `ON CONFLICT` against a table with no matching
constraint raises before a row is written. `insider_transaction` and
`events` are the two such tables in this phase, and `0002` gives each one.

A transaction per stage is the alternative and is rejected. Phase 3 runs
these same stages over five years, and one transaction spanning twelve
million rows is its own failure mode. Idempotence costs an index and
survives being interrupted; a long transaction costs nothing until the
first time it does not complete.

A future multi-table stage with no natural key reopens this. So does a
provider that files two rows a stage cannot tell apart, which is why 1.7's
sweep answers whether its tuple is genuinely unique against real rows
rather than by assumption.

**D-70 Settledness is measured against the trailing population, not by
re-fetching.** `ACTIVE`
Supersedes the settledness mechanism in D-65. D-65 otherwise stands: recency
and completeness are unchanged and the three checks remain three.

Re-fetching a date inside one run detects nothing. 2026-08-06 read back to
back returned 44,204 both times, because accretion runs over hours while two
calls are seconds apart. Repairing it with a count stored by an earlier run
was rejected: a stage is a pure function of its date and config version
[`CLAUDE.md` §5, §6], and a guard whose verdict depends on what it saw during
a previous wall-clock run is not. Two databases holding identical
`price_daily` contents would disagree.

A date is settled when its row count in `price_daily` is at or above
`freshness.settled_fraction` of the median row count of the last
`freshness.settled_window_days` dates strictly before it. Computed from
`price_daily` alone, on first sight, with no dependence on run history. The
fallback is unchanged: walk back from the newest date until one passes all
three checks, and return it.

This removes the ordering constraint the re-fetch implied between C02 and
C07. `RUNBOOK.md`'s 17:30 and 17:40 stand, and C07 makes one provider call
rather than two.

**D-71 A short page from an exhausted server is recorded, not fatal.** `ACTIVE`
`sec-filings/{t}/form4` reports a `meta.total` higher than the number of rows
it delivers. Measured over 250 tickers: 43 short, 104 rows missing of
101,325, and all 43 had walked the server's own pagination to its end. The
case the paging check was written for, a client that stops asking while pages
remain, occurred zero times.

Two failures were sharing one exception and they separate cleanly.

A loop that terminates while `links.next` is present is this client failing
to ask. It stays fatal, at exactly its current strictness, because nothing
about the provider changes what our own defect deserves.

A loop that terminates because `links.next` is absent, having collected fewer
distinct rows than `meta.total`, is the provider disagreeing with itself.
Asking again cannot recover the rows, and halting means a universe pass can
never complete. It is recorded and the stage continues.

No threshold is set and none is to be added later without evidence gathered
after this decision was written. Every candidate value would have been chosen
against data already seen, which is what §11 forbids. The rule is structural
instead: the distinction is which condition ended the loop, and that is
observable rather than judged.

The run log carries, per run, the count of tickers that under-delivered and
the total row shortfall. Today's figures are the baseline. Tolerating a
discrepancy without measuring it is how it stops being visible.

Where the shortfall sits is recorded per affected ticker from the pages
already collected: a final page below `page[limit]` puts the missing rows at
the oldest end of a history and outside every trailing-90-day window, while a
short interior page puts them inside one. If the former dominates,
`insider_net_90d_usd` and `distinct_buyer_count` are untouched and phase P's
S4 base rate can be answered without qualification.

**D-72 The store-wide config version is a count, not a maximum.** `ACTIVE`
Nothing in the corpus defined a store-wide config version; only `MAX(version)`
per key. Phase 4 stamps one on every attribution row and the tuner segments by
it, so it has to distinguish configurations.

A maximum over per-key versions does not. Keys at 3, 1, 1 give 3; changing the
second key gives 3, 2, 1 and still gives 3. Two different configurations share a
stamp from the second change onward, and segmenting on it would pool results the
tuner exists to keep apart.

The store-wide version as of a date is ~~the count of rows in `config_rows` whose
`set_at` is at or before that date~~ [amended, the mechanism counted rows and had
to count changes] **one plus the count of rows whose `version` is greater than one
and whose `set_at` is at or before that date**. A key's initial seed carries
`SeedInstant` and is backdated by design, so counting it would make seeding a new
key raise the version for every past date, and a backfill re-run would stamp a
different version on identical data. A seed extends the configuration's schema;
only a revision changes the configuration in force. Append-only insertion makes it
rise by exactly one per change, so distinct configurations get distinct values, it
resolves as-of by the same rule as every key, and no column is added. ~~It begins
at the seeded key count rather than at 1.~~ [amended with the mechanism] It begins
at 1 however many keys are seeded.

The null case is not arithmetic and is asserted separately. No row at all in force
as of the date returns null, because one plus zero revisions is 1 and 1 is a real
version, so the sum cannot tell a seeded-and-never-revised store from an empty one.
A date before the seed fails the run rather than being stamped.

**D-73 Specs are clean, records keep their strikes.** `ACTIVE`
Amends the strike-in-place convention in `CLAUDE.md` §13.

A struck line states a third time what `DECISIONS.md` and `CHANGELOG.md` already
hold. That duplication is the one open item 6 exists to close, and applying it to
the source of truth makes the document that constrains everything else the hardest
one to read. A reader parsing live text from dead text on every visit is paying a
cost the register was built to remove, and a session skimming can take struck text
for live.

~~In documents read to know the current state, a removal is a clean edit:
`ARCHITECTURE.html`, `SCHEMA.md`, `CONFIG_REFERENCE.md`, `RUNBOOK.md`. Delete the
superseded text, keep the decision citation at the point of change, and record the
prior value in `CHANGELOG.md`.~~

~~In documents that are the record, strikes stay. `DECISIONS.md`, because a
superseded entry must keep the reasoning its successor replaced. `PROGRESS.md`,
because a log's corrections are its content.~~ [amended, the four and the two were
written as the definition where they had to be examples of a test]

**The rule is a test on what a document is read for, and it is stated in the two
sentences below. Neither names a document, deliberately, so a document written later
classifies itself instead of waiting to be added to a list.**

A document read to know the current state takes clean edits.

A document that is the record keeps its strikes, because a superseded decision must
keep the reasoning its successor replaced, and because a log's corrections are its
content.

**The mechanism for a clean edit**, which is not part of the test: delete the
superseded text, keep the decision citation at the point of change, and record the
prior wording in `CHANGELOG.md`.

**Examples of the test rather than the definition of it.** Reading it as a definition
is what left three documents unclassified and one carrying both conventions.
`ARCHITECTURE.html`, `SCHEMA.md`, `CONFIG_REFERENCE.md` and `RUNBOOK.md` are read to
know the current state and take clean edits. `DECISIONS.md` and `PROGRESS.md` are the
record and keep their strikes. Runtime prompt files remain clean deletions, unchanged
from before and for a different reason: the model reads them at execution time and
cannot tell struck text from live.

**`BUILD_PLAN.md` is a spec under the test.** Its checkpoint tables, done-when lines
and carried obligations are all read to know what is currently owed, so it takes clean
edits from here.

**Its existing strikes stay for now, and that is a sweep rather than part of this
amendment.** Removing one requires confirming that its decision names what it removed,
which is the condition below, and the file carries several from phases P, 0 and 1. So
the file is mid-convention: earlier supersessions are struck in place and phase 3's
fourth done-when line is a clean edit. Recorded in `PROGRESS.md` as an outstanding
finding rather than left for the next editor to rediscover.

`METRICS.md` and `SCREEN_LIFECYCLE.md` were the other two the list did not reach.
Each classifies itself under the test at its next edit rather than here, since
neither has a strike to resolve today.

Existing strikes in ~~the four spec documents~~ [amended with the test] **any document
the test makes a spec** are cleaned under this decision, one condition: no strike is
removed until its decision names what it removed. Where a decision does not,
`CHANGELOG.md` records the text before the deletion. That condition is why
`BUILD_PLAN.md`'s existing strikes are a sweep rather than an edit taken here.

`CHANGELOG.md` is reopened for that purpose. D-67 closed it because corpus
versioning had stopped catching anything, and that reason still holds: no version
number is resumed and no sign-off step returns. What returns is the file, with a
different job. A clean edit destroys the only copy of a spec document's prior
wording unless something else holds it, and this decision makes that file the
something else.

**D-74 The Reads column names where a stage gets its ticker list.** `ACTIVE`
§3 gave four ingest components a Reads column containing only their endpoints.
`FundamentalsIngestor` declares `["price_daily", "security"]`; `SentimentIngestor`,
`FlowIngestor` and `EventsIngestor` each declare `["security"]`. `DeclaredAccess`
enforces those sets at runtime, so the catalogue and the running system have
disagreed since 1.4.

The omission is not cosmetic. A per-ticker endpoint needs a ticker list and the
catalogue never said where any of them gets one. That silence is what allowed the
fundamentals pool to be drawn from `security`, closing the universe over itself: a
name needs fundamentals to be admitted, so once `security` was populated only its
own members could be fetched, and coverage froze at 679 with no error and entirely
plausible output.

A pool source and an ordering source are different reads and are named as
different reads. Standing: any per-ticker component states in its Reads cell where
its ticker list comes from and what that read is for.

No code changes. The document begins describing what the code already does.

**D-75 FlowIngestor runs nightly.** `ACTIVE`
Supersedes the cadence in §3's C05 Runs cell, which read `Weekly`.

`RUNBOOK.md` line 17 puts flow at 17:45 with fundamentals and events. The two
documents have contradicted each other since the corpus was written and phase 1's
nightly sequence followed RUNBOOK without recording a choice.

Weekly was set when this component also carried short interest and wrote
`flow_daily`. D-58 removed the first, D-61 moved the second, and the cadence was
never revisited.

The binding constraint is coverage latency, not filing latency. The stage rotates
`flow.max_tickers_per_run` names of 2,841 per run, so a universe pass takes about
twelve runs. Nightly gives complete coverage in roughly twelve days against the
flow screen's trailing ninety-day window. Weekly gives it in about three months,
which is the window itself, so a name's insider figures would be stale by the full
length of the period they are computed over.

Recorded because the earlier argument was wrong: it reasoned from Form 4 arriving
within two business days, which was never binding, and it was made while the stage
had a fixed head and no cadence could have mattered.

**D-76 Writes are stated in §3 and in `SCHEMA.md`, and nowhere else.** `ACTIVE`
Closes open item 6.

§16's store matrix carries Written-by and Read-by columns repeating §3's Writes
and Reads, which repeat `SCHEMA.md`'s writer declarations. Every write-column
defect in passes K through N came from that duplication, and its stated trigger
fired at phase 0.

Both columns are dropped. Two statements are acceptable where three were not, and
the reason is mechanical: the 1.10 conformance test asserts §3's registry against
`SCHEMA.md`'s writer declarations in both directions, so those two are held
consistent by a test. The store matrix's columns were checked by nothing, which is
why they drifted.

**D-78 The derived sentiment metrics are computed by a compute-layer stage from
`sentiment_daily`.** `ACTIVE`
`sentiment_daily` is what the provider sends. The three forms the screens rank on are
derived from it, exactly as `flow_daily` is derived from the two flow source tables and
`indicator_daily` from `price_daily` [D-61]. C35 SentimentEngine reads `sentiment_daily`
and `security` and writes `sentiment_derived_daily` at ticker by day. Windows named in a
column stay constants rather than keys, as FlowEngine's 90 does.

S3 ranks on `article_count_z_own_90d`, `sentiment_delta_7v30` and `sentiment_7d_level`
and on nothing else, S5's stabilisation gate reads the first two, and §7's fixed core
carries two. `sentiment_daily` holds `ticker`, `date`, `article_count` and
`sentiment_score`, and no §3 catalogue row gave any component a path from one to the
other. The rule was already decided by D-12; what was missing was a component and a
store.

**D-77 A table's writers are whoever its heading names.** `ACTIVE`
Replaces the enumeration in `SCHEMA.md`'s opening.

`SCHEMA.md` said three splits over five tables and no fourth. The percentile engine
updates `_pctile` columns on `indicator_daily`, `valuation_daily`, `flow_daily` and
`sentiment_derived_daily`, which is four more splits over four more tables. Seven over
nine.

That sentence predicted this in its own last line: a rule that counts exceptions gets
longer every time the design is correct. It was a count of what existed when it was
written rather than a rule, and no amendment to the number fixes that, because the next
correct design would need an eighth.

The count is removed and nothing replaces it as a count. A table's writers are whoever
its heading names, each declaring the operation and the column set it owns, and nothing
else may write there. INVARIANT 10 as amended is per operation, so a metric engine
inserting and the percentile engine updating a disjoint column set are two claims rather
than a conflict.

The brake the count provided is not lost. Adding a second writer now requires editing an
authored document, which a build session cannot do, where a count is prose a build
session can read past. That is checked on every push rather than by whoever remembers the
sentence.

**The property this arrangement can break, which no test covers.** The failure mode here
is not a conflict the registry catches. It is a metric engine re-running and blanking the
percentiles the percentile engine wrote. That is safe today only by construction, because
the staged path builds its staging table from the written columns alone, so the upsert's
`SET` leaves `_pctile` untouched. D-68's idempotence is guaranteed per stage and says
nothing about columns a stage does not write, and this is the first place in the codebase
where two stages share a table's rows rather than a table.

**D-83 A spec states the rule that governs a set, not a count of it.** `ACTIVE`
Settles what `ARCHITECTURE.html` §04 and `SCHEMA.md` say about the technical columns,
and states the general rule the three instances of this defect share.

`ARCHITECTURE.html` §04 said `~40 technical columns per name` and `SCHEMA.md`'s
`indicator_daily` section said "Roughly forty technical columns plus their
percentiles". Phase 2 built fifteen and `PROGRESS.md` records fifteen, so two documents
now disagree with a third about the same fact rather than one carrying a stale
estimate that nothing contradicts.

**The count was made at design time and nothing has ever checked it.** It is the third
instance of one defect. The split count in `SCHEMA.md`'s opening was the first,
removed by D-77. The Written-by and Read-by columns of §16's store matrix were the
second, removed by D-76. Each was a number or a list stated in prose that no test
could read, and each drifted at the moment the design turned out to be right in a way
it had not anticipated.

**The count is removed rather than corrected.** Correcting forty to fifteen buys
nothing: it goes stale the first time a later phase adds an indicator column, and the
next reader has no way to tell a checked number from an unchecked one. Both documents
state what governs the set instead. The technical columns are those with a named
consumer, which is the rule phase 2 traced the fifteen from, and `SCHEMA.md`'s own
`indicator_daily` section carries the list.

**The list is checked where the count was not.** `SchemaParityTests` holds
`SCHEMA.md`'s type declarations against the live database in both directions: every
`real` or `double precision` column in the database must be declared in the document,
and every column the document declares must be in the database. Thirteen of the
fifteen technical columns are `real` and are covered by that pair;
`median_dollar_volume_20d` is held to `numeric` by the monetary direction of the same
test [INVARIANT 16]. `base_breakout_flag` is `boolean` and is covered by neither, which
is stated rather than glossed.

**Standing from here: where a spec would state how many of something there are, it
states what decides membership and points at the place a test can read.** A count in
prose is a claim with no owner. A rule plus a pointer is a claim someone can check,
and if nothing can check it, that is worth knowing at the moment the sentence is
written rather than at the sign-off two phases later.

**The rule is not scoped to the four spec documents.** [added after the fact] It applies
wherever a count is stated as fact and nothing checks it. A number a script prints on
every run is read more often than one in a document, and a comment restating a value the
assertion below it already checks is a second statement of one fact, which is the shape
every instance of this defect has had. Both cases were found at the phase 2 sign-off
after the first pass had been scoped to `docs/`: `guards.ps1` named the technical column
count in its header and in the rationale it prints on every run, and
`SchemaParityTests` carried a comment saying seventeen above an assertion of eighteen.

This does not reach a count that a test does read. `SCHEMA.md`'s "Eighteen columns
match the monetary pattern and are `numeric`" stays, because `guards.ps1` asserts it
against the migrations and `SchemaParityTests` asserts it against the live database,
and the paragraph says why it is exact rather than a floor. The distinction is whether
the number is the assertion or a restatement of it: the assertion stays and the
restatement goes.

`PROGRESS.md`'s fifteen stays as it is. It is a record of what was built and of an
estimate that moved to a measurement, not a spec, and records keep their strikes
[D-73].

**D-82 EBIT is not substituted from operating income, and the threshold that decided
it was fixed before the measurement.** `ACTIVE`
Settles whether `ev_ebit` and `roic` may read `operating_income` where `ebit` is absent.

`ebit` is null on the latest readable quarter for 946 of 3,897 names and 847 of those
carry `operating_income`. That is what holds `ev_ebit` to 2,203 names, and the gap is
not neutral: it excludes names by which ones this provider happens to populate a field
for, which is a selection effect on a screen input rather than a property of the
companies.

**The rule was written down before the number existed** [`CLAUDE.md` §11]. Substitute
if the median of `abs(ebit - operating_income) / abs(ebit)` over rows carrying both is
under 2% **and** the 90th percentile is under 10%. The two thresholds are not
arbitrary: `ev_ebit` is ranked inside a cell, so a couple of percent moves a name a rank
or two and is tolerable, while a tail past 10% moves names across deciles and is not.

Measured over 380,281 quarterly rows carrying both: median **1.01%**, 75th **13.9%**,
90th **63.9%**, 99th **942%**. Over the 2,942 latest readable quarters alone: median
**4.90%**, 75th **23.1%**, 90th **84.3%**. The median passes and the tail fails by six
to eight times, which is the exact failure the 90th percentile threshold was set to
catch, so the substitution is refused.

**The selection effect stays open rather than being closed by a worse answer.** What
the measurement establishes is that operating income is not available as a stand-in at
an error a ranked column can carry, not that the gap is acceptable. Whoever revisits it
needs a different input, not a different threshold, and lowering the bound because a
measurement missed it is what `CLAUDE.md` §11 prohibits.

**D-81 `cash_on_hand` reads the reported cash line where the parts line is absent, and
the coalesce to zero is narrowed to short-term investments.** `ACTIVE`
Supersedes the `cash_on_hand` rule in `METRICS.md` §3 as authored at 2.1.

The rule was `coalesce(cash_and_equivalents, 0) + coalesce(short_term_investments, 0)`,
falling back to `cash` only when both parts were absent. It is now
`coalesce(cash_and_equivalents, cash) + coalesce(short_term_investments, 0)`, null when
both `cash_and_equivalents` and `cash` are absent.

**The old rule zeroed an absent part in both directions and only one of them is a
zero.** A company reporting cash and equivalents with no short-term investments line has
no short-term investments, which is the case the original reasoning gives and which
occurs 5 times across the store's latest readable quarters. The mirror case, short-term
investments present with no cash and equivalents line, occurs **1,644 times**, and on
699 of those the reported `cash` line is at least twice the investments figure.
`NFLX.US` carried 28,678,000 under a column that says cash on hand, against a reported
`cash` of 9,099,232,000.

**Which line stands in was measured before the rule was chosen.** Reading another
reported line from the same statement is not inventing data, so the question was only
whether `cash` is the parts line under another name or the total. Over the 8,411
quarterly rows carrying all three where short-term investments are non-zero, `cash`
equals `cash_and_equivalents` **6,710** times exactly and equals their sum **13** times;
at a one percent tolerance, 7,083 against 757. It is the parts line, by two orders of
magnitude, so it substitutes for the part and the investments are still added.

**The measurement and the rule cover different populations, and the gap is recorded
rather than closed.** [added after the fact] The comparison needs `cash`,
`cash_and_equivalents` and `short_term_investments` all present to be made at all, so
it ran over rows carrying all three. The rule fires where `cash_and_equivalents` is
absent, which is exactly the population the comparison cannot observe. The inference is
from one population to another and that is stated rather than left implicit.

The ratio makes the direction decisive anyway. What it leaves is the nine percent: at a
one percent tolerance 757 of 8,411 rows had `cash` matching the sum rather than the
part, so on the order of a hundred and fifty of the 1,644 mirror cases may now carry
their short-term investments twice. Against 1,644 that were previously wrong by orders
of magnitude, and against `NFLX.US` reading 28,678,000 for 9,127,910,000, that is a
residual rather than a reason to hesitate. It is written down so a later reader knows it
was seen rather than missed, and knows what a follow-up would have to reach: a source
that distinguishes the two shapes on rows where `cash_and_equivalents` is absent, which
this store does not carry.

**5 names lose a value and that is the intended direction.** Where neither cash line is
present the column was the investments figure alone and is now null, because an absence
a reader can see is worth more than a number wrong by whatever the cash line would have
been [`CLAUDE.md` §6].

The column is not ranked [`METRICS.md` §6.5], so no percentile moves. It is sent to the
dossier for magnitude and is the denominator of runway against `quarterly_burn_rate`,
which is where the old figure was doing damage.

**D-80 The regime label takes three values, and the benchmark test needs no
threshold.** `ACTIVE`
Settles the enumerated values `market_context_daily.regime_label` holds.

Three values: `risk_on`, `risk_off`, `mixed`.

`risk_on` when breadth is at or above `market.regime_breadth_high` and the benchmark is
above its own 200-day average. `risk_off` when breadth is at or below
`market.regime_breadth_low` and the benchmark is below it. `mixed` otherwise.

The benchmark contributes a sign test rather than a threshold, deliberately. Zero is
already meaningful there, since a series is above or below its own two-hundred day
average, and every threshold is a number someone has to justify and later defend.
Breadth has two because a fraction has no natural cut; the benchmark has none because
it does.

**No minimum run length, and the reason is recorded rather than assumed.** The
three-state design already buffers: a single crossing on either input moves the label to
`mixed` rather than flipping it to the opposite, so the label cannot alternate between
`risk_on` and `risk_off`. What remains is one input sitting at its threshold and moving
between `risk_on` and `mixed`, which splits a boundary period into two rather than
mislabelling either, and that is the honest reading of a boundary period. If it proves
noisier than that in practice, a run length is added with the observation behind it.

VIX is null and contributes nothing. The label is derived from breadth and the
benchmark, and the absence of VIX is recorded rather than allowed to null the label,
since three components read it and a null degrades all three.

**The values are enforced by the database, not by the writer.** `regime_label` becomes
`NOT NULL` with a `CHECK` on exactly those three. Same treatment as
`filing_date_unknown_reason` and for the same reason: this column segments analysis, and
a drifted or mistyped value lands in its own bucket in every segmentation without ever
erroring.

**D-79 `fcf_yield` needs a capital expenditure figure of its own, and
`capital_expenditures` is ingested for it.** `ACTIVE`
Free cash flow computed as cash from operating plus cash from investing reads an
acquisition as capital expenditure and an asset sale as free cash flow. That is S1's
first ranking input, and both errors land hardest on exactly the names the screen is
meant to find: an acquisitive small cap looks like it spends everything it earns, and one
selling a division looks like it generates cash it does not.

The column is one field from the fundamentals endpoint C03 already calls, and
`0002_statement_fields_and_grains.sql` states its own column list is "driven by what
`valuation_daily` needs in phase 2, not by what the provider happens to send", which
covers this. It arrives in `0004` with C03's parse widened to populate it, and
`fcf_yield` becomes TTM cash from operating less TTM capital expenditure.

It is money and its name matches the monetary pattern, so it is `numeric` and it moves
`guards.ps1`'s `ExpectedMonetary` from 17 to 18, which is that number doing the job it
was made exact for [INVARIANT 16].

---

## Screen lifecycle

Authored 2026-08-11 from the design drafted in `docs/SCREEN_LIFECYCLE.md`, which
answers the brief archived at `prompts/spent/design-screen-lifecycle.md`. Authored
before `ARCHITECTURE.html` was touched, because the cells cite them [`CLAUDE.md`
§13]. No entry below rests on a measurement, and none exists to rest on: phase 3 has
not run, screens are phase 4 and the tuner is phase 8. That is the reason the
decisions were taken now rather than inside phase 4 [`CLAUDE.md` §11].

**D-84 A screen has three states and a shadow screen is one of them.** `ACTIVE`
`live` scores and holds slots, `shadow` scores and holds none, `retired` does not
score and keeps its config row. The state is `screens.<id>.state`, so registering a
candidate screen is a config row rather than a deployment, exactly as adding a screen
already was [D-6]. A shadow inherits D-9's floor unchanged and has no threshold of
its own, because D-9's floor is self-referential and is therefore defined for any
screen that has a distribution. C13's only change is which screens it iterates.

The state exists so that a candidate screen can accumulate a record before it can
affect a candidate set, which is the only way to know anything about it that is not
an argument. A screen registered live is a screen chosen by argument; a screen
promoted from shadow is one chosen on a record it built while unable to affect
anything.

**D-85 Shadow screens write `attribution` and never `candidate_set`.** `ACTIVE`
`attribution` gains `surfaced_as`, `NOT NULL` and constrained to `candidate` or
`shadow`, and `score_per_screen` gains each screen's rank alongside its score. The
grain does not change: one row per ticker per day surfaced, with `screens_surfacing`
carrying live and shadow ids together, because the forward return of a name on a date
is one number however many screens surfaced it.

A separate shadow store was weighed and rejected. C21 ForwardReturnFiller would
become two code paths that have to agree on the acquisition and delisting rules, the
point-in-time size bucket, sector and regime would be frozen twice by two writers,
and the tuner's single-table aggregate would become a union [`ARCHITECTURE.html`
§19]. A second store holding the same grain, the same context columns and the same
nine return columns is the shape D-76, D-77 and D-83 each removed.

`surfaced_as` is stored rather than derived for D-80's reason. A derivation resolves
against the live set, which moves on promotion, so a promoted screen would silently
relabel its own history, which is today's definitions applied to a past date
[INVARIANT 4]. And a constraint protects the column where a writer-side check
protects one writer.

A view `candidate_attribution` selects `surfaced_as = 'candidate'`, and every reader
meaning "candidate" reads the view, so the filter cannot be forgotten [`CLAUDE.md`
§5]. Eight readers mean candidate and three mean everything surfaced;
`SCREEN_LIFECYCLE.md` §4.5 enumerates all of them, with three more that a reader
would expect to appear and do not read the table at all. The population filter and
the per-screen grouping filter are different, and C24 needs both, because a candidate
row can carry a shadow id in `screens_surfacing`.

`candidate_set` is unchanged and is therefore the unambiguous definition of the
candidate set.

**D-86 Backfilled observations count toward a shadow's distribution and never toward
a promotion or a retirement.** `ACTIVE`
A shadow needs backfilled scores or it has no D-9 floor for its first 250 sessions
and no record at all for its first year. The condition on counting them is D-58's:
the screen's inputs must be backfillable to the definition the live screen will use,
because a floor drawn from a population the live screen does not share is the defect
D-58 removed short interest for.

A promotion or a retirement reads prospective observations only. Four years of
history arrive at once when the screen backfill runs, so a sample floor counting them
is met on registration day and the rule means nothing. `CLAUDE.md` §11 prohibits
tuning screens on forward returns before the researcher has judged anything, and a
promotion is a stronger action than a slot move. And `VALIDITY.md` §5 and §6 make the
window one macro environment with regime confounding stated rather than mitigated.

**Two documents were read against their surface and neither licenses a promotion on
backfill.** `VALIDITY.md` §6's "backfill is never used to evaluate the researcher,
only the screens" has model training contamination as its threat and the researcher
as its subject; it says backfill is unusable for the researcher and not that it is
sufficient for a screen. `BUILD_PLAN.md` phase 8's "the tuner moves slots on
backfilled data" is a build verification criterion and has to be, phase 8 having no
prospective night to run against. This is the clause to revisit if either reading is
wrong, and nothing else in the lifecycle moves with it.

**D-87 A screen is retired on sustained peer-relative underperformance, and the tuner
is what measures it.** `ACTIVE`
The measure is 21-day peer-relative return and the hit rate on the same column, never
absolute alpha and never the SPY column [D-42, INVARIANT 5]. A rule that can retire a
screen is more destructive than one that can cut its slots to four, so INVARIANT 5
binds on it at least as hard. Twenty-one days because it is the horizon
`VALIDITY.md` §4 already pre-registers the primary claim at, and because D-34's
40-day time stop means an edge appearing only at 63 days is one this portfolio cannot
hold to.

**The sample floor.** At least 1,000 prospective observations, which is
`VALIDITY.md` §4's per-side count used unchanged, or 250 paired observations where
the screen is paired against a named incumbent. The 250 encodes `rho = 0.75` and
nothing else, since the variance of a paired difference is `2 * sigma^2 * (1 - rho)`
and a paired test matches an unpaired one's power at `n * (1 - rho)`. The correlation
is observable once both screens have run over the same names on the same dates, and
re-setting the floor on a measured one sizes the instrument rather than choosing the
answer, which is the single after-the-fact adjustment `CLAUDE.md` §11 permits.
Provided the measurement precedes the comparison it sizes: sizing from a correlation
measured over the same window the promotion is then decided on lets the floor be
chosen by the data it is applied to, and that is result-shopping.

**The promotion bar.** A margin of `sqrt(2 * ln(k))` standard errors, against the
live family's mean for an addition and against zero for a paired shadow. One rule for
both, because promoting whichever of k paired shadows wins selects the maximum of k
exactly as it does for additions: pairing shrinks the standard error and does not
remove the selection across tests. `k` counts the screens of that kind that met the
sample floor at that evaluation, read off `screen_evaluation` and not off the
registry, and counted within a family rather than across it. A correction is for the
tests that could have been acted on, and a screen below the floor could not have been
promoted whatever it did. It is inert at `k` of one in both families, which is
correct in both, and it is not gameable: the floor is a versioned config row and
eligibility is a recorded fact per evaluation.

**Nomination, not execution.** A nomination requires three consecutive monthly
evaluations, so a quarter is the shortest path to one and a single bad quarter cannot
retire anything. A fail nominates; a human retires, at D-88's boundary.

**Why the tuner.** C22 ScreenTuner computes the measure for every registered screen
and allocates slots among the live ones only. That measure is already what it
computes, and a second component computing it is one fact stated twice, which is the
defect D-76, D-77 and D-83 each removed. INVARIANT 2 is not in the way, since the
tuner is not a screen and has read every screen's results since it was designed.
D-43's "touches nothing else" is about what the tuner tunes, and recording a
measurement is not tuning.

Its write set gains `screen_evaluation`, screen by evaluation date, insert only.
`config_rows` is versioned configuration and would resolve a measurement as config
[INVARIANT 13], `screen_history` is the wrong grain and C13's table [INVARIANT 10],
and the sustained-fail rule needs the consecutive count to be a record rather than a
recomputation, since recomputing applies today's definitions to a past evaluation
[D-40].

**D-88 A promotion or a retirement splits the primary claim, so none executes before
the claim has its sample and all due are bundled into one boundary.** `ACTIVE`
The set of screens is the set of rubrics [`ARCHITECTURE.html` §07], so promoting or
retiring one changes the rubrics and the dossier, which `CLAUDE.md` §12 lists among
the changes that invalidate comparisons across the boundary. It therefore splits the
primary claim's history and not only the screen's own record.

So nothing executes until `VALIDITY.md` §4's pre-registered sample is reached,
meaning at least 1,000 observations on each side of BUY against PASS at 21 days, and
its minimum evaluation period of 12 months regardless. Every nomination due at that
point goes into one boundary. Four screens promoted one at a time is four boundaries
and five incomparable segments where one bundled change is two, which is the outcome
`CLAUDE.md` §12's instruction to bundle exists to avoid.

**D-89 The slot pool stays at 40, D-7's 2/3/3 is restated as a proportion, and the
feasible live-screen range is four to ten.** `ACTIVE`
Large takes `floor(slots / 4)`, the remainder splits between mid and small with the
extra to small. That is exactly 2 / 3 / 3 at eight slots and integral at every count
from four to twelve, which is the range D-43's floor and cap have always permitted.
The gap is pre-existing: the tuner has been able to reach any count in that range
since it was designed and 2/3/3 is defined at eight alone, so retirement makes the
gap reachable more often rather than creating it.

Two properties are why this proportion rather than another. The large share never
exceeds 25 percent at any count, so the sum over any allocation totalling 40 is at
most 10 and **D-7's megacap bound of ten of forty holds at every live-screen count**
rather than only at five screens of eight. And the guaranteed small-cap places are
minimised at exactly today's five screens of eight, every other feasible allocation
giving 16 to 20, so **D-7's small-cap floor of fifteen is a floor across the whole
reachable space** rather than a figure that happens to hold today.

With a floor of four and a cap of twelve, forty is reachable only with four to ten
live screens. A retirement leaving three does not execute and becomes a design
decision requiring a promotion in the same boundary or an authored change to the
pool, the cap or the floor; a promotion taking the count to eleven does not execute
either. A retiring screen's slots return to the pool and the tuner redistributes at
the next monthly run with the floor and the cap untouched, which is a normal monthly
move rather than an exception because the arithmetic above guarantees one exists. A
promoted screen enters at the floor of four.

---

## Ingest rotation

**D-91 The fundamentals rotation orders on a record of the attempt, read strictly
before the run date.** `ACTIVE`
Settles three choices the fix made that a later session could reasonably make
differently: where the record lives, what it records, and when it is readable.

**The failure it corrects produced no error and survived two phases.** C03 ordered
never-fetched first, where fetched meant any row in `fundamental_snapshot`. Once the
pool was covered that group was empty, ticker ordinal decided everything, and the
same alphabetically-first 500 names were selected on every run afterwards, for ever.
`capital_expenditures` reached 482 tickers running contiguously from `A.US` to
`CCBG.US` and stopped. Two consecutive runs wrote an identical 44,365 rows over an
identical 500 tickers.

**The figures that make it a decision rather than a tidy-up.** `fcf_yield` covered
464 of 3,897 `valuation_daily` rows when phase 2 recorded it and 464 of 5,713 on
2026-08-11: the same 464 rows, so coverage fell from 11.9 percent to 8.1 percent
while both row counts stayed healthy and nothing anywhere reported a fault. It is
S1's first ranking input and three of five screens read fundamentals.

**One, the record is of the attempt and lives in its own table.**
`fundamental_fetch_attempt`, one row per ticker, written for every selected ticker
whether or not the fetch yielded rows [0006]. An absent row means never attempted; a
null `last_yield_date` means attempted and never yielded. Those are different facts
and the old ordering could not tell them apart.

Both obvious alternatives fail on the same case. A `fetched_at` column on
`fundamental_snapshot` moves only when rows are written, so a ticker whose fetch
returns nothing never moves and holds the head of the rotation for ever, which is the
fourteen-404s residue of the flow ingest one component over. A column on `security`
fails differently: C03's pool is the candidate set, deliberately broader than the
universe [1.8], so a pool member with no `security` row has nowhere to record an
attempt.

**No counter column.** An attempts tally would increment on a re-run of one date, and
D-68 requires every stage write to be idempotent on the table's own grain. Every
column is a function of the last attempt alone.

**Two, attempts are read strictly before the run date.** A re-run of one date
therefore sees the state the first run saw and selects the same names, so the stage
stays a pure function of its date and config version [`CLAUDE.md` §6]. The rotation
advances between dates and never between runs.

Ordering on an attempt timestamp looks equivalent and is not: it advances on every
run, so replaying a night would fetch a different set and the night would stop being
reproducible. This is the discipline every fundamental read already applies to
`filing_date_effective` [INVARIANT 12, INVARIANT 13].

**Three, universe membership is a tiebreak and not a tier.** Ranked above freshness
it starves every pool member outside `security` permanently, because the universe is
refreshed on every run and is therefore never exhausted. That is the same defect this
decision corrects, wearing different clothes. Among names of equal staleness a
universe member goes first, which is the preference the old tier reached for without
the starvation. Coverage still precedes freshness while coverage is incomplete: a
name absent from the store cannot be screened at all, where a name whose figures are
a few days old still can.

**The run log separates new from refreshed and names the oldest attempt in the
selection**, so a frozen rotation is visible where a reader already looks rather than
in a query someone thought to write. A run that is entirely refreshed while the pool
still holds never-attempted names is the defect; entirely refreshed on a fully
attempted pool is the rotation working.

**Not closed by this.** C05 `FlowIngestor.SelectionFor` carries the identical defect
and is untouched, the two stages sharing no selection code. It is carried in
`BUILD_PLAN.md` against phase 3.

---

## Backfill

Authored 2026-08-11 at phase 2's sign-off, from the plan drafted in
`prompts/BuildPlans/phase-3-backfill.md` §3. Authored before any phase 3 code, because
each blocks a checkpoint that cannot be built without it [`CLAUDE.md` §13].

No entry below rests on a measurement and none exists to rest on: the backfill has not
run, and D-92 and D-94 are what decide what it will produce [`CLAUDE.md` §11]. The four
numbers run in the order the decisions were drafted rather than in the order the plan
presents them, and nothing is missing between them.

**D-92 Universe membership, size bucket, market cap and sector are stored per date in
`security_daily`. `security` keeps identity alone.** `ACTIVE`
C11 ranks inside size-bucket-by-sector cells [D-10] and `security` carries one row per
ticker, so a backfilled 2021 date would rank every name in its 2026 cell. The cell is
the population D-10 defines, and a percentile computed over a slightly wrong cell is not
inspectable afterwards: nothing downstream can see it or correct it.

Deriving it at read time was weighed and does not work. The bucket itself derives, being
a comparison of one name's market cap against two absolute config floors
[`UniverseBuilder.Bucket`], but C11's cell is `(size_bucket, sector)` and the
fifteen-member test counts the non-null population in that cell on that date, so ranking
one row needs every other row's bucket and sector on the same date. Market cap as-of
needs `shares_outstanding` readable on `filing_date_effective <= date`, and sector is a
provider call with no history from this source at all. The derivation is therefore the
whole store recomputed per date, which is a store.

A month-end grain was weighed and rejected for the reason the derivation was rejected
against: it puts an approximation underneath cell membership rather than beside it.

`security_daily` is ticker by date, written by UniverseBuilder, carrying `sector`,
`size_bucket`, `market_cap` and `is_active`. `security` keeps `ticker`, `name`,
`first_seen`, `last_seen` and `delisted_date`, which is identity and lifespan. Every
reader meaning "the universe on a date" reads `security_daily`; a reader meaning "this
ticker" reads `security`.

This subsumes the `is_active` obligation rather than sitting beside it. C01 having no
path that deactivates a name was a phase 1 finding [`PROGRESS.md`, 2026-08-09] and it
stops mattering for any read, because membership is reconstructable per date by
construction rather than by a flag someone has to remember to clear.

Sector is the one column that is not point-in-time, and that is stated rather than
hidden. This provider carries no sector history, so a ticker's sector is fetched once and
carried across the window. A reclassification inside the window is invisible, which is a
bounded distortion of cell membership and is recorded in `PROGRESS.md` rather than
proxied.

**C01's cadence does not change and that is deliberate.** `ARCHITECTURE.html` §3 runs it
weekly on Sunday. The backfill evaluates membership on the same weekly cadence and C11
reads the most recent `security_daily` row at or before the date, so backfilled cells sit
on the identical population rule as live ones. That is D-58's principle applied to
membership: a floor drawn from a population the live system does not share is not a
floor.

**D-93 A backfill is the registered components executed over a range. No component exists
that a night does not run.** `ACTIVE`
`SCHEMA.md` names PriceIngestor as `price_daily`'s writer and IndicatorEngine as
`indicator_daily`'s, and the conformance test asserts no two components claim the same
component-table-operation triple [INVARIANT 10]. A `HistoricalPriceIngestor` would be a
second claim on the same triple, and `ARCHITECTURE.html` §3 would not name it, which
`RegistryNameTests` catches. Neither is an accident of the machinery: a loader with its
own arithmetic is a second implementation of every formula, and the reference fixtures at
2.5 to 2.8 would then cover half of what runs.

So the range mode is a second entry point on the same class.
`IBackfillStage.ExecuteRangeAsync(from, to)` beside `IStage.ExecuteAsync(date)`.
`WriteSet`, `ReadSet`, the registry and §3 are all unchanged.

Config resolves per date being computed, not once for the range [D-43, INVARIANT 13]. A
window key resolved at the range end would give a backfilled date a different answer from
a nightly re-run of the same date, which is exactly the equality phase 3's fourth
done-when line asserts.

Idempotence rather than a transaction is what makes an interrupted backfill resumable,
which D-68 already states in as many words: one transaction spanning twelve million rows
is its own failure mode.

**D-94 The backfill window start is a stored date, and the compute window is the only
bound on how deep the price load goes.** `ACTIVE`
`eod/{t}` costs one unit for five years or twenty, so the load depth is a disk decision
rather than a unit one and the loader takes whatever the call returns. What is bounded is
the range compute runs over, and it is bounded by a date rather than by a count of years.

A count of years resolved against the run date moves the window on every re-run, so two
backfills over one store would compute different date sets and phase 3's fourth done-when
line could not be tested at all. A stored date resolves as-of by the same rule as every
other key and gives the same window whenever it is read [D-43, INVARIANT 13]. It is also
the D-83 case: a bound stated as a number of years is a claim about what a later phase
will need, where the compute window is a bound someone can check.

There is a floor underneath it that the key does not show. `ConfigSeeder.SeedInstant` is
2020-01-01 and `RequireVersionAsync` fails for any earlier date [D-72], so no stage
resolves config before it whatever `price_daily` holds. `price_daily` reaching further
back than the pipeline can compute for is the intended state rather than a defect: the
extra history costs bytes now and cannot be re-fetched cheaply once it is the deep past.

**D-95 FlowIngestor orders on its own attempt record, and the two rotations share their
ordering rule rather than a table.** `ACTIVE`
`FlowIngestor.SelectionFor` carries C03's defect unchanged: never-fetched first, then
ticker ordinal, so it freezes on the alphabetical head once the pool is covered, and the
14 of 250 that answer `404 Symbol not found` stay never-fetched and are re-asked on every
run [D-91, which names this as not closed by it].

`flow_fetch_attempt`, one row per ticker, four columns, written for every selected ticker
whether or not the fetch yielded rows, read strictly before the run date. That is D-91's
shape applied to the component it explicitly left open, and its three reasons carry over
unchanged: the record is of the attempt, attempts are read strictly before the run date,
and universe membership is a tiebreak rather than a tier.

A shared table was rejected: two components writing one table is two claims on one triple
[INVARIANT 10]. What is shared is the ordering function, so the two rotations cannot
drift apart the way the code and the catalogue did.

**D-96 The earnings history is captured on the sweep that pays for it.** `ACTIVE`
`Earnings::History` sits in the `fundamentals/{t}` payload, which 3.7 fetches once for
the whole pool. Capturing it there costs nothing; capturing it afterwards costs the
sweep again at 10 units a ticker.

**Capture is not use.** D-90 is open and this prefers no fork. What decides the timing
is the asymmetry: a capture taken during a sweep already happening is cheap and
reversible, and one taken after it is a re-sweep. Recording that here is what stops a
later reader taking this as X-PEAD having been quietly favoured.

**The store.** `earnings_history`, grain ticker by fiscal period, writer
`FundamentalsIngestor`. Columns: `ticker`, `period_end`, `report_date`,
`before_after_market`, `eps_actual`, `eps_estimate`, and the provider's own surprise.

`period_end` with `ticker` is a natural key, so no surrogate and no `NULLS NOT
DISTINCT` case arises.

**The read rule.** Every read is keyed on `report_date <= date`, the analogue of
`filing_date_effective` [INVARIANT 12]. A row whose `report_date` is null is stored and
is unreadable, exactly as an undated fundamental row is, and the null count is recorded
at 3.7 so coverage is a measured figure rather than an assumption: 3.1 saw three
populated entries on one ticker out of fifty spanning 2014.

That rule is not lookahead and the reason should be stated, because it looks like it
might be. The nightly run executes after the close, so a result released after the close
of the date being computed was public before the run began. A backfilled date inherits
the same property, `report_date` being when the result actually landed.

**`before_after_market` is load-bearing rather than descriptive.** A result released
after the close of day D is reacted to on D+1, and one released before the open of D is
reacted to on D. A drift screen that computes its reaction window without reading this
column uses the wrong session for roughly half of all announcements, and the error is
systematic rather than noisy.

**The surprise column stores a fraction, not a percent** [`METRICS.md`, one rule for
every ratio]. The provider sends a percent; it is divided on the way in and the column
is named for what it holds.

It is stored rather than derived because it may not be derivable: the provider's figure
may rest on an estimate other than the one it reports. Whether it agrees with
`(eps_actual - eps_estimate) / abs(eps_estimate)` is measurable once the store is
loaded, and if it always agrees the column is a third statement of a derivable fact and
can go. That is the question 3.7's own output answers.

**D-97 Sector is stored per filing, and C01 reads it there.** `ACTIVE`
Completes the sector move the phase 3 plan makes at 3.7.

C01's per-member sector call buys at 10 units what `fundamentals/{t}` already carries
for nothing, and 3.11 has C01 reading sector from `fundamental_snapshot`, where no such
column exists.

**The column goes on `fundamental_snapshot`**, written by C03 from `General::Sector` on
the call it already makes. C01 reads the most recent filing at or before the date being
built, and remains the sole writer of `security` and `security_daily`.

**Not on `security`, for three reasons and the first is enough.** It would make C03 a
second writer on the table C01 owns, requiring a split declaration [INVARIANT 10, D-77],
where the column route needs none. It would carry one sector per ticker, which is
today's sector applied to every historical date, the exact defect `security_daily` was
created to remove. And it would put a weekly stage's read behind a nightly stage's
write.

**The limitation is recorded rather than proxied.** Sector as of a filing is not sector
as of a date: a company that reclassifies between filings reads as its former sector
until the next one lands. That is closer to point-in-time than a single current value
and is not the same thing, and the percentile cells inherit it.

A ticker with no fundamental rows has no sector, which resolves to the existing
bucket-only fallback rather than to a new case.

**D-98 The institutional holdings block is captured by C03 and C05 keeps form4 alone.**
`ACTIVE`
`FlowIngestor` calls `fundamentals/{t}` with `filter=Holders::Institutions`. C03 calls
the same endpoint unfiltered since 3.7, so the filter is a projection of a document C03
already receives, bought a second time at 10 units a ticker: 2,500 a night at
`flow.max_tickers_per_run` 250, about 912,500 a year, and 28,410 for a universe pass.

**That the two blocks agree is measured rather than inferred**, and the distinction
matters because the first draft of this decision asserted it "by construction". 3.1
confirmed the block is present in the unfiltered payload and 1.9 measured twenty entries
in the filtered form; neither confirmed the unfiltered one carries all twenty rather
than a truncated head, which providers do. Measured 2026-08-12 over CCS.US and NVDA.US,
two calls each: twenty entries both ways on both tickers, agreeing row for row on name,
date, total shares, current shares and change
[`docs/evidence/phase-3/holders-filtered-vs-unfiltered-20260812.txt`].

**Freshness improves rather than degrades.** C05's rotation covers 250 of 2,841 in 11.4
days; C03's covers 500 of about 4,800 in 9.6. The block is a top-20 snapshot at one or
two report dates and those move quarterly [D-69], so both cadences are far finer than
the data changes.

`institutional_holding`'s writer becomes `FundamentalsIngestor` and C05 keeps
`insider_transaction` and `flow_fetch_attempt`. One writer rather than a split, so no
D-77 declaration arises. §3's C03 and C05 cells move with it.

**There is no deadline on this and the reasoning that suggested one does not transfer.**
D-96 put the earnings capture inside the sweep because that history has fifty back
periods: miss the sweep and they cost a re-sweep. Holders is a current snapshot with no
series behind it, which is this decision's own argument for keeping it out of 3.9, so
missing 3.7's sweep costs about ten days of C03's rotation filling it at no additional
units. What decides the timing is that 3.9 is next and its scope depends on this, and
that a scope written right is better than one amended afterwards. A decision resting on
a reason that does not survive inspection gets reversed for a reason that does not
either.

**The population widens from the universe to the candidate pool**, about 4,800 names
against 2,841. That is storage rather than correctness and is the same case as C09's
over-write, recorded rather than narrowed.

**What this couples, which is the cost side of the freshness gain.** Holders now ride
C03's rotation, so one rotation feeds two screens' inputs. That rotation froze silently
once and nothing in the row counts said so; `OldestAttemptInSelection` is the observable
D-91 added to catch it, and it now covers two tables rather than one. The rotation
health figure matters more after this than before.

**3.9 does not sweep holders under any reading.** The block has no series, so a universe
pass re-fetches a current snapshot 2,841 times for 28,410 units where the nightly
rotation covers the universe in days. 3.9's scope is form4 alone.

D-69 is not a blocker: its own text keeps this table usable as a static feature under
either outcome, so the ingest stands whichever way `inst_ownership_change` goes.

**D-99 A ticker-partitioned backfill resumes by attempt record, not by position.**
`ACTIVE`
Supersedes the resume-position reasoning recorded under open item 22.

**A recorded position only survives a throw inside the tracked region.** A command
timeout, a process kill and an out-of-memory record nothing, the log write being the last
thing a run does. Three failures in one day produced two full restarts.

**Each restart re-wrote rows that already existed, which is not free work.** In MVCC an
update is a new tuple, a dead one behind it and an index entry in every index that is not
a heap-only update: 27 million dead tuples, and an upsert that had crossed its 300-second
command timeout by the third pass over the same rows. The claim that re-work is safe
because every write is idempotent is true for correctness and false for cost.

**So a sweep asks what is left rather than remembering where it stopped.** The remaining
set is the pool minus the tickers carrying an attempt record for that range. A hard kill
and a clean halt resume identically because neither is consulted.

**The stamp differs between C02 and C03 and the reason is not arbitrary.** For C03 the
nightly call and the sweep call are the same call, `fundamentals/{t}` returning full
history either way, so a ticker attempted by the nightly rotation is as complete as one
attempted by the sweep and skipping it is correct. Its attempt stamps the range end,
which also keeps `last_attempted_date` honest as the rotation's freshness ordering. For
C02 the two calls differ in depth: the nightly reload takes a twenty-date window and the
sweep takes whole history, so the sweep's marker has to be one no nightly run can
produce, which is the range start.

That is recorded because the two look inconsistent side by side and the next reader will
try to harmonise them. They are the same rule applied to two endpoints that differ in
what a nightly call already achieves.

**One column serves two purposes on C03**, the rotation's freshness ordering and the
attempt marker, and that is why the asymmetry exists at all. **This is the thing to split
if it ever bites, and it is not split now.** A second column would let the sweep stamp
what it likes without moving the rotation, at the cost of a schema change and a second
write for a conflict that has not arisen. What would make it bite is a sweep whose range
end is a date the rotation should not treat as fresh.

**D-100 A transient fault is decided by its error code, and one rule decides it at
every layer.** `ACTIVE`
Closes open items 28 and 29, which are one question asked twice.

**The distinction is in `SocketErrorCode`, not in the exception type.** The database
layer retried `NpgsqlException and not PostgresException` or `TimeoutException`, which
named types. A connect-phase `SocketException` is neither, so the commonest transient
database fault was not retried at all, while a `HostNotFound` wrapped in an
`NpgsqlException` was retried three times for an answer that cannot change. One fault
reaches a caller in three shapes depending on where in the handshake it lands, and a
predicate reading the outermost type treats one fault as three.

**Retryable: `TimedOut`, `ConnectionReset`, `ConnectionRefused`, `HostUnreachable`,
`NetworkUnreachable`, `TryAgain`.** Everything else fails on the first attempt,
`HostNotFound` included. `HostNotFound` is a connection string that is wrong, not a
network that is briefly unwell, and retrying it spends the attempts and the backoff on
something that will never succeed while the run learns at the third failure what it knew
at the first. `TryAgain` is the resolver saying it could not answer this time rather than
that there is nothing to answer, which is the one DNS failure that is transient.

**The socket error decides wherever it sits in the exception chain**, because that is
what makes the rule independent of which layer wrapped it.

**The provider client takes the same rule with its own vocabulary.** `EodhdClient`
retried nothing, and one reset socket in roughly 50,000 requests ended a 128-minute sweep
on the free gate read. Its transient set is those socket codes plus `429`, `502`, `503`
and `504`. **`402` stays fatal**: an exhausted allowance persists for the provider's day,
so a retry spends the wall clock against a wall that will not move and the stage has to
fail rather than complete over a partial load. **`404` stays "not carried"**, being a
fact about the ticker rather than a fault, and the callers that tolerate it record zero
rows.

**One decision rather than two, and that is the point rather than tidiness.** Two
separately reasoned rules for one distinction drift, and each drifts toward whichever
failure its own layer saw last. The retryable codes are named in one place in code as
well, `TransientFault`, cited at both call sites.

**A retry at the client can cost a unit and that is accepted.** A request that reached
the provider and then lost its connection may already have been billed, so two units can
be spent for one series. Against that, the fault it exists for cost a sweep and about
13,500 tickers' work. The allowance gate's reserve absorbs the difference many times
over.

**What is not retried anywhere: a statement on an open connection.** That reasoning is
unchanged and is `RUNBOOK.md`'s. A failed write is a lost write for data already fetched
and paid for, and tolerating one reports a completed sweep over a partial load. Only the
establishment of a connection and the issuing of a request retry, both being asks that
left nothing behind when they failed.

---

---

## Open

**D-53 Whether the local digest model stays local once measured.** `OPEN`
The rotation in D-27 will produce a paired sample. If digest source proves not to
matter, the chain can be simplified. Do not act before a quarter of data exists.

**D-54 Whether both research portfolios are kept.** `OPEN`
Decide from the ~~A against D~~ [superseded, D-36] `Research: Opus 5` against
`Research: V4 Pro` comparison after at least a year, and from the validator
rejection rate per model, which is available much sooner. The letters were the
naming in use before D-36 set the four portfolio names, and they survived the
rename here [M.3].

~~**D-64 The freshness guard's abort floor against a part-settled file.** `OPEN`~~
[answered, and now stated in full above as `ACTIVE`]. The body is not repeated
here, because a decision stated twice is a decision that can disagree with
itself. The number is kept in place so that the register shows it was open and
where it went.

~~**D-65 What the freshness guard asserts about the latest price date.** `OPEN`~~
[answered, and now stated in full above as `ACTIVE`]. Same handling, same
reason.

**D-69 Whether the flow screen survives an unbackfillable institutional
ownership source.** `OPEN`
Owed to phase 4, not phase 1. Recorded on the corrected 1.9 measurements, not
on the retracted ones: the subject is `inst_ownership_change`, and insider
data is not the problem.

`Holders::Institutions` is a top-20 snapshot rather than a series. CCS.US and
NVDA.US each return 20 entries at a single `date`, 2026-03-31; BXC.US returns
20 across two, 2026-03-31 and 2026-06-30. `sec-filings/{t}/13f` is a 404 and
the filings index lists only `10k`, `10q`, `form4` and `8k`. `SCHEMA.md` says
`report_date` is what makes this table backfillable, and against this source
that is false: the column is populated, which is why the claim survived, but
one or two distinct values per ticker is not a history.

So S4's third input has no past. `insider_net_90d_usd` and
`distinct_buyer_count` are unaffected and fully backfillable, form4 paging on
`page[offset]` and `page[limit]` with `meta.total` matching the filings index
on every ticker checked, CCS.US 324, NVDA.US 590, PHAT.US 171.

D-58 already removed `short_interest_change` from S4 for the same reason in a
different form, that a screen whose backfill scores come from a different
population than its live scores has a floor drawn from a distribution the live
screen does not share. This is the second of three inputs to meet it.

Options, none chosen here. Run S4 on its two insider inputs and drop
`inst_ownership_change`, which keeps the screen backfillable and costs the one
input that is not insider-derived. Keep all three and accept that S4's floor is
drawn from a two-input distribution during backfill and a three-input one
live, which D-58 rejected. Or source ownership from SEC EDGAR 13F, free and
complete, at the cost of a second provider and a real ingest.

**The two insider inputs are backfillable and are not clean, measured at 3.1.**
`sec-filings/{t}` and `sec-filings/{t}/form4` return `404 Symbol not found` for every
delisted name tested, against the same ticker strings `eod/{t}` and `splits/{t}`
answered for in the same run. So the sentence above, that
`insider_net_90d_usd` and `distinct_buyer_count` are unaffected, holds only for names
that still exist. Whichever option is taken here is taken on evidence measured over
survivors, and the direction of that bias is toward keeping S4 rather than cutting it
[`PROGRESS.md` open item 12, which carries the reasoning and the
shadow-comparison consequence].

The timeboxed check A12 asked for was run and is in the same transcript. The
subscription exposes no dated or market-wide institutional feed. It does expose
a market-wide legacy `insider-transactions` endpoint, 1,000 rows in one call,
but it is stale by roughly three months and thin per ticker, and it is not
needed now that form4 pages.

**Recorded alongside whichever way it goes** [A15]. After this, both of S4's
surviving inputs come from one endpoint, so a single provider change takes the
whole screen rather than one input. The screen was designed with four inputs
from three sources; D-58 removed one and this removes or isolates another, and
the concentration that leaves is a property of the screen rather than of either
decision on its own.

`institutional_holding` still ingests and 1.7's institutional half still builds.
A top-20 current-holders snapshot is a usable static feature. It is only the
change metric that has no series behind it.

**D-90 Whether post-earnings drift is registered, and on what history.** `OPEN`
Owed to phase 4, and open rather than decided because it is a fork the design
cannot settle from what it knows. Its input has no backfillable history and
nothing else in the family shares the problem.

`events.earnings_backward_days` is 7 [`CONFIG_REFERENCE.md`, verified consumer
EventsIngestor], so the events store reaches seven days into the past and there
is no multi-year earnings history to backfill against. And `announced_date` is
null for earnings, because `calendar/earnings` sends none, so a backfilled row
and a live-accumulated one are indistinguishable and a rescheduled report
overwrites its old date without trace [`BUILD_PLAN.md` carried obligations, 1
to 5].

D-86's condition therefore fails for this screen alone: its backfilled
distribution would come from a different population than its live one. That is
what D-58 removed short interest for and what D-69 is open on for
`inst_ownership_change`, making this the third instance of one pattern and the
first found before anything was built on it.

Three options, none chosen. Register it as a shadow accumulating prospectively
only, in which case it has no floor for 250 sessions, reaches D-87's sample
floor roughly a year behind the other addition, and is never comparable to the
other three on a backfilled window. Widen the backward window and backfill
earnings history first, which is an ingest change, does not fix
`announced_date`, and runs into phase 5's open question about whether a
backfilled `events` row is point-in-time correct at all. Or do not register it,
and revisit when `events` can support it.

**What the choice costs the rest of the family, which is less than it looks.**
D-87's `k` counts screens eligible at the evaluation rather than registered, so
under the first option X-PEAD raises no bar until it reaches the sample floor
about a year in. Registering it costs the other addition nothing over that year,
and the bar rises to 1.18 standard errors at the first evaluation where there is
genuinely a selection of two.
