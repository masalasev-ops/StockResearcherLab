# UI design, v1

    Target:     Claude Design
    Issued:     2026-08-05
    Status:     SUPERSEDED BY design-ui-v2.md
    Produced:   an initial pass at the six screens

Superseded within the same session. The digest design changed underneath it: this
version tells the designer that a missing local model leaves digests simply absent
while the run completes, which stopped being true once the provider chain and the
hard gate were added [D-25, D-26]. It also predates the second research portfolio,
the probability output and the counter-argument field.

Archived because it was issued and acted on. Never edited.

---

Design the UI for a personal paper-trading research lab. Single user, desktop-first,
runs locally. Blazor front end calling a C# API. Data-dense and utilitarian, closer
to a Bloomberg terminal or a good observability dashboard than a consumer fintech
app. No marketing surfaces, no onboarding, no settings-heavy chrome.

WHAT THE SYSTEM DOES
Every evening after the US close it screens the entire US stock market (~2,000 names
above $300M market cap and $5 a share) through five independent screens: quality at a
fair price, trend, sentiment inflection, insider and short-interest flow, and mean
reversion. Each screen contributes up to 8 names under a size quota of 2 large / 3
mid / 3 small, deduplicated into roughly 30 candidates. An AI researcher then judges
each candidate independently, returning BUY or PASS with a conviction score of 1 to 5
and a thesis under 60 words. A risk layer sizes and caps whatever it approves, and at
most 2 new positions are entered per day against 8 open maximum.

Three portfolios run in parallel off the identical candidate set so the researcher can
be measured: A is the researcher, B is the screens alone with no AI, C is random
selection. All three use identical sizing and risk rules, and their market exposure is
matched, so any difference between them is selection skill.

SCREENS TO DESIGN

1. TONIGHT'S RUN — the default landing view.
   Tonight's ~30 candidates as a dense table: ticker, company, size bucket, sector,
   which of the five screens surfaced it (a name can be surfaced by more than one),
   its score within each screen, the researcher's verdict and conviction, and whether
   it became an order. Verdicts are mostly PASS, so BUY rows need to be findable at a
   glance. Show the funnel for the night: candidates, BUYs, orders. Show the market
   regime label and the run's total AI cost.

2. CANDIDATE DETAIL — one name, opened up.
   Every fundamental, technical, valuation, sentiment and flow metric, each shown as a
   raw value alongside its percentile within its size-and-sector peer group, since the
   percentile is what actually matters. The exact dossier text the model was given.
   The model's verdict, conviction, thesis, suggested stop and target. A price chart
   with the 20 and 200 day averages. Once matured, the 5, 21 and 63 day forward
   returns shown three ways: raw, against SPY, and against its size-and-sector peers.

3. PORTFOLIOS — the three compared.
   Equity curves for A, B and C on one chart with SPY as a reference line. Per
   portfolio: open positions with entry, current price, unrealised P&L, stop distance,
   days held. Summary stats: total return, hit rate, average win, average loss, max
   drawdown, Sharpe. Current exposure broken out by size bucket and by sector, with
   the caps drawn in so a portfolio approaching one is obvious. Closed trade history
   with exit reason (stop, target, time stop, thesis change).

4. SCREENS — where the tuning is watched.
   The five screens as cards: current slot allocation out of 40, how often each fills
   its 8 slots, hit rate, mean alpha, and its current floor. A history of how the
   monthly tuner has moved slots between them over time. Per screen, its recent
   candidates and how they went. This is where a screen quietly failing should become
   visible.

5. CALIBRATION — is conviction meaningful.
   Realised hit rate and mean forward return bucketed by conviction 1 through 5. If
   conviction is informative the bars step upward, and the whole point of the view is
   that a flat profile is immediately obvious. Break the same picture out per screen.
   Show the current active lessons the researcher is being given, each with the sample
   size behind it and its expiry date.

6. RUN HEALTH — operations.
   Pipeline stages as a timeline with status, duration and row counts, so a stall is
   locatable. Data freshness per source. AI cost per night with a running annual total
   against a $100 budget. Cache hit rate. Concentration alerts, specifically the
   megacap share of recent candidates and the distinct ticker count over 60 days.
   Recent failures with their reasons.

DESIGN CONSTRAINTS
Dense tables that stay readable at 30-plus rows without pagination. Numbers in a
monospaced face and column-aligned. Restrained colour, used to carry meaning
(verdicts, P&L direction, alert states) rather than decoration. Both light and dark.
Charts should be legible small, since several appear alongside tables. Every screen
should survive being opened at 8am with no memory of last night, so state and time
context need to be evident without hunting.
