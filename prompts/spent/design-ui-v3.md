# UI design, v3

    Target:     Claude Design
    Issued:     2026-08-05
    Status:     ISSUED
    Supersedes: design-ui-v2.md
    Produced:   (fill on completion)

The v2 output was dense as asked and unreadable, because the prompt never said that a
number invented by this project needs its comparison, its sample size and its threshold
attached to be legible. This version states that as the governing constraint, adds the
primary claim screen, and requires insufficient data to display as insufficient.

Record of what was asked. Never edited once run.

---

Design the UI for a personal paper-trading research lab. Single user, desktop-first,
runs locally on the user's own machine. Blazor front end calling a read-only C# API.
Light and dark themes.

Dense is correct. The previous version was dense and unreadable, and the reason was not
density. Read the legibility section below before the screens; it governs everything.

WHAT THE SYSTEM DOES

Each evening after the US close it screens the entire US stock market (~2,000 names
above $300M market cap, $5 a share and $2M average daily dollar volume) through five
independent screens, each looking for something different:

  S1 Quality at a fair price   good businesses cheap against their own history
  S2 Trend                     names whose relative strength is improving
  S3 Sentiment inflection      a coverage spike against that ticker's own baseline
  S4 Flow                      insiders buying, short interest falling
  S5 Mean reversion            quality names beaten down and now stabilising

Each screen contributes up to 8 names under a size quota of 2 large / 3 mid / 3 small,
deduplicated into roughly 28 candidates. A summariser condenses each candidate's recent
news into a digest. That summariser is an ordered chain: a local model on the user's own
GPU is primary, Haiku 4.5 is secondary, and if neither is healthy the entire run halts
before any researcher call.

Two AI researchers then judge every candidate independently and identically, Opus 5 and
DeepSeek V4 Pro, on the same dossier with the same rubrics. Each returns BUY or PASS, a
probability that the trade reaches its target before its stop, a thesis under 60 words,
a counter-argument under 20 words, and a suggested stop and target. A validator checks
every figure they cite against the dossier and rejects any that do not match. A risk
layer sizes what survives, entering at most 2 new positions a day against 8 open.

Four portfolios run on the identical candidate set with identical risk rules, so any
difference between them is selection: Research: Opus 5, Research: V4 Pro, Screens (each
screen's top name in fixed rotation, no AI), and Random. Screens and Random match their
entry count to whichever research portfolio is primary. Portfolios come from a config
registry, so nothing may assume there are four or that they are named these things.

**This is a measuring instrument that happens to trade.** The question it exists to
answer is whether the researcher selects better than the screens. That is what the
interface is for.

LEGIBILITY, WHICH GOVERNS EVERY SCREEN

A trading terminal can be dense because price and volume explain themselves. Almost
nothing here does. Peer-relative alpha, slot fill, screen floor, Brier score, an article
count z-scored against a ticker's own 90-day baseline are all inventions of this system,
and a bare figure for any of them tells the reader nothing.

Do not solve this with help text or tooltips on everything. That adds clutter to screens
that are already full. Solve it with these five rules.

1. **No number appears without its comparison and its sample size.** Not "alpha 2.4%"
   but "+2.4% vs peers, n=340". The comparison is what gives it meaning and the count is
   what says whether to believe it. This applies to every rate, every alpha, every hit
   rate, every fill percentage.

2. **Every threshold the system already knows is drawn, not remembered.** The screen
   floor on the score distribution. The position and sector caps on the exposure bars.
   The breakeven probability implied by a stop and target, drawn against the stated
   probability. The pre-registered evidence threshold on the primary claim chart. If the
   line is on the chart, nobody has to recall its value.

3. **Every screen opens with one generated sentence stating what it currently says.**
   Not a written description of the screen. A sentence computed from tonight's data.
   "Both models agreed on 24 of 27 candidates tonight and passed on all of them." "The
   sentiment screen has filled under half its slots for three weeks." "Not enough
   observations yet to separate the models." The table below is the evidence for that
   sentence. This is the single most valuable element on every screen.

4. **Definitions arrive on demand, not permanently on screen.** A quiet affordance on
   any invented term opens its definition and where the number comes from. Assume the
   text is served from a glossary the application already has, so wording cannot drift
   from the project's own documentation.

5. **Insufficient data displays as insufficient.** For the first months most of these
   views cannot support a conclusion. Show the observation count against what is needed
   and say so plainly, rather than rendering a chart that looks finished. A confident
   chart drawn on forty observations is the most dangerous thing this interface could do.

PERSISTENT ACROSS EVERY SCREEN

A summariser chain indicator in the application frame, with three states that must look
different at a glance: running on local, running on the secondary, and no link healthy.
The last means tonight's run will not happen. Running on the secondary is a legitimate
state rather than a failure, and the indicator shows which link is answering rather than
a binary light.

The user runs the local model themselves and will not reliably remember to check it, so
a readiness check runs a couple of hours before the evening window. The indicator also
shows the currently loaded local model name and time since the last successful digest.
Clicking it opens the connection panel.

SEVEN SCREENS, IN THIS NAVIGATION ORDER

The order is deliberate and should be visible. Equity curves are the most compelling
thing here and the least informative for the first two years, so they are not first.

1. PRIMARY CLAIM — the first screen, and the reason the system exists
   Peer-relative forward return at 5, 21 and 63 days for candidates the researcher said
   BUY against those it said PASS. Observation count on each side, always. The
   pre-registered threshold drawn on the chart: supported above 1.5 percentage points of
   separation at 21 days with at least 1,000 observations each side, falsified below
   0.3, and an explicit inconclusive band between them that must be visually distinct
   from both.
   Broken out per model and per screen. Also the abstention question: on nights the
   researcher declined everything, what did the candidates it passed on go on to do.
   Until the sample is large enough this screen says so and shows how far away it is.

2. TONIGHT'S RUN
   Tonight's ~28 candidates as a dense table: ticker, company, size bucket, sector,
   which screens surfaced it, and both models' verdicts side by side with their
   probabilities and an explicit agreement column. Disagreement between the models is
   the most interesting thing here and should be findable without reading. Most verdicts
   are PASS, so BUYs need to stand out. The night's funnel from candidates to BUYs per
   model to orders placed. Market regime, run cost, validator rejections.

3. CANDIDATE DETAIL
   Every metric as a raw value beside its percentile within its size-and-sector peer
   group, since the percentile is what makes it comparable. Show both; neither alone is
   readable. The news digest and which provider in the chain produced it, since two
   candidates a night are deliberately routed to the secondary. The exact dossier text
   each model was given. Both verdicts with probability, thesis, counter-argument, stop
   and target, and the breakeven their own stop and target imply, drawn against the
   stated probability so an internally inconsistent call is visible without arithmetic.
   Price chart with the 20 and 200 day averages. Once matured, forward returns three
   ways: raw, against SPY, and against peers.

4. SCREENS
   The five screens as cards: current slot allocation out of 40, how often each fills its
   8 slots, hit rate, mean peer-relative alpha with its observation count, and the
   current floor drawn on that screen's score distribution rather than stated as a
   number. History of how the monthly tuner has moved slots. Recent candidates per screen
   and how they went. A screen quietly going dark should be obvious here, so fill rate
   over time matters more than its prominence suggests.

5. CALIBRATION
   A reliability diagram per model: stated probability on one axis, realised outcome on
   the other, **with the 45-degree line drawn**, because that line is the entire point of
   the chart and without it the plot says nothing. Bucket counts shown, since a bucket
   with nine observations should not look like one with nine hundred. Brier score with
   its decomposition and its trend. The same per screen. The active lessons each
   researcher is being given, with the sample size behind each and its expiry date.

6. PORTFOLIOS
   Equity curves for every active portfolio with SPY as reference. **Closed trade count
   beside every curve, and the comparison marked underpowered until each portfolio has
   roughly 400 closed trades**, which takes two to three years. This marker is not
   decoration: this screen is the most visually convincing in the system and the least
   informative early, and the interface should say so rather than letting the chart
   imply otherwise.
   Per portfolio: open positions with entry, current price, unrealised P&L, stop distance
   and days held. Total return, hit rate, average win and loss, max drawdown, Sharpe.
   Exposure by size bucket and sector with the caps drawn in. Closed trades with exit
   reason. The comparison that matters most is Research: Opus 5 against Research: V4 Pro,
   since that is nine times the cost for whatever difference appears.

7. RUN HEALTH
   Pipeline stages as a timeline with status, duration and row counts. The Opus 5 stage
   is asynchronous, submitting a batch in the evening and completing when results return
   against a hard 09:00 deadline, so the timeline needs a waiting state that reads as
   normal rather than stuck. Data freshness per source. Cost per night and a running
   annual total against a $100 budget, split by model. Cache hit rate. Validator
   rejection rate per model, which measures how often each invents a figure and is the
   earliest signal that separates them. The summariser chain in full: each link with its
   health, endpoint, loaded model, latency and share of recent digests, plus reconnect
   and test. Concentration alerts, being megacap share of recent candidates and distinct
   tickers over 60 days. Recent failures with reasons.

CRAFT

Dense tables readable at 30-plus rows without pagination. Numbers monospaced and
column-aligned. Restrained colour carrying meaning only: verdicts, P&L direction, alert
states, model agreement, and the three chain states. Charts legible small, since several
sit beside tables. Every screen survives being opened at 8am with no memory of last
night, which is what the generated summary sentence in rule 3 is for.
