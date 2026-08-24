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

**D-101 Every backfill ingest pool that can reach delisted names does, and they all mean
the same thing.** `ACTIVE`
Closes open item 16.

**The pool is the live pool plus every admitted delisted common stock carrying at least
one bar inside the window**, which is 16,862 names measured 2026-08-13. C03 was amended
to it at 3.7; C04 and C06 take the same definition from here. C05 cannot and that is
open item 12's finding rather than an exception carved here: `sec-filings` returns 404
for delisted names against ticker strings the price and fundamentals endpoints answer
for in the same run.

**The sentiment half is not optional, because the absence is not a gap.** A delisted
name in a reconstructed 2021 universe with no sentiment rows does not arrive as unknown.
`article_count` zero-fills, so `article_count_z_own_90d` is computed against a baseline
of zeros while `sentiment_score` stays null. That is a degenerate value that ranks, not
an absence that abstains, and it ranks in the same direction for every name that later
failed. Null means unknown is the rule [`CLAUDE.md` §6]; a zero that arrives by
construction rather than by measurement defeats it silently.

**Both of the two anti-megacap screens were on course to have survivor-only backfilled
history, and that is what makes this urgent rather than tidy.** §20 names the sentiment
and flow screens as "the two that structurally tilt small and the two doing the most to
keep this system off megacaps". S4's bias is irreducible at open item 12. S3's is not,
the endpoint answering for delisted names, so leaving it would have meant the tuner
reading both anti-megacap screens off survivors alone at exactly the moment D-42's
reasoning says a tuner cutting those two dismantles the design.

**C06 widens whether or not its rows have a reader for delisted names.** Three ingest
pools with two definitions is the shape that has produced every silent hole this phase
has found, the fundamentals pool and the price pool being the other two. Consistency is
what is bought here, and a pool rule that has to be looked up per component is one a
later session gets wrong.

**The earnings half was checked for being free and it is not in the phase at all.** The
mechanism is as supposed: `calendar/earnings` is one global bulk call at weight 1 for
any range, and the universe narrowing happens in `ParseEarnings` after the response
arrives rather than in the request, so widening the set passed in would cost nothing.
It does not apply, because 3.10 deliberately loads no earnings: the payload carries no
date on which a schedule became public, so `announced_date` is null and loading it would
put a lookahead of unknown size under C12 and C15 [phase 3 plan, 3.10]. There is
therefore no free half to collect and none priced. **The blackout consequence is uniform
rather than asymmetric**: C12's earnings blackout never fires on any backfilled date for
any name, survivor or not, so this is not a survivorship defect and the widening does
not address it. Whether `earnings_history`, which C03 now populates over the widened
pool [D-96], can serve that gate is a separate question and phase 5's.

**The marginal cost is 118,034 units**, being 84,310 for sentiment at 5 a ticker and
33,724 for splits and dividends at 1 each. The phase total is repriced from the measured
D in the phase plan's §2 and comes to ~647,376, inside the 376,685 to 702,795 bracket
that section stated before D was known.

---

**D-102 The whole-table pass under both pool statements is not removable by an access
path, and every form that looked like it was has been measured and rejected.** `ACTIVE`
Narrows open item 32 and open item 25 rather than closing them. What is still owed is at
the foot of this entry.

C03's `BootstrapPoolAsync` and C01's `LiquidAsync` derive a candidate set from
`price_daily`, now 109,787,541 rows and 18 GB. Both open by asking for every distinct
ticker, which the planner answers with a parallel sequential scan of the whole heap:
1,146,298 buffers and 9.2 GB read to produce 88,341 values. **That pass stands.** Four
forms were tried against it and all four are rejected on measurement.

**What changed instead is `LiquidAsync`'s shape, which is a separate defect in the same
statement.** It windowed every row of the table, materialised 109,787,541 rows into a
5.4 GB on-disk CTE, sorted them externally at about 4.5 GB across three workers, and
scanned the table a second time for `count`, `min` and `max`: 2,292,676 shared pages and
2,369,378 temp pages written, roughly 19 GB of temporary I/O to return 8,610 rows. It
now takes each ticker's twenty most recent bars through a LATERAL bounded by `LIMIT`,
and asks the history question last of the few thousand names that already cleared price
and liquidity. **Proved set-identical at 8,610 tickers, 0 missing and 0 extra**, against
the shipped statement extracted from source rather than transcribed. C03's statement is
unchanged by this decision, having had that fix at item 24.

**Rejected, each with the measurement that rejected it.**

**A recursive loose index scan.** The form that looked best and the one this decision
was first written to adopt. Measured alone it is decisively cheaper: **436,340 buffers
against the plain form's 1,146,298**, `Index Searches: 88341` and `Heap Fetches: 0`,
which is exactly one descent per distinct ticker and no heap access at all. Measured
**inside `LiquidAsync` it took 18,557.6 seconds against the plain form's 1,063.9s, a
regression of about seventeen times**, returning the identical set both ways. **A
recursive CTE reports a fixed estimate of 100 rows whatever the data**, so every node
above it is costed for a hundred tickers when 88,341 arrive, and the planner picks joins
that are right for a hundred rows and catastrophic for eighty-eight thousand. **A form
measured in isolation and adopted on that measurement is the failure this entry exists
to record**: the isolated number was real, it was simply not the number that decides.

**The index path, forced.** Available and more expensive: 1,783,610 buffers against
1,146,298, and `Index Searches: 1`, one full pass over all 109,787,541 index entries.
**Postgres does not do a loose index scan for `DISTINCT`**, which is measured here rather
than assumed, so the planner was picking the cheaper of the two paths it had rather than
mis-costing a third.

**A larger `shared_buffers`.** The warming fix restated. It dies on a restart, the
sweep's own writes evict it, and a 610-second warming pass immediately before a run
failed to hold it. Measured incidentally at the 128MB default, which is a fact about
this server rather than an argument.

**Partitioning.** Both statements need the newest bars per ticker and a bounded walk of
each ticker's whole history. A date-leading key scatters every per-ticker seek across
every partition; a ticker-leading one needs tens of thousands of partitions. Any key
helps one half and hurts the other.

**Pinning `n_distinct`.** Tried first because it was the cheapest form available: a
per-column attribute option, persistent, no new table and nothing asked of C02.
`pg_stats` gave 20,800 against a true 88,341, an underestimate of 4.2x, **which is the
opposite direction from the one that would explain the plan**: the planner already
believed a per-value descent cheaper than it is and took the sequential scan anyway.
Pinned to the true value and re-measured: **the plan did not change**, the same parallel
sequential scan over the same 1,145,742 pages, cost moving 1,832,512 to 1,832,094. The
34x wall-clock improvement across that pair is the OS page cache and not the pin, which
the identical buffer counts settle. Rejected as an access-path fix and not carried.

**A per-ticker summary table.** Not tried, and it is what remains. It is the only option
that removes the whole-table pass rather than re-routing it, and its boundary is why it
is not taken here in one step: it serves as-of-now for the nightly path and a fixed range
for a backfill pool, and it does not serve a per-date historical universe. **C03's two
call sites are both inside that boundary. C01's is not**, `LiquidAsync` running per
weekly evaluation date at 3.11, so a summary of current state cannot answer for a 2021
date and applying it there would stamp today's universe on history, which is the
survivorship failure `security_daily` exists to prevent.

**The 3.1-second precedent is a different statement and nothing regressed with growth.**
Item 32 records `SELECT DISTINCT ticker FROM price_daily WHERE date >= '2021-01-04'` at
3.1s off 0007's `(date, ticker)` index. That is a bounded range on that index's leading
column. The pool asks `date <= asOf`, which matches every row and gives the same index
nothing to narrow, so the two were never the same plan.

**`count(*)` reads every row and `EXISTS ... OFFSET` stops, and that is worth recording
because a later session will reach for the first.** "Has at least 250 bars" written as
`count(*) >= 250` inside a LATERAL reads every bar a ticker has; written as
`EXISTS (... OFFSET 249 LIMIT 1)` it stops at the 250th index entry. The first
`LiquidAsync` rewrite used `count(*)` and measured 948.7s against C03's 474.7s, which is
the same shape and the same data differing only in that one clause. `min(date)` and
`max(date)` are the exception and stay as aggregates, Postgres answering each as a
one-row seek on the same index.

**The visibility map is a precondition and not a fix.** `relallvisible` stood at 818,328
of 1,132,553 pages, so C03's history test reported `Heap Fetches: 6,328,244` inside a
scan the plan calls index-only. `VACUUM (ANALYZE)` took it to 100 percent and that node
from 5,399,262 buffers to 510,352, a 90.6 percent cut, with `Heap Fetches: 0`. Autovacuum
fell behind during this sweep exactly as item 27 measured it falling behind during the
last, so the same 28 percent returns on the next one. When a vacuum runs is item 27's
question and stays open.

**What is still owed, so this entry is not read as closing more than it does.** Both
statements still make one whole-heap pass each time they run, C03's pool build measuring
between 46s warm and 531s cold across this phase. The per-ticker summary is the remaining
option for C03 and is not built. `LiquidAsync` is improved and proved and is still not
fast, and what 3.11 needs from it is recorded in `PROGRESS.md` as a finding about that
checkpoint's shape rather than folded in here.

---

**D-103 A failure-mode test triggers the failure through the system, not through the
environment.** `ACTIVE`
Closes open item 29, which D-100 closed as an instance and this closes as a class.

Two tests pinned a connection failure by arranging a condition the environment happened
to provide rather than one the system guarantees.

`ARefusedLoginIsNotRetried` set a wrong password and asserted the refusal. CI's Postgres
runs `POSTGRES_HOST_AUTH_METHOD: trust`, so no login is refusable there, the read
succeeded and the assertion failed. It passed on every developer machine, which asks for
a password, and was red on every CI run from the moment it was written: 23 consecutive
runs from 2026-08-13T02:07Z to 2026-08-16T05:27Z, checked one at a time rather than
sampled, one line at the end of 372 green ones.

The earlier instance dialled an unresolvable host with a one-second timeout and asserted
a timeout. The resolver returned `NXDOMAIN` in 206 milliseconds, so the timeout never
ran. It passed only while the network was saturated enough for the lookup to take longer
than a second.

**The two were red together and only one was seen.** The first two of those 23 runs fail
on both names at once, which is the register's evidence that this is one defect with two
instances rather than two tests that each went wrong. D-100 read the pair as one question
asked twice at the layer of the retry predicate, and fixed the predicate for both; the
trigger it repaired in one instance it left standing in the other.

**The rule.** A test that pins a failure mode triggers it through a condition the system
guarantees. An unroutable address always fails to connect. A database that cannot exist
always raises `invalid_catalog_name` during startup, whatever the authentication method.
Neither depends on how a machine is configured.

**The check.** Would this test behave identically on a machine configured differently
from the one it was written on? If the answer needs a fact about DNS, about an auth
method, about network speed or about what else is running, the trigger is wrong even when
the assertion is right.

**This matters immediately rather than in principle.** D-100 names six socket error codes
as transient, being `TimedOut`, `ConnectionReset`, `ConnectionRefused`, `HostUnreachable`,
`NetworkUnreachable` and `TryAgain`, so six more tests of this shape are due, and each has
an environment-dependent trigger available alongside a guaranteed one.

**Name what the test pins.** Both of these were named for the environmental trigger rather
than the property. A test called after its trigger drifts when the trigger changes; one
called after the property does not. **The two D-100 tests were renamed on this clause
although their triggers were sound**, because D-100 classifies six socket error codes as
transient, so six more tests of this shape are due, and naming them after the
classification makes them a set that references the rule rather than six names referencing
six mechanisms. The trigger moves into a doc comment on each, with the line that says why
it is guaranteed rather than convenient: the name says what is pinned and the body says
how it is reached.

**The `ci.ps1` gap goes alongside and is not closed here.** `ci.ps1` mirrors `ci.yml`'s
steps and asserts that it does. It does not mirror the environment those steps run in, so
its green is evidence about this machine's database. **Do not replicate CI's environment
locally.** Trust authentication is precisely the property this test needed absent, and
copying it would destroy on the developer machine what it just fixed in CI. What is
recorded instead is that `ci.ps1` reads `ci.yml`'s `services` and `env` blocks and reports
what differs, so its green carries its own scope, which is the same discipline as stating
an expected count before a sweep. That is open item 37 and it is not built while the
backfill is running.

---

**D-104 The benchmark is a reference series: fetched into `price_daily`, admitted to
nothing.** `ACTIVE`
Two compute components measure against a benchmark and for most of the backfill window
neither could, because nothing had ever fetched one. C02's sweep pool is every admitted
common stock, live and delisted [D-4, INVARIANT 1], and the benchmark is an ETF, which D-2
puts out of scope. So the sweep never asked for it, and nothing errored.

**What a reference series is.** A price series a component reads as a comparison, held in
`price_daily` beside every other series and admitted to nothing else. It is never written
to `security` or to `security_daily`, so it is a member of no universe on any date, is
never a candidate, is never ranked inside a cell, and can never become a position. **D-2 is
untouched by this.** D-2 says which instruments can be selected, and a reference series is
not selectable. The exception is to C02's fetch list and not to the universe definition,
which is where absolute filters live and stay [INVARIANT 1]. Nothing downstream narrows,
because nothing downstream gains a member.

**Which components read it.** `IndicatorEngine` for `rs_change_21d`, `rs_change_63d`,
`rs_21d_63d_change` and `rs_20d_slope`, which are the ticker's return over the benchmark's
[`METRICS.md` §2]. `MarketContextEngine` for the regime label's sign test, which is whether
the benchmark closed above its own `market.breadth_ma_days` average [D-80]. **Breadth is
not one of them**: it is counted off `indicator_daily.dist_200dma` over that date's
universe and reads no benchmark at all. The sector composites read none either, being built
from the universe's own members, which is the reason the peer benchmark needs no ETF
[D-42].

**The measurement that forced it,** taken 2026-08-18 with C08 and C10 having run the whole
window. **`SPY.US` held 265 bars in `price_daily`, first 2025-07-22, and zero rows in
`price_fetch_attempt`.** The 265 are what the unfiltered nightly bulk feed had accumulated
since July 2025. The empty attempt table is the sweep saying it never asked, which is the
distinction an absent row is there to make [0010].

**What it cost, which is why this is a decision and not a defect note.** **2,690,981 of
4,143,273 `indicator_daily` rows carried null `rs_change_21d`, `rs_change_63d` and
`rs_20d_slope`**, being every row before the benchmark's history began. **1,497 of 1,584
`market_context_daily` rows were labelled `mixed` and none was `risk_off`**, against 192
dates whose breadth was at or below the 0.40 floor and 925 at or above the 0.60 ceiling,
with all 87 `risk_on` in 2026. Both components were behaving exactly as written:
`BenchmarkAboveItsAverageAsync` returns null below `market.breadth_ma_days` bars rather
than averaging over whatever is stored, and `Regime` returns `mixed` on a null. Breadth
itself was right throughout, 2022 averaging 0.376. Nothing errored, both runs completed,
and both reported plausible counts. That is `CLAUDE.md` §1 arriving as a measurement rather
than as a warning.

**What the reasoning had been, so this is a replacement rather than an addition.**
`METRICS.md` §2 and `IndicatorEngine.Benchmark` both record that C02 writes every row the
bulk feed returns with no universe filter, so the series is present. That is true of the
nightly feed's retained window and was read as covering history. It does not, because the
backfill loads history through `eod/{t}` per ticker and that path takes a pool. **A series
a component depends on is loaded by something that names it, not by something that happens
to sweep past it.**

**How a second one is added.** `ReferenceSeries.All` is the set, stated once, and C02's
pool is the admitted common stock union that set. An index, a sector proxy or a second
benchmark is one string in that list and no other change: the pool picks it up, the sweep
fetches its whole history at one unit, and the component that wants it names it. A
component reading a series absent from that list is reading something no sweep guarantees,
which is the state this decision was written out of.

**Stated in code rather than as a config key, and that is the point rather than an
omission.** Config resolves as of the simulated date [INVARIANT 13], so a benchmark held as
a key would resolve per date, and a change to it would leave one relative-strength column
computed against two different series with nothing in the row saying which. Changing a
benchmark splits history into halves that cannot be pooled [`CLAUDE.md` §12], and a change
that splits history is a code change carrying a decision, not a config row.

**The load is a re-run of C02 over the same range and costs about three units.** The sweep
resumes by attempt record [D-99, 0010], so every ticker already carrying an attempt at the
range start is not dispatched and the remaining set is the reference series alone. Two
symbol-list calls build the pool and one `eod/{t}` call fetches the series. **Then C08 and
C10 are re-run**, because their rows were written against a benchmark that was not there.

**D-105 A compute range refuses a range end past the ingest frontier.** `ACTIVE`
Closes open item 60.

**The frontier is the newest date in `price_daily` carrying a real bar count, not the
newest date present.** That distinction is the finding rather than a detail of it.
Measured 2026-08-21: the store held 3,024 bars on 2026-08-12 and exactly one on each of
2026-08-13, 2026-08-14 and 2026-08-17, all three `SPY.US`, D-104's benchmark load having
run forward past where the equity sweep stopped. A driver reading the newest date present
gets 2026-08-17; a driver reading a real bar count gets 2026-08-12. **The backfill read
neither, because it never read the frontier at all and took its range end as given.**

**It refuses rather than clamps.** A range end the store cannot cover is an operator
error, and silently narrowing a five-year range is the class of failure this system exists
to catch [`CLAUDE.md` §1]. Clamping would put the same silence one step further along: the
run would complete, the numbers would look plausible, and the window analysed would not be
the window asked for. The three exit codes already distinguish a refusal from a halt, so
nothing had to be invented to tell an operator error from the allowance gate working.

**C07 performs this check nightly and the backfill had no equivalent.** That is the same
shape as the pool precondition and the seeding race: a guard present on one path and
absent on the other. So the rule is C07's settledness read through C07's own keys,
`freshness.settled_window_days` and `freshness.settled_fraction`, walking newest-first and
taking the first date at or above that fraction of the trailing median. A key of its own
would have been the same defect one level down, the two paths free to drift on what a real
bar count is. It does not take C07's two absolute floors:
`freshness.row_count_abort_below` is 40,000, sized for the bulk feed's whole-exchange row
count against about 3,000 bars a session here, and applying it would refuse every range
ever issued. Recency is not taken either, being a provider call and a question about
currency rather than about what the store holds.

**What it produced before it existed, recorded so the cost is a figure rather than a
worry.** The compute range end was 2026-08-13, a date carrying one bar. `indicator_daily`
took 2,864 rows on it and `sentiment_derived_daily` 2,864, with one `market_context_daily`
row: **5,729 rows that were a byte-repeat of the day before.** Against 2026-08-12 they
were 2,864 of 2,864 identical on `dist_200dma`, `adx14` and `atr_pct`, and breadth read
0.70810056 on both against 0.7126397 on 2026-08-11. **A trailing-window stage cannot fail
on a missing bar**, which is why nothing said so: the window ending on the unreached date
holds the same bars as the window ending on the frontier, so the stage computes a correct
answer to the wrong question. Those rows are indistinguishable from real ones except by
the thinness of their date, and they stand: 2026-08-13 carries one bar rather than none,
so item 57's no-bar predicate cannot reach them.

**Where the check sits, because the placement is load-bearing and could have gone wrong
silently.** It binds the trading calendar, in the session source the driver composes, and
not the top of the range run. A range end past the frontier is an operator error for a
compute stage and the ordinary case for an ingest one, since fetching the dates the store
does not hold yet is how the frontier moves at all. A check on the run would have made the
backfill unable to extend the store, and every test of the refusal would still have
passed. The six calendar consumers are exactly the six the wrong end harms.


**D-106 A sweep marker records the coverage a sweep reached, not the invocation that
reached it.** `ACTIVE`
Closes open item 44, which item 62 was merged into.

**One column carried both the sweep marker and the rotation's freshness ordering**, on
`fundamental_fetch_attempt` and `flow_fetch_attempt`, the only two tables a nightly path
and a range path both write. D-99 named the split and deferred it in its own words:
"This is the thing to split if it ever bites, and it is not split now." D-105 then
refused the only range end C03 and C05 were stamped for, so the next sequence backfill
would have re-dispatched both pools whole.

**The split gives the sweep its own column.** `swept_through_date` is the sweep's, and
the rotation keeps `last_attempted_date` as the freshness ordering it was always for.
Each reader now answers its own question and neither can undo the other's work: a range
stamp cannot make a ticker look freshly attempted to the night, and a nightly attempt
cannot satisfy a sweep.

**The marker is read as "swept through at least this date", never as equality, and that
is the rule this decision exists to state.** A marker compared by equality records which
invocation happened. A marker compared by coverage records what the store holds, which is
the only one of the two a later run has any use for. The difference is not academic:
equality re-dispatches a completed pool the moment a range end moves by a day, and a
range end moves whenever a frontier correction finds one, which is item 44's third
trigger and the general case D-99 did not foresee. **Nothing would fail while it did so.**
The pool would be re-fetched, every row would be rewritten with the same values, every
stage would report `ok`, and the only visible trace would be the bill. That is why the
reading rule is the decision rather than a detail of the migration.

**A column split alone does not give this.** Splitting the column and keeping the
equality test leaves a marker of 2026-08-13 against a corrected end of 2026-08-12,
matching nothing and re-dispatching exactly as before. Both halves are required and the
second is the one that generalises.

**The evidence is the fall-through rather than an illustration of it.** The first
sequence run after the split, over 2021-01-04..2026-08-12:

```
FundamentalsIngestor   covered   0 of 4,156
FlowIngestor           covered   0 of 2,864
EventsIngestor         covered   0 of 4,117
SentimentIngestor      covered   0 of 4,117
```

Roughly **440,000 provider units and 4.9 provider days became 36 units and 20 seconds**.

**The 36 is not a rounding of zero and it says something about what `covered` means.**
C02 reported `ok`, dispatching 35 of 50,615 admitted names plus the reference series
[D-104], those being names the symbol list has admitted since the sweep ran and which
therefore carry no marker at all. The pool rebuilds itself live from the provider on
every run, so `covered` is a statement about a pool that moves rather than about a fixed
list. A marker read as coverage is what lets a moving pool report `covered` for the part
of itself that has not moved.

**The stamp is unchanged and stays the range end on both tables.** D-99's asymmetry is
deliberate and per stage: C03's and C05's nightly and sweep calls are the same call,
where C02's, C04's and C06's differ in depth, which is why those three key on the range
start. What was wrong was the shared column and the reading, not the choice of stamp, so
that decision is not reopened.

Migration `0013_attempt_sweep_marker.sql`, `FundamentalsIngestor` C03 and `FlowIngestor`
C05. Both halves are asserted: a range run stamps the marker and leaves the ordering
untouched, and a nightly attempt moves the ordering and leaves the marker untouched.
One alone is satisfied by a component that writes neither.

**D-130 A session is a date the exchange traded, read from the exchange calendar rather
than inferred from the store.** `ACTIVE`, closing item 42 and the half of item 41 the two
share. Authored 2026-08-24 on the operator's direction, at phase 4's sign-off.

`TradingCalendar.SessionsAsync` takes a range's dates from `price_daily`, on the reasoning
that a calendar walk would write a row of nulls on a day the exchange did not trade,
indistinguishable from a name with no history. **That reasoning holds for the walk and not
for the source.** Taking the dates from the store means a handful of stray bars makes a
closed day a session.

**The surplus is measured and it is entirely non-sessions.** 1,584 distinct dates over
2021-01-04..2026-08-12 against roughly 1,409 real US sessions [item 42, 2026-08-18]. The
distribution is bimodal with three orders of magnitude between the modes: a median of
18,315 tickers a date against **162 dates below 1,000**, p1 at 3 and p5 at 12, and almost
nothing between 500 and 1,000. 1,584 less 162 is 1,422 against the estimate's 1,409. The
thin dates read off as 122 weekends and the US market holiday calendar with its
observances.

**Item 57's closure does not reach this and its note is amended to say so.** That delete
removed compute rows on dates `price_daily` holds no bar for at all, and its note argued
the fault could not recur from the range path because a date with zero bars is never in the
session list. True as written. **The 54 holidays inside phase 4's frozen range carry 1 to
18 bars each**, so they were never in scope for it.

**A bar-count threshold is rejected.** No number in this corpus constrains where it would
sit; it would need revisiting whenever the provider's coverage changes, and the 2025-07-05
onward weekend rows are that change already visible; and it infers a fact `ExchangeCalendar`
already holds as data. Reading the answer beats estimating it.

**Item 41's rejected option is not this one, and the distinction is the point.** Option 3
there was to take the calendar from a single benchmark ticker's series, which would have
excluded exactly these dates and risked excluding real ones on any gap in that one name.
The exchange calendar excludes non-sessions without depending on any ticker's coverage.

### What this costs in the record, measured rather than assumed

**The 57 frozen `attribution` rows stay.** They stand on 54 dates the exchange was shut, 8
of them candidates and 49 shadows, and `attribution` rows are written at shortlist time and
never reconstructed [INVARIANT 4, D-40, `RUNBOOK.md`]. **What phase 8 owes is an exclusion
rather than a correction**: C21 skips a non-session date, and the 8 candidate rows are named
by date in the carried obligation so they are excluded by identity rather than by a rule
that has to be got right twice.

**The half-populated dates inside every screen's 250-date floor window do not matter.**
Recomputed over the 250 open dates ending on the same date, sampled every twentieth open
date across all five live screens, 2026-08-24:

- **Floors move by under 0.1 percent of themselves.** Mean absolute move 0.012 to 0.075
  percent of the floor, worst single sampled date 0.255 percent.
- **And it does not reach the ranked set.** Under one name a date on every screen, at most
  four on the worst sampled date, against ranked sets of 21 to 52. S5 changes on none of
  its 58.

**Two persistence figures move and both are recorded here so this is not re-opened on
intuition.** Twenty-two of the twenty-four move by 0.013 or less, which is the same reading
at three digits. **S1's D-1 rises from 0.8784 to 0.9344 and its D-5 from 0.7683 to 0.8093**
when the 54 leave both sides of the lag, because S1 ranks about 22 names on a closed day
against about 46 either side, so every closed date sat in two low-overlap pairs. **The
recorded figures understate rather than inflate**, which is the direction that gets believed
without checking, and it is why they are named. S5's D-21 falls from 0.9091 to 0.7778 on 42
pairs of a set of size one and means very little. No reading in the phase 4 plan's §5 table
changes.

**On an open-dates-only calendar the record would have started 2022-01-05 rather than
2021-12-24**, which is what D-115's "around 2022-01-03" was reaching for. 1,403 of the 1,457
dates are real sessions.

### The code change is not phase 4's

`TradingCalendar.SessionsAsync` is read by every compute range execution, so this reaches
C08, C09, C10, C11 and C35 as well as the selection layer. **It lands in its own pass
against whichever phase next touches the calendar**, with the measurements above as its
evidence, and it is filed as a carried obligation in `BUILD_PLAN.md` naming the component.
A build session does not widen its own scope to take it [`CLAUDE.md` §3].

**It is rebuild-forcing and therefore bundled.** A changed calendar changes what every
compute stage evaluates, and `SCREEN_LIFECYCLE.md` §6.7 and `CLAUDE.md` §12 say changes that
invalidate comparisons across a boundary are bundled into one boundary rather than taken one
at a time. Cheap to take now, expensive to take repeatedly.

---

## Inspection

Authored 2026-08-23 at phase 3's sign-off, from the plan drafted in
`prompts/BuildPlans/phase-3.5-record-inspector.md` §4. Authored before any phase 3.5
code, because each blocks a checkpoint that cannot be built without it [`CLAUDE.md`
§13]. Entered verbatim from that plan on the operator's explicit authorisation, the
operator remaining the decider of record.

Two of the three add a store, and neither rests on a measurement: both record a figure
the system already computes and discards, so what they change is what survives rather
than what is calculated [`CLAUDE.md` §11].

**D-107 The population a percentile was ranked against is stored per cell, not
recomputed and not discarded.** `ACTIVE`
C11 computes each metric's non-null population in its `(size_bucket, sector)` cell and
in its bucket inside one window function, uses both to pick a scope, writes the
percentile and keeps neither count. What survives is one run log line per date
aggregating every cell into a total.

A percentile without the size of the population behind it is unreadable. One over three
members and one over eighty are the same number on the page, and the fifteen-member
fallback is theoretical rather than visible. This is D-92's argument arriving one table
over and in its own words: a percentile computed over a cell nothing downstream can see
is not inspectable afterwards.

Recomputing it in a reader was weighed and is rejected. The count is not `count(*)` over
the cell's members; it is the non-null count of that metric in that cell, under C11's
null-sector rule, its `LEFT JOIN` and its `is_active` handling. A reader reproducing
those four rules is a second implementation of the cell rule whose failure is a
plausible number.

`percentile_cell_daily` is date by size bucket by sector by metric, written by
PercentileEngine. The grain is the cell, not the row: the population is a property of
the cell and one row per member per metric would restate it thousands of times over on
the two largest tables in the store.

Each row carries `cell_members`, `bucket_members`, `min_members` as the floor in force
on that date, and `ranked_scope` over `cell`, `bucket` and `none`. `bucket_members`
repeats across the sectors of a bucket, which is a redundancy accepted so that a reader
takes one row per metric rather than two. `ranked_scope` is stored rather than derived
so no reader compares a count against a floor and labels the result.

A name whose sector is null forms no cell and goes straight to the bucket fallback
[`METRICS.md` §6.4], so it needs a row with no sector.

Metric names are distinct across the four source tables today, which is what makes
`metric` sufficient in the key without the table beside it. `source_table` is carried as
a column so a reader knows where to look, and a future collision is a failed key rather
than a silent overwrite.

The pass that populates history records the range it has covered, so a reader
distinguishes a date nothing has reached yet from a cell that does not exist. That is
D-106's rule applied to a date-partitioned pass rather than a ticker-partitioned one:
the marker records coverage, not the invocation. Running date-descending makes the
covered set a contiguous suffix, so one covered-from date carries it.

**D-108 C01 records the criterion that rejected a name, per ticker per evaluation
date.** `ACTIVE`
"Why is this obvious company not in my universe" is the question the membership panel
exists for, and it is the half that currently takes a query. The admitted case is the
one whose answer is already known.

C01 counts six rejections into a run log line and writes no per-ticker row, so the
answer for one name is not recoverable from the record at all. Three further criteria,
minimum price, minimum median dollar volume and minimum history, are applied together
inside one SQL filter in `LiquidAsync`, so a name failing any of them is absent from the
counted loop and absent from the counts.

Reconstructing it later does not work and the reason is D-92's. The clean gap count is
computed as of the date and never stored [M.1], market capitalisation is computed from a
share count readable on that date and is stored only for members, and instrument type
comes from a provider symbol list that is stored nowhere. Three of the nine criteria
have no persisted input at all.

`universe_rejection` is ticker by evaluation date, written by UniverseBuilder, one
column: `criterion`, `NOT NULL` with a `CHECK` over the enumerated values. The
constraint is in the database for the same reason `regime_label`'s is: this column
segments every count taken off it, and a drifted value would land in its own bucket in
every segmentation without ever erroring.

One criterion per row, being the one that rejected, not every criterion the name would
have failed. D-4 is a conjunction and a name fails on the first criterion it fails;
recording all nine would suggest an independent evaluation the code does not perform.
What the column therefore means is the criterion the evaluation stopped on, which is a
property of C01's order rather than of D-4, and that is stated in `SCHEMA.md` so a later
reader does not read it as the only criterion the name failed.

The three pre-pass criteria are projected rather than filtered, so the statement
classifies instead of dropping. That widens what `LiquidAsync` returns, and
`MembershipAsync` consumes its result as already filtered: the loop applies six further
criteria and admits whatever survives them, never re-testing price, dollar volume or
history. Projected without a corresponding first test, the loop would therefore admit
names D-4 excludes, which is a change to C01's membership rather than an addition beside
it. So the pre-pass verdict travels with each row and the loop rejects on it before
every other criterion, and the admitted set is unchanged by construction rather than by
inspection.

The containment is exact and is stated rather than assumed. `LiquidAsync` is private
with one caller, `MembershipAsync`, which has two of its own, the nightly path and the
range path, and both take the same `Day`. Nothing else in the codebase reads the
statement. `universe.min_price`, `universe.min_adv_20d` and `universe.min_history_days`
each carry FundamentalsIngestor as a second consumer, and that is C03's own pool
statement rather than a call into this one, which is what D-102 means by the two pool
statements: C03 is untouched here.

The population is bounded by `backfill.window_start`, the key C01 already resolves and
already bounds `ListingAsync` by, for the reason stated there: a name that stopped
trading before the window can never be a member on any evaluated date. One population
rule rather than two, so the store and the listing cannot drift apart. What that
inherits is stated: if the window start moves, the store's population moves with it,
which is correct, a date outside the window not being one C01 evaluates. The unbounded
alternative is every ticker with a bar at or before the date, roughly 50,785 names
across 260 weekly evaluation dates and about 13 million rows to record that a ticker has
not traded since 2019. A tighter alternative is a new trailing-recency key, which is
smaller again and introduces a second population rule and a key whose only consumer is
this write; it is rejected on that rather than on size.

**D-109 A component that reads stores and writes nothing declares its read set as data,
and the catalogue conformance holds it in both directions.** `ACTIVE`
Write ownership has been enforced through the registry since 0.4 and read declarations
since D-74, and both mechanisms see only components the pipeline registry holds. Every
reader in this system has so far also been a writer, so nothing has needed the
distinction. RecordInspector is the first reader that is not, and the read-only query
surface is where more of them will appear.

`IWriteOwner` exists because a writer the registry cannot see is a writer INVARIANT 10
is not enforced against. The same sentence holds with reader substituted, and this
decision states the reader half rather than leaving the first one outside the net.

So a read owner declares `Name` and `ReadSet`, is named in `ARCHITECTURE.html` §3 like
every other component, and reaches data through `IStageData` behind a `DeclaredAccess`
built from that read set and an empty write set. The empty write set is the read-only
guarantee made structural: any write at all throws as undeclared before a connection
opens, which is the same guard rather than a second one.

It is hosted in the Api project and the Api still never references Pipeline
[`CLAUDE.md` §4], so no page can invoke a stage. The test project already references the
Api, so the conformance test sees the declaration without any new project reference and
without the Api gaining one.

---

## Screens and selection

Authored 2026-08-23 at phase 3.5's sign-off, from the plan drafted in
`prompts/BuildPlans/phase-4-screens-and-selection.md` §4. Authored before any phase 4
code, because each blocks a checkpoint that cannot be built without it [`CLAUDE.md`
§13]. Entered verbatim from that plan on the operator's explicit authorisation, the
operator remaining the decider of record.

**The `DoD:` paragraph closing each drafted clause is not carried here**, following
D-107 and D-109, which were transcribed the same way at phase 3.5's sign-off. The
register states what was decided and why, which stays true for as long as the decision
stands; a definition of done states how a checkpoint proves it was built, which is spent
once that checkpoint passes. The DoD text is in the plan, which is where the checkpoint
reads it from.

**D-110 `attribution` gains `surfaced_as`, and `score_per_screen` carries the rank
beside the score.** `ACTIVE`
D-85 authored both and neither exists. `surfaced_as` has no column in any of the
sixteen migrations and `score_per_screen` is flat `jsonb` with no writer. This entry is
the migration detail rather than a new ruling, and it exists because the shape has to be
settled in the same checkpoint as the column.

`surfaced_as` is text NOT NULL with `CHECK (surfaced_as IN ('candidate','shadow'))` and
no DEFAULT. No default for `security_daily.is_active`'s reason: a column that defaults
is a column a writer can decline to think about, and the value that gets written is then
the one nobody chose. A writer that has not decided must fail at the column.

The constraint rather than a writer-side check, for D-80's reason exactly. This column
segments every analysis in `SCREEN_LIFECYCLE.md` §4.5, so a drifted value lands in its
own bucket in every one of them without ever erroring.

`score_per_screen` becomes screen id to an object of score and rank, held by a `jsonb`
CHECK asserting every top-level value is an object. The flat shape then cannot be
written at all rather than being refused by whichever writer remembers. It is a shape
change to a column with no rows in it, so it costs nothing now and cannot be done
cheaply later.

The `candidate_attribution` view selects `surfaced_as = 'candidate'` and every reader
meaning candidate reads the view. Reading the table becomes a deliberate act rather than
the default, which inverts which mistake is easy.

**D-111 `screen_score_daily` is range-partitioned by date with its key reordered, and
the ranked rows carry a partial index.** `ACTIVE`
The primary key is `(ticker, screen_id, date)` and the table carries no other index
[`0001_snapshot.sql`]. Every read this system makes of it is one date: the allocator's
nightly read, and the backfill's read once per date across roughly 1,260 of them. The
leading column of the only index is the one the query does not constrain
[`SCREEN_LIFECYCLE.md` §9.3].

Three changes, and all three rather than one of them.

The key becomes `(date, screen_id, ticker)`. That fixes every per-date read rather than
only the allocator's, and it is the change §9.3's complaint literally describes.

Declarative range partitioning by date, one partition a year, with no default partition.
A date outside the declared range then fails loudly rather than landing in a partition
nothing queries. Partitioning makes the per-date read a single partition scan and makes
pass one's writes cheaper because each date lands in its own child.

A partial index on `(date, screen_id, rank_within_screen)` WHERE `rank_within_screen` IS
NOT NULL. The floor admits about two percent of the population, so the index covers
about two percent of the table and costs about two percent of what §9.3 priced a full
one at.

§9.3's option 1 as literally written is rejected. A full index on the largest table in
the system, maintained across roughly 19 million inserts during pass one, to serve a
query wanting two percent of the rows, while leaving the key's leading column still
wrong.

The cost is named rather than glossed. Partitioning adds one child relation a year, and
`SchemaParityTests` and `guards.ps1` both read `information_schema.columns` with no
partition filter, so every child's `score` column would be reported as an undeclared
real column. Two predicates in two places, and they are part of this checkpoint rather
than a surprise inside it.

**D-112 A screen score is the weighted mean of its direction-adjusted percentiles, taken
over the non-null members of its metric list, and null below a minimum input count.**
`ACTIVE`
`METRICS.md` §8 assigns this here and nothing else states it. What the formula has to
survive is the null rule: a great deal of this store is legitimately absent, and a screen
that scores absence as the worst value deletes the thinly covered small caps its small
slots exist to find.

On the 0 to 100 scale the percentiles already use, so a 98th-percentile floor reads
against the same units the inputs carry and no second scale exists to disagree with the
first.

Mean and not sum. A sum makes the score a function of how many inputs happened to be
non-null, so a name with three of seven is scored below one with seven by construction
rather than on its merits. That is `CLAUDE.md` §6's null rule broken inside the
arithmetic instead of at a call site, where nothing greps for it.

`screens.<id>.min_inputs` is what stops a name with one of seven being scored as
confidently as one with seven. Below it the score is null, which is the established idiom
rather than a new one: `ARCHITECTURE.html` §18 already says of a name with no sentiment
that the sentiment screen simply cannot rank it.

Weights are carried per metric and default to 1. `METRICS.md` §8 names weights as this
phase's to author, so the shape has to carry them even where every seeded value is one,
because adding the field later is a config migration across every screen row.

Rejected: a sum of percentiles, which punishes absence. A geometric mean or a product,
where one zero annihilates and there is no reason to want an AND across seven quality
measures. Z-scores, which need a second normalisation the store does not hold and
reintroduce the cross-sectional scale D-10 removed.

One consequence, stated because a reader will otherwise read it as a defect. A mean
compresses toward 50 and compresses further as the metric count grows, so S1 at seven
inputs and S3 at three will show visibly different dispersions and therefore floors at
visibly different levels. That is correct, each floor being drawn from its own screen's
distribution, and it is stated so that a floor of 71 on one screen beside 84 on another
is not read as a fault.

**D-113 The direction a screen reads a metric in lives in `screens.<id>.metrics`, and no
`_inv` column exists.** `ACTIVE`
Percentiles are ascending always: higher raw value means higher percentile, for every
metric [`METRICS.md` §6.3]. S1 ranks on `net_debt_ebitda_inv`, `accruals_inv` and
`share_count_change_inv`, and those names in `ARCHITECTURE.html` §05 name a direction
rather than a column. No `_inv` column exists or is meant to [`BUILD_PLAN.md` carried
obligations, 2 to 4].

The metric list is an array of objects, each carrying the metric, the direction and the
weight:

```
{"metric": "net_debt_ebitda", "direction": "low", "weight": 1}
```

direction "low" is applied as 100 minus the stored percentile. The words are "high" and
"low" rather than asc and desc or plus and minus one, because the stored percentile is
always ascending, so the word has to say what the screen rewards rather than how anything
sorts.

Rejected: a stored `_inv` column, which is a second copy of one fact and doubles the
percentile write. That is the shape D-76, D-77 and D-83 each removed from this corpus.

Rejected: a parallel `screens.<id>.directions` key, being two lists that can fall out of
length with each other and produce a plausible score when they do.

**D-114 `base_breakout_flag` enters S2 as a fixed bonus in score points outside the mean,
and a null flag contributes what false contributes.** `ACTIVE`
It is the one column with a named reader that is not percentiled, because a percentile
over a two-valued column collapses to two values and carries nothing the boolean does not
[`METRICS.md` §6.5]. It is an S2 ranking input [`ARCHITECTURE.html` §05], so it has to
enter somehow.

```
{"metric": "base_breakout_flag", "kind": "bonus", "points": 10}
```

applied after the weighted mean.

Mapping true to 100 and false to 0 inside the mean is rejected, and the arithmetic is the
reason. At six S2 inputs one boolean is worth a sixth of the score and opens a fifty-point
gap between two names differing on one binary condition. That is the magnitude falling out
of the mean's arithmetic rather than being chosen, which is exactly what §6.5 says a
percentile over a two-valued column cannot represent. A bonus is the only form where the
magnitude is stated in configuration.

Rejected: the flag as a hard gate on S2, which narrows the screen to a handful of names
most nights and leaves its floor drawn over a population it no longer ranks.

Null contributes zero, the same as false, and that is written down rather than defaulted.
"There is no base" and "the base is unknown" both mean no evidence of a defined entry
level, and the bonus is evidence-positive only. This is the one place in this system where
unknown and false legitimately coincide, which is why it is stated rather than left to the
null rule.

**D-115 The floor is the 98th percentile of the non-null score population, and a screen
without a full lookback has no floor and ranks nothing.** `ACTIVE`
D-112 makes a score null for a name below its screen's minimum input count, so the score
column is legitimately sparse. Postgres counts nulls toward `PERCENT_RANK`'s denominator,
so a floor computed over 700,000 slots of which 200,000 are null is a different number
from one computed over 500,000 real scores, and the two are indistinguishable on the
page. This is PercentileEngine's own `count(metric)` argument applied one level up.

Below `screens.floor_lookback_days` of observations there is no floor. `floor_score` is
null, `observation_days` records the short window, and nothing is ranked. That is
`SCREEN_LIFECYCLE.md` §5.1's rule for a newly registered shadow applied identically to a
live screen at the start of the backfill window, because it is the same condition rather
than an analogous one.

The consequence is that the first 250 sessions of the window carry scores and no
candidates, so candidate history begins around 2022-01-03 rather than at the window
start. That is the floor working and it is stated here so the gap is not read as a failed
pass.

**D-116 D-89's proportion is the only quota arithmetic, and `screens.quota_large`,
`quota_mid` and `quota_small` are retired.** `ACTIVE`

```
large = floor(slots / 4)
rest  = slots - large
mid   = floor(rest / 2)
small = rest - mid
```

which is exactly 2 / 3 / 3 at eight slots and integral at every count from four to twelve
[D-89]. The three quota keys are that proportion's output at eight and nothing reads them
once the proportion exists.

Rejected: keeping them as the proportion's inputs. Three keys that must sum to slots, can
be set so they do not, and nothing would notice.

Rejected: keeping them for eight and computing only above it. Two code paths that must
agree, of which the one that runs today is the one nobody exercises.

`SCREEN_LIFECYCLE.md` §10 already says the proportion introduces no value, so a key for it
would be a key with nothing behind it. Config is append-only, so the existing rows stay
where they are and the retirement is a removal from the seeder and from
`CONFIG_REFERENCE.md` rather than a delete.

**D-117 A gated name is scored, excluded at allocation, and its slot passes to the next
name of the same size. `gate_state` records whether every gate reason was evaluable.**
`ACTIVE`
C13 does not read `gate_result` and must not. The floor is the 98th percentile of the
whole scored population, so a floor over the ungated subset moves when a position opens or
a cooldown expires, which makes a screen's floor a function of the portfolio [INVARIANT 1,
INVARIANT 2]. C14 reads `gate_result` and drops gated names from the ranked list before
slots are filled.

Filling then continues within the bucket, and the distinction from D-8 is the whole of
this clause. D-8 forbids backfilling from a larger bucket, which is the case where nothing
of that size cleared the screen's floor. A name that cleared the floor and is unavailable
tonight is a different case, and conflating the two either leaves a hole D-8 does not ask
for or leaks the guarantee D-8 exists to hold.

The five gate reasons are named in `ARCHITECTURE.html` §03 and their thresholds are keys
under a new `gates.*` namespace, one per threshold, with no literal at a call site
[`CLAUDE.md` §8]. Every failing reason is recorded rather than the first, which
`SCHEMA.md` already requires of the column, so `passed` is the empty reason array rather
than a separately written flag.

Three of the five are structurally unevaluable over the backfill window rather than
passing over it. `position` and `trade_outcome` hold no rows until phase 7, so
already-held and cooldown can never fire. `events.earnings_backward_days` is 7 and
earnings are deliberately not backfilled, so the blackout has no calendar to read on a
historical date.

So `attribution.gate_state` carries `passed` or `passed_partial` and never `gated`, a
gated name having no attribution row to carry it. `passed_partial` means at least one gate
reason was structurally unevaluable on that date, and it is what stops a backfilled row
reading identically to a live one when it is not. This is the D-58 and D-69 pattern met a
third time and the first time it is labelled on the row rather than found afterwards.

**D-118 S4 drops `inst_ownership_change` and runs on its two insider inputs.** `ACTIVE`,
closing D-69.
D-58 removed `short_interest_change` from S4 for this fact in a different form: a screen
whose backfill scores come from a different population than its live scores has a floor
drawn from a distribution the live screen does not share. Deciding the same question the
other way here, with no new evidence, is result-shopping under `CLAUDE.md` §11.

The independent argument does not rest on backfillability at all.
`inst_ownership_change` is a change in the top twenty holders' total, so when a holder
enters or leaves that set between report dates, composition change and ownership change
are added together and the metric cannot separate them [`BUILD_PLAN.md` carried
obligations, 1 to 4]. The input is defective whether or not it has a history.

Rejected: keep all three and accept a two-input backfill distribution against a
three-input live screen. That is what D-58 rejected, and its failure is a floor that is
simply wrong and inspectable by nothing.

Rejected: source ownership from SEC EDGAR 13F. Free and complete, and a second provider
and a real ingest, which is a phase-1-shaped body of work added to a selection phase's own
scope [`CLAUDE.md` §3].

Two costs are recorded rather than glossed. Both surviving inputs come from one endpoint,
so a single provider change now takes the whole screen rather than one input; the screen
was designed with four inputs from three sources and this is the second removal. And
`SCREEN_LIFECYCLE.md` §7.6 notes the family covers no axis but S1's, so a later S4 failure
leaves the flow axis uncovered and is a design decision rather than a swap.

One measured caveat is carried forward rather than discovered in phase 8.
`sec-filings/{t}/form4` returns 404 Symbol not found for every delisted name tested,
against the same ticker strings `eod/{t}` answered for in the same run [D-69, measured at
3.1]. So S4's backfilled distribution, and therefore its floor, is measured over
survivors. That is a known property of this screen's floor and belongs in `PROGRESS.md` as
one.

`flow_daily.inst_ownership_change` keeps computing and stays available to the dossier as a
static feature. It stops being a ranking input, not a column.

**D-119 The shadow family is not registered in phase 4. The mechanism is built and proven
against a fixture screen, and the registration decision is taken at this phase's
sign-off.** ~~`ACTIVE`~~ **`AMENDED BY D-129`**, which takes the deferred decision for the
three backfillable members and takes it before 4.14 rather than at sign-off. The reasoning
below stands and is what D-129 acts on; what it did not reach is that sign-off falls after
the attribution write, and a screen registered after that can never carry a backfilled row.
`SCREEN_LIFECYCLE.md` gives phase 4 the mechanism: C13 iterating on `screens.<id>.state`
and C14 recording a shadow to `tuner.slot_cap` under the same proportional quota. It does
not require that any family member be registered while that mechanism is built, and the
two are separable.

Registering the family in this phase costs its share of §9.1, taking `screen_score_daily`
from roughly 1.4 GB to roughly 2.2, and requires D-90 to be answered before the phase can
start rather than when the fork actually bites.

So the mechanism is exercised against a fixture screen registered as shadow and removed
again, which is the same proof at none of the cost. The screen that proves a shadow writes
attribution and no `candidate_set` row does not have to be a screen anyone intends to
promote.

D-90 is therefore not owed until the family is registered, which is `SCREEN_LIFECYCLE.md`
§7.5's own condition read literally: the fork is about X-PEAD's registration, and nothing
registers here. The same applies to the three backfillable members, whose registration is
a decision taken on the measured live backfill rather than before it.

What this buys beyond cost: the registration decision is then taken by someone who has
seen five live screens scored over 1,462 dates, with the persistence figures in §5 in
front of them, rather than by someone reasoning about a mechanism that has never run.

**D-120 S5's quality and technical gates are metric lists of S5's own, and the duplication
against S1 is the price of INVARIANT 2.** `ACTIVE`
`ARCHITECTURE.html` §05 states S5's gate as "S1 top quintile AND technical bottom quintile
AND stabilising", which read literally is one screen reading another. `WORKED_EXAMPLE.md`
§3 says what it means: the two gates traced there are a quality composite percentile of 84
and a technical composite percentile of 12. Neither composite is defined anywhere, and
"technical bottom quintile" names inputs no document lists.

`screens.s5.quality_metrics` holds the same content as S1's metric list, copied.
`screens.s5.technical_metrics` holds `dist_200dma`, `dist_52w_high` and `rs_change_63d`,
each direction high, so a low composite is a name that has fallen against its cell on all
three. Both are composed with D-112's arithmetic from the percentile store and neither
reads another screen's score or config.

`screens.s5.quality_quintile_min` is 80 and `screens.s5.technical_quintile_max` is 20, top
and bottom quintile on the 0 to 100 scale `METRICS.md` §6.3 fixes.

The copy is deliberate and is stated rather than deduplicated. Removing it is what
INVARIANT 2 forbids, in the invariant's own words: sharing a computed value between screens
looks like removing duplication and destroys the independence the design rests on. What
holds it is a per-screen config facade that throws when asked for another screen's key, so
the shortcut is unavailable rather than discouraged.

**D-121 C12 GateEngine computes `gate_state`, and `gate_result` is the column that carries
it.** `ACTIVE`
D-117 defines what `gate_state` means and never says which component works it out, so this
closes the siting rather than the meaning. Authored 2026-08-23 on the operator's direction,
before 4.14 froze anything, the build having reported it rather than closed it.

Evaluability is a property of the gate evaluation itself. C12 is the only component that
asks every reason on a date, so it is the only one that knows which of them could be asked,
and recording a fact where it is already true costs nothing.

Rejected: C14 CandidateAllocator computes it. It would have to read `position`,
`trade_outcome` and `events` to decide which reasons were evaluable, its Reads cell names
none of the three, and the amendment would have been the eighth taken on that column in
four phases [D-125].

Rejected: compute it at read time. `gate_state` is a statement about what was knowable on a
past night, so a later reader asking the same question of a populated store gets today's
answer to a question about a past date. That is the reconstruction INVARIANT 4 forbids, met
at a different column.

Migration `0019` puts the column on `gate_result` with a CHECK closing it to `passed` and
`passed_partial`, and C14 copies it onto the attribution row it writes.

**D-122 A gate reason is evaluable when the store it reads holds something bearing on the
date, decided per date and never per ticker.** `ACTIVE`
D-117 says `passed_partial` means at least one gate reason was structurally unevaluable and
leaves the reading of unevaluable open. Authored 2026-08-23 on the operator's direction.

Per ticker turns an empty store into a positive answer about a name. `position` holds no
rows until phase 7, and a per-ticker reading would report already-held as evaluable for
every name on the grounds that nothing is held in any of them. That is a true sentence
about the store and a false one about the night, where the reason could not be asked at
all.

**The consequence is stated rather than discovered.** Every row 4.14 freezes reads
`passed_partial`. `position` and `trade_outcome` are empty across the whole backfill window
and `events.earnings_backward_days` is 7, so the blackout has no calendar on a historical
date and three of the five reasons were unevaluable on every one of them. The column
therefore discriminates nothing within the backfill and separates the backfill from live
history at the boundary, which is the whole of what it is for and is worth having.

**The obligation that follows is phase 8's and cannot be met afterwards.** Rows carrying
`passed_partial` and rows carrying `passed` are not one population, and any measure that
pools them is measuring the boundary rather than the screens. It is in `BUILD_PLAN.md`'s
carried obligations as well as here, because that is where the planning for phase 8 looks
and a rule recorded only beside the code that has it is not recorded [`CLAUDE.md` §7].

**D-123 The halt gate fires when the session has no bar for the name, or a bar with no
volume.** `ACTIVE`
`ARCHITECTURE.html` §03 names halt as one of the five gate reasons and nothing in this
corpus defines it operationally. `CONFIG_REFERENCE.md` records that it carries no threshold
key, so it is not a volume floor with the number left out. Authored 2026-08-23 on the
operator's direction.

It is the only reading `price_daily` supports. A halted session produces no print, and the
bulk end-of-day file carries no halt flag, so an absent bar and a bar with zero volume are
the two observable forms of one fact.

Rejected: define it against a halt feed. That is a second source and a real ingest, which
is a phase-1 shaped body of work added to a selection phase's own scope [`CLAUDE.md` §3].

Measured at 4.12: it fired on 9 of 2,819 names on the warm-up night of 2026-08-07.

**D-124 `candidate_set.slot_filled` is written null and has no writer.** `ACTIVE`
The column is in the `0001` snapshot and no document in this corpus says what a `true` in
it means. Authored 2026-08-23 on the operator's direction, before 4.14.

Three readings are each defensible and nothing chooses between them: that this candidate
occupied a slot, that its slot was fillable, or that the bucket it sits in reached its
quota. Any value written is a guess, and a guess stamped on a row no later pass may rewrite
is worse than an absence, because null says unknown truthfully and a boolean says something
[`CLAUDE.md` §6].

So the column stays and carries nothing. Recorded here rather than left for a later phase
to meet as an oversight: a reader finding an all-null column meets a decision instead.

Rejected: drop the column. `SCHEMA.md` and the migrations would both move for a column that
costs nothing to keep, and the meaning it was reserved for may still be authored.

**D-125 The three Reads-cell amendments taken during phase 4's build are ratified, and
the drift they are part of is a finding about the column rather than about them.**
`ACTIVE`
Authored 2026-08-23 on the operator's direction. C13 gains `screen_history` at 4.5, C13
gains `sentiment_daily` at 4.7, and C12 gains `security_daily` at 4.8. Prior wordings are
in `CHANGELOG.md` [D-73].

Each was forced rather than chosen. In every one of the three the component provably had
to read the store to implement a rule an active decision states, and the two remaining
options were a stage that cannot open the table it needs or a declaration that lies about
what the code reads. The direction is code-reads-what-the-cell-does-not, which
`ReadDeclarationConformanceTests` gives no exemption list by design, so none of the three
could have been recorded as a deviation instead.

**The finding is the count, not the third instance.** Reads cells in §3 have been amended
on twelve recorded occasions across phases 2, 3 and 4, six of them within phase 4 alone
and three of those six on one cell. A column amended that often is either being written
before the components exist, which it is, or being read by something that keeps
disagreeing with it, which it also is. Recorded so that the next phase meets a known rate
rather than a surprise.

**The answer is a check, and the Reads column already has one.** D-74 built it after four
deviations went unnoticed, and it is what found all six of phase 4's: each one failed the
suite rather than being noticed by a reader. The column that had no check was Writes,
which the carried obligation raised at code review `0006` says in terms, and which
predicted that a component gaining a second write would drift immediately. Q.3 builds it,
and it found two on its first run.

**Item 2 named two of the three and 4.5's is the same case one checkpoint earlier**, so
all three are ratified together rather than leaving one unaddressed for a reason that
would be arbitrary.

**D-126 `alert.alert_type` holds `megacap_share` and `distinct_tickers_60d`, and the
vocabulary is closed by a CHECK.** `ACTIVE`
`SCHEMA.md` gave the column no vocabulary at all, so the two strings C28 writes were the
build's own and nothing stopped a later phase spelling one of these conditions a third
way. Authored 2026-08-23 on the operator's direction.

The two strings are their conditions' config keys less the bound, so a reader who has one
has the other. Migration `0021` holds the same list on the column, which is `0018`'s
instrument for the gate reasons applied unchanged: a vocabulary closed in code alone
leaves the store able to carry a string no reader can interpret.

**The vocabulary is the alert types that have a writer, and it is deliberately not
larger.** `ARCHITECTURE.html` §03 gives `alert` one writer and §18 gives that writer two
conditions. §18's table names four more whose row reads "Alert" and whose owner is C07 on
completeness, C03 on the filing date substitution rate, C13 on every live screen returning
zero, and C26 on the cache miss rate. None of those components declares a write to `alert`
and no document says how their alerts are recorded, so admitting a guess at their strings
would be inventing a vocabulary rather than closing one. A phase that gives one of them a
writer extends the constraint, which is a migration and therefore visible.

**The correction this decision also carries.** 4.11 reported C28's twenty-date megacap
window as unauthored and that was wrong. §18's failure table states the condition as
"Megacap share above a third over 20 days", found with the whitespace-tolerant pattern
`Megacap\s+share\s+above\s+a\s+third\s+over\s+20\s+days` against the document's tags
stripped [`CLAUDE.md` §7]. The number is authored and no key is renamed on the strength of
a finding that was not true.

**What is genuinely open is narrower and is not closed here.** §18 states both windows in
"days" and C28 measures both in candidate dates. Candidates exist per session, so a window
of sessions is what the guarantee is about and a calendar span over a holiday week reaches
back a different number of nights, which is the distinction the trailing floor window got
wrong at 4.5. The reading is the build's and it is recorded rather than ratified, because
the two readings differ by about nine sessions a year on the sixty-day window and neither
document distinguishes them.

**D-127 `screens.slot_ceiling` is retired.** `ACTIVE`
It has never had a reader. `CONFIG_REFERENCE.md` named CandidateAllocator as its consumer
from the first corpus and 4.9 read the composition code and found nothing resolving it,
which that document's own rule makes worse than an absent entry rather than harmless.
Authored 2026-08-23 on the operator's direction.

The reason it has no reader is structural. D-7 gives a ceiling of eight slots per screen,
`screens.<id>.slots` is what a screen actually has and what the tuner moves, and a ceiling
of eight cannot also cap that without contradicting D-43's cap of twelve. D-116's
proportion is what turns a slot count into a quota, so nothing is left for the key to do.
C14 validates a screen's slot count against `tuner.slot_floor` and `tuner.slot_cap`.

**Two keys answering one question with different numbers is D-116's own case**, and the
per-screen key wins it by having a reader.

The rows already inserted are untouched, config being append-only. This is a removal from
the seeder and from the reference and not a delete, which is exactly what D-116 did with
`screens.quota_large`, `quota_mid` and `quota_small`.

`ConfigSeeder.Keys` goes 84 to 83, and the count assertion in `ConfigResolutionTests` moves
down for the first time. That assertion was written to catch a key with nothing behind it
and this is the opposite case, a key with nothing in front of it, so it records the
direction rather than only the number.

**D-128 `SCREEN_LIFECYCLE.md` §4.5's reader count is seven, which is what its own table
marks, and the table is the specification.** `ACTIVE`
The section's summary said eight readers mean "candidate" and its table marks seven.
Authored 2026-08-23 on the operator's direction, settling the count at whichever the
enumeration supports rather than at whichever sentence was written first.

The seven are C23 LessonWriter, C24 CalibrationReporter, U2 Candidate detail, U7 Primary
claim, the abstention analysis, `VALIDITY.md` §3's counts and `WORKED_EXAMPLE.md`. The
same sentence's other two figures were right, three meaning everything surfaced and three
not reading the table at all, which is what made the wrong one hard to see: a reader
checking it ticks two and stops.

**A table is checkable and a sentence is not**, so the sentence is held against the table
rather than the other way round. `ScreenLifecycleCountTests` counts the Means column and
asserts both prose statements against it, §4.7's "seven chances to forget one" included,
since that number drifted with §4.5's and would drift again on its own. It also asserts
that no live sentence in the section still says eight, because a corrected sentence can
sit beside an uncorrected one and a positive pattern passes over both.

The item was reported before phase 4 and survived the whole of it unchanged, for the
reason the rest of this corpus keeps finding: nothing read either number.

**D-129 The three backfillable shadows are registered before 4.14 runs, and the ordering
is the reason.** `ACTIVE`, amending D-119's timing and taking its deferred decision for
X-NSI, X-ACC and X-FM. Authored 2026-08-23 on the operator's direction.

`attribution`'s key is `(ticker, date)` with `score_per_screen` one jsonb object across
screens [D-110, `SCREEN_LIFECYCLE.md` §4.2]. So a screen registered after 4.14 could gain a
backfilled row only by updating a frozen one, which `CLAUDE.md` §12 and `RUNBOOK.md`
prohibit outright. **After 4.14 no screen can ever have a backfilled attribution record,
only a prospective one.**

D-119 put the registration decision at this phase's sign-off, and sign-off falls after
4.14. That sequence removes an option rather than deferring it, and nobody chose to give it
up: D-119's reasoning is about who takes the decision and on what evidence, and it does not
reach the question of what is still available when they take it.

**D-119's own condition is met rather than bypassed.** It defers so that the decision is
taken by someone who has seen five live screens scored over the range with §5's persistence
figures in front of them. Those figures exist as of 4.13 and are in `PROGRESS.md`. The
evidence D-119 was waiting for is what this decision is taken on.

**What registering buys is a distribution to look at, which is the whole reason the family
exists.** It changes nothing about how a shadow is promoted: D-86 already reads prospective
observations only for a promotion or a retirement, and backfilled observations count toward
a shadow's trailing distribution and nothing else. A shadow with no backfilled record has no
D-9 floor for its first 250 sessions and no record at all for its first year, which is the
condition §5.1 states.

**The three, and why not four.** X-NSI and X-ACC are extractions from S1 and X-FM is an
addition, and each ranks on one `valuation_daily` column that C09 already writes and C11
already percentiles [§7.2, §7.4]. X-PEAD is not registered, because D-90 is `OPEN` for it
alone: `events.earnings_backward_days` is 7 and `announced_date` is null for earnings, so
its backfilled distribution would come from a different population than its live one, which
is what D-58 removed short interest for. Registering it here would answer an open fork by
side effect.

The cost is the backfill's row count at eight screens rather than five and three more score
entries in each `score_per_screen` object. §9.1 prices the storage and D-119 states it as
roughly 1.4 GB going to roughly 2.2.

**Had the answer gone the other way it would still have been a decision taken here**, since
prospective-only for ever is what the ordering produces on its own and inheriting it is not
the same as choosing it.

---

## Digest chain

Phase 5's twelve. **Adopted 2026-08-24 on the operator's direction, ahead of the phase's
first checkpoint**, from `prompts/BuildPlans/phase-5-digest-chain.md` §4, which is where the
reasoning was first set out and which is archived as issued. Two of the twelve close items
carried rather than opening new ground: D-141 closes the `events` point-in-time obligation
`BUILD_PLAN.md` has held from phase 1, and D-142 answers the question phase 3 handed here
about `earnings_history`.

**D-131 `headline` carries the article body, and the digest reads it rather than the
title.** `ACTIVE`
`ARCHITECTURE.html` §07 prices local enrichment against articles of five to eight hundred
tokens, and `headline` had no column that could hold one: `ticker`, `date`, `published_at`,
`title`, `source` and `url`. A digest built from titles alone summarises about forty-five
tokens of input, which is not the condensation §07 argues for, and it would leave the
qualitative gap that section identifies unaddressed while every output looked normal.

`content` is nullable, because the endpoint may send an article without one. Null there
means the provider sent no body rather than that the body was empty, and a row carrying null
content is excluded from the digest input and counted [`CLAUDE.md` §6]. `published_at` is
the column D-133's lookback window is read against.

**D-132 `headline` is idempotent by run scope: C29 deletes the ticker and date it is about
to write and reinserts.** `ACTIVE`
This closes the obligation `BUILD_PLAN.md` carried from phase 1, that `headline` is an event
record and the snapshot grain the other layer-3 stores have does not apply.

Two articles about one company on one day are not a duplicate to be collapsed; they are two
articles, and they may share a title, a source and a publication timestamp while differing
in body. So a unique index on the row's own attributes is the wrong instrument, and reaching
for one is how this starts badly [D-68].

C14 already establishes the shape and `SCHEMA.md` states it: the allocator deletes the date
before it rebuilds it, both operations are its own, and INVARIANT 10 read per operation is
untouched. C29 declares `Insert` and `Delete` on `headline` and nothing else writes there.

**The scope is ticker and date rather than date alone**, because C29's unit of work is a
candidate and a partial night must not delete the candidates it did not reach.

**D-133 The digester reads at most `digest.max_articles` articles published within
`digest.lookback_days` of the date being processed, most recent first.** `ACTIVE`
Neither figure existed anywhere in this corpus and both are needed before the first call.
`digest.max_articles` is 3 and `digest.lookback_days` is 7.

**Three is read off `ARCHITECTURE.html` §07's own cost figures rather than chosen.** The
rotation at about $1.30 a year and a full secondary year at roughly $18, against Haiku 4.5
at $1.00 per million input tokens and $5.00 per million output, 28 candidates and 252
sessions, leave about 1,800 input tokens per candidate, which at §07's five to eight hundred
tokens an article is three. It also agrees with the S3 and S5 screen blocks, which give
three headlines each.

Seven days is the window the digest's recency is about. §07 asks for what happened recently
and the apparent cause of a sharp move, and `s5.news_gate_min_articles` already operates at
three articles in seven days. D-60's ninety days is a different measurement, of whether a
name is covered at all, and is not this.

**`digest.lookback_days` and `s5.news_gate_min_articles` read the same seven days and are
separate keys deliberately.** One is what the digest summarises and the other is when S5's
two news conditions fail open. They agree today because seven days is the right window for
both questions rather than because they are one question, and harmonising them into a single
key, which is the tidy-looking edit a later session will reach for, leaves the gate and the
digest no longer independently movable. That coupling would be inherited rather than chosen.

Most recent first, ties broken on the source string ordinally, so the selection is
deterministic when a name carries more than three articles in the window. Five of the seven
names phase P probed carried more than three in ninety days and NVDA.US carried 1,000, so
the tie-break is reached in practice rather than in principle.

**D-134 `news_digest` distinguishes four outcomes and a null `digest_text` means no article
was available to read.** `ACTIVE`
`digest_text` is nullable and `provider` and `model_name` are NOT NULL [D-29], so a row
exists only where a link answered or was asked. That leaves four states and they must stay
separable, because D-60's no-digest disqualifier bites on one of them and not on its
neighbour.

No row: the ticker was not a candidate that night, or the run halted before the digest step.
A null `digest_text`: a link was selected and there was nothing to send, meaning no article
inside the window or every article carrying a null body [D-131], and this is "no digest
available" in D-60's sense. `NO MATERIAL NEWS`: a link read the articles and returned the
escape hatch `prompts/digest-instruction.md` defines, which is a reading rather than an
absence and which D-60 does not bite on. Prose: an ordinary digest.

**The middle two are the pair that gets collapsed**, and collapsing them makes S5's
disqualifier fire on thinly covered small caps, which is the population the design exists to
reach and the asymmetry D-60 was written to prevent.

`news_digest` gains a CHECK asserting `model_name` is non-empty rather than merely non-null,
because an empty string satisfies NOT NULL and records nothing. The `provider` vocabulary is
closed by a CHECK to the chain's link names, which is `GateReasons`' and `AlertTypes`' shape
and is deliberately the same shape [D-126].

**D-135 `attribution.digest_provider` stays null. `news_digest.provider` is the record and
no third component writes `attribution`.** `ACTIVE`
The column exists in `0001` and nothing can fill it. C14 inserts the attribution row at
18:30 and the digest is produced at 18:33, so the provider is not known when the row is
written; C21 owns the update and only of the nine return columns; and `SCHEMA.md` says in
terms that no third component writes here at all, which is a stronger claim than the
exception it replaced. Adding C33 as a second claimant of `attribution.Update` would also
collide with C21 on the table-and-operation pair `WriteOwnershipConformanceTests` asserts
over.

**Nothing is lost.** `news_digest` is at ticker by day, `attribution` is at ticker by day,
and the provider sits on the first at the same grain, so `VALIDITY.md` §5's mitigation, that
`digest_provider` is recorded and the rotation gives a paired sample, is met by the join
rather than by the copy.

This is D-124's disposition of `slot_filled` applied to a second column for the same reason:
null says unknown truthfully, and a value stamped on a row no later pass may rewrite is
worse than an absence. The 74,767 frozen rows are unaffected and could not be otherwise
[INVARIANT 4].

**D-136 A link is an implementation of one interface, the order comes from
`local_model_config`, and the seeder seeds both rows.** `ACTIVE`
`ARCHITECTURE.html` §07 asks for a chain rather than a primary with a fallback branch, so
that failover works in both directions with no rarely-executed code path. In code that is
one interface with a health check and a digest call, a list built from `local_model_config`
ordered by `provider_order` and filtered on `enabled`, and a caller that takes the first
healthy link. **Nothing in the caller knows which link is preferred.**

`local_model_config` is seeded rather than written by a stage. `SCHEMA.md` names the UI as
its writer [D-51] and the UI is phase 9, so until then the two rows arrive through
`ConfigSeeder` alongside the config keys, which is what `seed.ps1`'s header already
anticipates. The insert is `ON CONFLICT DO NOTHING` on `provider_order`, so a second run
changes nothing, and this adds no writer to the registry: `SCHEMA.md` already carries the
precedent that a table's writer may be configuration rather than a stage.

**`last_health_check` and `last_loaded_model` stay null and gain no writer in this phase.**
C32's Writes cell in §03 says nothing, via C27, and a health check is emitted through the
run log exactly as C07's abort is. Phase 9 inherits whether the UI fills them, and §20's
"last successful digest run" reads `run_log` until it does.

**D-137 A malformed or over-long response is retried once on the same link and then falls
through to the next. Nothing is truncated.** `ACTIVE`
§18 states the rule and no document said what malformed or over-long mean, so both are
stated here rather than left to a call site.

**Over-long** is a response whose token count exceeds `digest.max_tokens`, which is 150. The
cap is requested on the call and asserted on the response, because a provider that ignores
it returns a long answer with a normal status.

**Malformed** is an empty response, a response that is only whitespace, or a transport
failure. It is deliberately not a content judgement: a digest that reads oddly is not
malformed, and a step that decided which summaries were good enough would be judging
[INVARIANT 7].

**Never truncate.** A half-sentence digest is worse than none, because the researcher
receives it as a complete fact and the validator cannot check prose. A link that fails twice
is unhealthy for the remainder of that run, so a dead primary costs two calls rather than
one per candidate.

**The retry count is not a config key.** §18 states one retry as behaviour rather than as a
tuning parameter, and a key here would invite raising it, which trades a visible fall-through
for an invisible delay.

**D-138 The two candidates routed to the secondary are chosen by a stable hash of the run
date and the ticker, not by `System.Random`.** `ACTIVE`
D-27 says chosen by the date seed and `CLAUDE.md` §6 says `Random` is seeded from the run
date, so a seeded `Random` is the reading that first suggests itself. It is rejected for one
reason: the sequence a seeded `System.Random` produces is a runtime implementation detail,
and a framework upgrade that changed it would silently change which pair the rotation
compared, splitting the paired sample D-27 exists to build without anything saying so. That
is the failure class `CLAUDE.md` §1 is about.

The rule instead: order the night's candidates by an FNV-1a hash of the invariant
`yyyy-MM-dd` date string concatenated with the ticker, ties broken on the ticker ordinally,
and take the first `digest.rotation_count`. **The hash is written in this repository, so it
cannot move underneath the record.**

The rotation runs regardless of primary health [D-27], including on a night the primary is
down, where the rotation pair is indistinguishable from the fall-through except that
`was_rotation` is true on those two rows. That is what `was_rotation` is for. If the
candidate set is smaller than `digest.rotation_count` the whole set rotates and nothing is
padded.

**D-139 No healthy link halts the run at C33, and C28 moves ahead of C29 in the evening
order.** `ACTIVE`
INVARIANT 15 and §18 both state the halt in terms of the researcher and of orders, neither
of which exists in phase 5. The observable is narrower and is stated rather than assumed:
the run halts at C33, `NightlyRun` returns not completed, and no stage after C33 executes.

That has a consequence the documents do not address. C28 runs at 19:00, after C33 at 18:33,
and it is what raises the megacap-share and distinct-ticker alerts. On a halted night the
candidate set exists and is exactly as concentrated as it is, and losing its alert on the
nights something else is already wrong is the wrong direction. **C28's 19:00 in §04 is a
clock time rather than a dependency**: it reads `candidate_set`, `position` and
`security_daily`, and nothing the digest step writes.

So the evening order becomes C14, C28, C29, C33. The alternative, leaving C28 after C33 and
accepting that a halted night raises no concentration alert, is defensible and is not taken,
because the monitor exists so the guarantees are measured rather than assumed and a night
that halts is not a night they stopped mattering.

**D-140 The secondary link is Haiku 4.5 at `claude-haiku-4-5` through the official Anthropic
SDK, and C26 CostLedger is built in phase 5 rather than phase 6.** `ACTIVE`
The model id is a config key rather than a literal, `digest.secondary_model_id`, so that a
model change is a config version and a splittable history rather than a code edit
[`CLAUDE.md` §12].

The SDK rather than a hand-rolled client. `EodhdClient` is hand-rolled because that provider
has no SDK and because the rate limiter and the unit allowance are this system's own;
neither applies here.

**C26 moves, and the reason is that this phase spends money.** `BUILD_PLAN.md` phase 6
carries the obligation that the cost ledger must be recording per model before the first
full night, not after, and phase 6 held it because phase 6 was believed to be the first
phase that spends money. D-27 makes phase 5 the first: two candidates a night go to the
secondary regardless of primary health, so the first ordinary night this phase runs makes a
paid call. The obligation's condition is met one phase early and the obligation moves with
it rather than the money going unrecorded for a phase.

What C26 records here is one row per digest call that reached the secondary: `date`,
`model_id`, `portfolio_id` null, the four token counts from the response usage, `cost`
computed from a config-held price, and `was_batch` false. `cost` is `numeric` [INVARIANT
16] and the price keys are decimal. **It is deliberately not the whole of C26**: the cache
hit rate, the annual budget alert and the validator rejection counts are phase 6 and phase
10 concerns and none of them has an input yet.

**D-141 A backfilled `events` row is not point-in-time for an earnings date, no first-seen
column is added, and the blackout stays inert over history.** `ACTIVE`
This closes the obligation `BUILD_PLAN.md` carried from phase 1: `calendar/earnings` sends
no date on which a schedule became public, `announced_date` is populated for dividends and
null for earnings and splits, and the table has no first-seen column, so a backfilled row
and a live-accumulated one are indistinguishable.

Three things settle it and none is new work. **3.10 already loaded no earnings**,
deliberately, so `events` carries no earnings for any backfilled date and there is nothing
to separate. **Live accumulation is point-in-time correct by construction**, because a row
appears the night the calendar first lists it; that is a fact about how the rows arrive
rather than something the schema records, and a `first_seen` column would record it going
forward while saying nothing about the rows already there. **And the exposure is bounded
rather than open**: a schedule is usually published two to four weeks ahead,
`gates.earnings_blackout_days_before` is 5, and a blackout narrower than the publication
lead behaves the same either way.

So no column, no backfill, and C12's earnings blackout is inert over the whole frozen window
and live from the first night it accumulates.

**The consequence is stated rather than left to be found.** The 74,767 frozen `attribution`
rows were selected under a gate with one of its five conditions never firing, uniformly
across survivors and delisted names alike, so any analysis comparing frozen-window selection
against live selection is comparing four gates against five.

**D-142 `earnings_history` cannot serve C12's earnings blackout, and the reason is
structural rather than a gap in the data.** `ACTIVE`
Phase 3 left this open in terms, at D-98's discussion: whether `earnings_history`, which C03
populates over the widened pool [D-96], can serve the gate `events` cannot.

It cannot. The blackout needs the **next** earnings date, which is forward-looking, and
every read of `earnings_history` is keyed on `report_date <= date` [D-96, INVARIANT 12].
That rule is what makes the table honest and it is precisely the rule that removes every
forward row. Lifting it to reach a scheduled future report would read a date out of a
payload fetched today with nothing recording when it became public, which is the same
lookahead D-141 declines for `events` arriving through a second table.

**So D-141 and D-142 are one finding seen twice.** A forward earnings date is point-in-time
only where the arrival of the row is itself the evidence of publication, and that is true of
live accumulation and of nothing else this system has.

`earnings_history` keeps its purpose, which is D-96's: `last_two_earnings_surprises`, and
whatever a drift screen would need if D-90 ever registers one. Neither is a forward date.
D-90's third option, widen the backward window and backfill earnings history first, does not
fix `announced_date` and is pointed here.

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
ownership source.** ~~`OPEN`~~ `SUPERSEDED BY D-118`
[closed 2026-08-23 by D-118, which drops `inst_ownership_change` from S4 and runs
the screen on its two insider inputs. The body below is kept rather than removed,
because D-118 cites these measurements rather than restating them.]
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
~~Owed to phase 4~~ [moved by D-119, not owed until the family is registered. The
mechanism is built in phase 4 against a fixture screen and no member is seeded, so
this fork is not reached there. The status is unchanged because the question is
moved rather than answered], and open rather than decided because it is a fork the
design cannot settle from what it knows. Its input has no backfillable history and
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
