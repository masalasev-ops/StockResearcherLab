# UI design, v2

    Target:     Claude Design
    Issued:     2026-08-05
    Status:     SPENT, SUPERSEDED BY design-ui-v3.md
    Supersedes: design-ui-v1.md
    Produced:   Research_Lab.html, six screens

What it got right: the three-state summariser chain, both models side by side with an
explicit agreement column, the probability scale throughout, breakeven, the
counter-argument, peer-relative alongside SPY, and a data-driven portfolio list with a
primary role rather than four hardcoded entries.

Why it was superseded: the screens are dense as asked and give no way to read the
numbers on them, because this prompt never said that a figure invented by this project
needs its comparison, its sample size and its threshold attached. It also predates the
primary claim view and the underpowered marker on portfolios.

Regenerated after the digest provider chain replaced the single local model. Three
substantive changes from v1: the chain indicator has three states rather than two
because running on the secondary is legitimate rather than a failure; the second
research portfolio and the model-agreement view appear on the landing screen; and
conviction became a stated probability with a counter-argument field.

Record of what was asked. Never edited.

---

Design the UI for a personal paper-trading research lab. Single user, desktop-first,
runs locally on the user's own machine. Blazor front end calling a read-only C# API.
Data-dense and utilitarian, closer to a trading terminal or a good observability
dashboard than a consumer fintech app. No onboarding, no marketing surfaces, no
settings-heavy chrome. Light and dark themes.

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
deduplicated into roughly 28 candidates. A summariser then condenses each candidate's
recent news into a short digest. That summariser is an ordered chain: a local model
running on the user's own GPU is primary, and Haiku 4.5 is the secondary link. If
neither is healthy the entire run halts before any researcher call is made.

Two AI researchers then judge every candidate independently and identically: Opus 5 and
DeepSeek V4 Pro, same dossier, same rubrics. Each returns BUY or PASS, a probability
that the trade reaches its target before its stop, a thesis under 60 words, a
counter-argument under 20 words, and a suggested stop and target. A validator checks
every figure they cite back against the dossier they were given and rejects any that do
not match. A risk layer then sizes and caps whatever survives, entering at most 2 new
positions a day against 8 open maximum.

FOUR PORTFOLIOS, ALL ON THE IDENTICAL CANDIDATE SET

  Research: Opus 5     the frontier model judging
  Research: V4 Pro     a model roughly nine times cheaper, judging identically
  Screens              each screen's top-ranked name in fixed rotation, no AI
  Random               drawn at random from the candidate set

Identical sizing, stops and risk rules across all four, so any difference between them
is selection skill. Screens and Random match their entry count to whichever research
portfolio is primary, so nobody wins on market timing. The portfolios are a config
registry, not hardcoded: one may be retired or pointed at a different model later, so
nothing in the UI may assume there are exactly four or that they are named these things.

PERSISTENT ACROSS EVERY SCREEN

A summariser chain indicator in the application frame. This is not a binary up-or-down
light. It shows which link is currently answering, because running on the secondary is
a legitimate state the user still needs to see rather than a failure. Three states
matter and must look different at a glance: running on local, running on the secondary,
and no link healthy. The last one means tonight's run will not happen at all.

The user runs the local model themselves and will not reliably remember to check
whether it is up, so a readiness check runs a couple of hours before the evening window
and surfaces a problem while there is still time to start it. The indicator also shows
the currently loaded local model name, since the user swaps models, and time since the
last successful digest. Clicking it opens the connection panel.

SCREENS TO DESIGN

1. TONIGHT'S RUN — the landing view
   Tonight's ~28 candidates as a dense table: ticker, company, size bucket, sector,
   which screens surfaced it (a name can appear via more than one), and then both
   research models' verdicts side by side with their probabilities. Agreement and
   disagreement between the two models is the most interesting thing on this screen and
   should be visible without reading. Most verdicts are PASS, so BUYs need to be findable
   at a glance. Show the night's funnel: candidates, BUYs per model, orders placed. Show
   the market regime label, the run's cost, and any validator rejections.

2. CANDIDATE DETAIL — one name, opened up
   Every fundamental, technical, valuation, sentiment and flow metric, each as a raw
   value beside its percentile within its size-and-sector peer group, since the
   percentile is what makes it comparable. The news digest, and which provider in the
   chain produced it, since a couple of candidates each night are deliberately routed to
   the secondary. The exact dossier text each model was given. Both models' verdicts
   with probability, thesis, counter-argument, suggested stop and target, and the
   breakeven probability their own stop and target imply, so an internally inconsistent
   call is visible. A price chart with the 20 and 200 day averages. Once matured, the 5,
   21 and 63 day forward returns shown three ways: raw, against SPY, and against its
   size-and-sector peers.

3. PORTFOLIOS — the four compared
   Equity curves for every active portfolio on one chart with SPY as a reference line.
   Per portfolio: open positions with entry, current price, unrealised P&L, stop
   distance, days held. Summary stats: total return, hit rate, average win, average
   loss, max drawdown, Sharpe. Current exposure by size bucket and by sector with the
   caps drawn in, so a portfolio approaching one is obvious. Closed trade history with
   exit reason (stop, target, time stop, thesis change). The comparison that matters
   most is Research: Opus 5 against Research: V4 Pro, since that is nine times the cost
   for whatever difference appears.

4. SCREENS — where the tuning is watched
   The five screens as cards: current slot allocation out of 40, how often each fills
   its 8 slots, hit rate, mean peer-relative alpha, and its current floor. A history of
   how the monthly tuner has moved slots between them. Per screen, its recent candidates
   and how they went. A screen quietly failing should become visible here.

5. CALIBRATION — are the probabilities meaningful
   A reliability diagram per model: stated probability on one axis, realised outcome on
   the other, with the 45-degree line drawn so systematic over- or under-confidence is
   immediately obvious. Brier score and its trend. The same broken out per screen. The
   active lessons the researchers are being given, each with the sample size behind it
   and its expiry date.

6. RUN HEALTH — operations
   Pipeline stages as a timeline with status, duration and row counts, so a stall is
   locatable. Note that the Opus 5 stage is asynchronous: it submits a batch in the
   evening and completes when results return, with a hard 09:00 deadline, so the
   timeline has a waiting state that is normal rather than stuck. Data freshness per
   source. Cost per night and a running annual total against a $100 budget, split by
   model. Cache hit rate. Validator rejection rate per model, which is a hallucination
   measure worth watching independently of returns. The summariser chain in full: each
   link with its health, endpoint, loaded model, latency and share of recent digests,
   plus reconnect and test controls. Concentration alerts (megacap share of recent
   candidates, distinct tickers over 60 days). Recent failures with reasons.

DESIGN CONSTRAINTS

Dense tables that stay readable at 30-plus rows without pagination. Numbers in a
monospaced face, column-aligned. Restrained colour used to carry meaning (verdicts, P&L
direction, alert states, model agreement) rather than decoration. Charts legible at
small sizes since several sit alongside tables. Every screen should survive being opened
at 8am with no memory of last night, so the date and state of the most recent run need
to be evident without hunting for them.
