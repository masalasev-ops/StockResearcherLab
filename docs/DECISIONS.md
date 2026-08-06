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
At least 250 trading days of history. The dollar volume floor was raised from $1M
because the participation cap would otherwise reject a typical $8,300 position.

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

---

## Open

**D-53 Whether the local digest model stays local once measured.** `OPEN`
The rotation in D-27 will produce a paired sample. If digest source proves not to
matter, the chain can be simplified. Do not act before a quarter of data exists.

**D-54 Whether both research portfolios are kept.** `OPEN`
Decide from the A against D comparison after at least a year, and from the
validator rejection rate per model, which is available much sooner.
