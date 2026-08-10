# METRICS.md

Every metric the compute layer produces, with the formula, the window, the warm-up, the
null rule and the source columns. One place, in a form a hand computation can be worked
from.

**This document is the draft produced at checkpoint 2.1 and is not yet authored.** Its
content states rules, which `CLAUDE.md` §13 makes authored content. It is here so that
the definitions are on paper before any engine is written, because phase 2's definition
of done is that a known ticker's indicators match a hand-computed reference and a
reference needs a definition. Every entry marked **PROPOSAL** is a reading taken where
no document decides, and each says what the ambiguity is.

**It is also a new corpus document.** `CLAUDE.md` §13 lists what exists and a metric
definition reference is not among them. `SCHEMA.md` is the nearest fit and is about
grain and write ownership rather than arithmetic, and it says its own column lists are
"the load-bearing ones, not exhaustive". Forty formulas would swamp it. The location is
a judgement and is easy to move.

**Read by** C08, C09, C10, C11 and C35, their reference tests, phase 3's backfill and
phase 6's dossier builder.

---

## 1. Conventions that apply to everything below

### 1.1 Which price series

`price_daily` stores `open`, `high`, `low`, `close`, `adj_close` and `volume`. Only
`adj_close` is adjusted by the provider.

**Anything that compares a price across dates uses the adjusted series.** A two for one
split halves the raw close, and a moving average, a 52-week high or a relative strength
computed on the raw series reads that as a fifty percent decline. Nothing errors and
every screen downstream sees a name that has collapsed.

The adjustment factor for a date is `f(d) = adj_close(d) / close(d)`. It applies to every
price on that date, so the adjusted high and low are derivable:

```
H'(d) = high(d)  * f(d)
L'(d) = low(d)   * f(d)
C'(d) = adj_close(d)
```

**Anything that is a level today uses the raw close.** The price in the dossier's
identity group, and the price in `close(d) * volume(d)`.

**Volume is unadjusted and is adjusted the same way.** A split doubles the share count,
so a raw volume compared against a raw fifty-day average spanning the split reads as
double the participation.

```
V'(d) = volume(d) * close(d) / adj_close(d)
```

`volume_vs_50d_avg` divides two such quantities, so any constant normalisation cancels
and the form above is sufficient. It carries a residual: `f` moves on dividends as well
as splits, so a dividend-paying name's adjusted volume drifts by roughly its yield over
the window, which is under one percent over fifty trading dates against the factor of two
a split produces. The residual is stated rather than corrected.

`median_dollar_volume_20d` needs no adjustment at all. Raw close times raw volume is the
dollars that actually changed hands on that date, and both factors are unadjusted, so the
product is correct as stored.

### 1.2 Units

**Every ratio and every distance is stored as a fraction, not as a percentage.** A name
27.7 percent below its 200-day average carries `dist_200dma = -0.277`. An ATR of 2.8
percent of price carries `atr_pct = 0.028`. Presentation multiplies by 100; storage does
not.

One rule rather than one rule per column, because the alternative is a unit that has to
be remembered per field and a risk layer that takes `2 * atr_pct` and is silently a
hundred times wrong.

`adx14` is the single exception and it is not this document's to make: `SCHEMA.md:468`
declares it "an index between 0 and 100" and it is stored as such.

**FINDING.** `atr_pct` is named for a percentage and holds a fraction. The name comes
from `ARCHITECTURE.html` §3 and §7 and names match the architecture [`CLAUDE.md` §6], so
it is not changed here. The unit is stated in this section and nowhere else.

### 1.3 Null

**Insufficient history is null, never a shorter window.** A name with 120 bars has no
200-day average. Substituting the 120 it has produces a number that is not what the
column says it is, on exactly the names whose history is thinnest, which is
disproportionately the small caps the design exists to reach.

**Null propagates.** A metric derived from two others is null when either is null.

**Zero is used only where zero is the fact.** `quarterly_burn_rate` is zero for a company
that generated cash, because it burned nothing. Everything else absent is null
[`CLAUDE.md` §6].

### 1.4 Trading dates and calendar days

**Windows over price are counted in trading dates**, meaning rows present in
`price_daily` for that ticker, because a 200-day average means 200 sessions.

**Windows over sentiment are counted in calendar days**, because `sentiment_daily` has a
row only on days that carried news and counting rows would make a 90-day baseline mean 90
news days, which on a thinly covered name is several years.

Every window is inclusive of the computation date `D` unless the entry says otherwise.

### 1.5 Point in time

Every fundamental input is the most recent `fundamental_snapshot` row for that ticker with
`filing_date_effective <= D` and `filing_date_effective IS NOT NULL`. Never `period_end`
and never the raw `filing_date` [INVARIANT 12, D-46, D-62].

Only `period_type = 'quarterly'` is ingested [`FundamentalsIngestor.cs:198`], so "TTM"
below means the four most recent qualifying quarters and "four quarters earlier" means
the fourth qualifying period back.

**A TTM sum is null unless all four quarters are present and readable.** Summing three and
calling it a year understates every ratio built on it.

### 1.6 Determinism

Two runs of a stage over the same date and config version produce byte-identical output.
No formula below reads a clock, and every ordering that reaches output is explicit
[`CLAUDE.md` §6].

---

## 2. `indicator_daily` — C08 IndicatorEngine

Source: `price_daily` for the ticker, `price_daily` for `SPY.US`, `security` for sector,
`price_daily` for the sector composite's members.

| Column | Window | Null below |
|---|---|---|
| `atr_pct` | 14 plus warm-up | warm-up + 15 bars |
| `adx14` | 14 plus warm-up | warm-up + 29 bars |
| `dist_20dma` | 20 | 20 bars |
| `dist_200dma` | 200 | 200 bars |
| `dist_52w_high` | 252 | 252 bars |
| `dist_52w_high_20d_change` | 252 + 20 | 272 bars |
| `rs_change_21d` | 21 | 22 bars, ticker or SPY |
| `rs_change_63d` | 63 | 64 bars, ticker or SPY |
| `rs_21d_63d_change` | 63 | as its inputs |
| `rs_20d_slope` | 20 | 21 bars |
| `rs_change_vs_sector` | 63 | 64 bars, or a thin sector |
| `volume_vs_50d_avg` | 50 | 50 bars |
| `ma50_200_slope` | 200 + 20 | 220 bars |
| `base_breakout_flag` | `indicator.base_lookback_days` + 1 | that many bars |
| `median_dollar_volume_20d` | 20 | 20 bars |

### `atr_pct`

True range on the adjusted series:

```
TR(d) = max( H'(d) - L'(d),
             abs(H'(d) - C'(d-1)),
             abs(L'(d) - C'(d-1)) )
```

Wilder's smoothing, which is recursive and is not a window aggregate:

```
ATR(s)   = mean of TR over the first 14 dates of the warm-up window
ATR(d)   = (ATR(d-1) * 13 + TR(d)) / 14      for every date after s
atr_pct  = ATR(D) / C'(D)
```

**The warm-up window is `indicator.wilder_warmup_bars` trading dates ending at `D`**,
default 250. `s` is the fourteenth date of that window. Without a fixed warm-up the
recursion's answer depends on how much history the database happens to hold, so two
databases with the same 250 bars and different amounts before them disagree, neither is
wrong, and the reference test cannot be written. With it, the seed is a function of `D`
alone.

250 rather than 14: Wilder's smoothing has a decay of 13/14 per step, so a seed's
influence falls to under one percent after roughly 64 steps and under one part in ten
thousand after 125. A 250-bar warm-up makes the seed choice immaterial to the answer while
keeping it exactly specified, and the universe requires 250 trading dates anyway [D-4].

### `adx14`

Directional movement on the adjusted series:

```
up(d)   = H'(d) - H'(d-1)
down(d) = L'(d-1) - L'(d)

+DM(d) = up(d)   if up(d)   > down(d) and up(d)   > 0, else 0
-DM(d) = down(d) if down(d) > up(d)   and down(d) > 0, else 0
```

Each of `+DM`, `-DM` and `TR` is Wilder-smoothed over 14 with the same warm-up rule as
`atr_pct`, then:

```
+DI = 100 * smoothed(+DM) / smoothed(TR)
-DI = 100 * smoothed(-DM) / smoothed(TR)
DX  = 100 * abs(+DI - -DI) / (+DI + -DI)
adx14 = Wilder smoothing of DX over 14
```

Null when `+DI + -DI` is zero, which is a name that has not moved at all across the
window. Stored 0 to 100 per §1.2.

### `dist_20dma`, `dist_200dma`

```
MA_n(D)    = mean of C' over the last n trading dates including D
dist_ndma  = (C'(D) - MA_n(D)) / MA_n(D)
```

Signed. Negative below the average. `WORKED_EXAMPLE.md:45-51` pins this exactly:
200-day average 58.20, price 42.10, `(42.10 - 58.20) / 58.20`.

`dist_20dma` exists so S5's stabilisation gate reads `dist_20dma > 0` rather than needing
a stored 20-day average. A stored average would be a price in a `real` column, which
INVARIANT 16 has something to say about, and the gate needs only the sign.

### `dist_52w_high`

```
H52(D)        = max of H' over the last 252 trading dates including D
dist_52w_high = (C'(D) - H52(D)) / H52(D)
```

Always at or below zero. The adjusted **high** rather than the adjusted close, because a
52-week high means the high. `H'` is derived per §1.1 since the provider adjusts only the
close.

### `dist_52w_high_20d_change`

```
dist_52w_high(D) - dist_52w_high(D - 20 trading dates)
```

Positive means the name has closed the gap to its own high over the last month. This is
D-11's change over level applied to a distance, and it is one of S2's ranking inputs.

### `rs_change_21d`, `rs_change_63d`

Relative strength against SPY, read from `price_daily` where the whole US bulk feed
already lands it.

```
RS(d)          = C'_ticker(d) / C'_SPY(d)
rs_change_n(D) = RS(D) / RS(D - n trading dates) - 1
```

Equivalently the ticker's n-day total return over SPY's, in ratio form. Null when either
series lacks a bar at either endpoint.

**SPY is read from `price_daily` and is not a universe member.** C02 writes every row the
bulk feed returns with no universe filter [`PriceIngestor.cs:48`], so the series is
present. D-2 puts ETFs out of scope as *candidates*, which is a statement about
`security`, not about what may be read as a benchmark.

### `rs_21d_63d_change`

```
rs_change_21d(D) - rs_change_63d(D)
```

**PROPOSAL.** §05 names this as S2's first ranking input and no document defines it. The
reading taken is acceleration: relative strength over the last month against relative
strength over the last quarter, positive when the recent leg is the stronger one. It is
consistent with S2's stated thesis, "a name whose relative strength is improving, not a
name that is already strong" [§07], and with D-11.

The alternative reading, a change in some 21-to-63 ratio, produces a quantity with no
stated thesis behind it.

### `rs_20d_slope`

```
b    = ordinary least squares slope of RS(d) against day index i = 0..19
       over the last 20 trading dates including D
rs_20d_slope = b / mean(RS over the same 20 dates)
```

Normalised by the mean so the value is scale free and comparable across names, which is
what makes it percentile-able. S5's gate reads only the sign, which the normalisation
does not change since the mean of a price ratio is positive.

### `rs_change_vs_sector`

The same form as `rs_change_63d` with the sector composite in place of SPY.

**The sector composite is an equal-weighted daily-rebalanced index of that sector's own
universe members**, not a sector ETF.

```
members(d, s) = security rows with sector = s and is_active,
                having a price on both d-1 and d
r(d, s)       = mean over members of ( C'_m(d) / C'_m(d-1) - 1 )
Idx(d, s)     = Idx(d-1, s) * (1 + r(d, s)),  Idx seeded at 1 at the window start
```

**Why the composite and not XLI.** The sector SPDRs are S&P 500 sector slices and this
universe is 2,840 names mostly outside that index. Measuring a $500M industrial against
XLI measures it against Honeywell and Caterpillar, which imports a large-cap benchmark
into a small-cap universe and tilts the trend screen by regime. That is the megacap drift
`ARCHITECTURE.html` §20 says this design exists to prevent, arriving through a column
nobody would look at. Needing no ETF and no mapping is true and is not the reason: a
later session reading only that would reasonably conclude that adding the mapping is an
improvement.

Null when the ticker's `security.sector` is null, and null when the sector has fewer than
`market.sector_composite_min_members` members on any date in the window.

**PROPOSAL, the horizon.** §07 describes the field as "relative strength against its own
sector" and gives the example "down 30 percent while the sector is down 28 is a sector
story", which is a comparison over a developed decline. 63 trading dates is taken, matching
`rs_change_63d`. No document states it.

### `volume_vs_50d_avg`

```
V'(D) / mean of V' over the last 50 trading dates including D
```

One means participation exactly at its own average. Adjusted per §1.1, and the residual
dividend drift is stated there.

### `ma50_200_slope`

```
r(d)           = MA_50(d) / MA_200(d)
b              = OLS slope of r against day index over the last 20 trading dates
ma50_200_slope = b / mean(r over the same 20 dates)
```

**PROPOSAL.** The name gives two windows and the word slope and no document defines the
combination. The reading taken is the slope of the 50 over 200 ratio, positive when the
faster average is pulling away from the slower one, which is the trend confirmation S2
ranks on. The alternative readings, the slope of the 50 minus the slope of the 200, or the
level of the ratio, are respectively a difference of two quantities in different units and
a level rather than a change.

### `base_breakout_flag`

**PROPOSAL, and the least constrained definition in this document.** "Base breakout"
appears in `ARCHITECTURE.html` §05 as an S2 ranking input and in §07's S2 dossier block as
"breakout level, distance from the base", and nowhere with a definition.

Two conditions, both over the `N = indicator.base_lookback_days` trading dates ending at
`D - 1`, default 60:

```
high_N  = max of H' over that window
low_N   = min of L' over that window
base    = (high_N - low_N) / low_N <= indicator.base_max_range_pct
breakout = C'(D) > high_N

base_breakout_flag = base AND breakout
```

The range condition is what makes it a base rather than a trend. Without it a name in a
steady advance sets a new window high most days and the flag is on permanently, which
carries no information and would put every strong trend into S2 twice.

`indicator.base_max_range_pct` default 0.25.

### `median_dollar_volume_20d`

```
median over the last 20 trading dates including D of close(d) * volume(d)
```

Raw close and raw volume, per §1.1. `numeric`, because a dollar volume is money and
INVARIANT 16 does not bend for storage size [`SCHEMA.md:203`].

**This is the same number C01 computes weekly to apply D-4's liquidity floor**
[`UniverseBuilder.LiquidAsync`, `:169`, using
`percentile_cont(0.5) WITHIN GROUP (ORDER BY close * volume)` over the last 20 rows per
ticker]. One SQL definition, extracted to a shared constant, read by both. Two
definitions of one number in two components at two cadences is how the universe and the
dossier come to disagree about whether a name is liquid.

C01's existing statement also filters `close IS NOT NULL AND volume IS NOT NULL` inside
the window, so a ticker with a gap takes its median over fewer than 20 points rather than
being nulled. That is the established behaviour and the shared constant carries it
unchanged, which is a deliberate exception to §1.3: the alternative is that one absent bar
in twenty removes a name from the universe.

---

## 3. `valuation_daily` — C09 ValuationEngine

Source: `fundamental_snapshot` read per §1.5, `price_daily` for price, and its own prior
rows only where an entry says so.

```
market_cap(D) = shares_outstanding(latest qualifying period) * close(D)
```

Raw close, because a market capitalisation is a level today. `numeric` throughout the
monetary path.

### `fcf_yield`

```
FCF       = TTM cash_from_operating - TTM capital_expenditures
fcf_yield = FCF / market_cap(D)
```

**Capital expenditure on its own line, not total investing cash flow** [D-79]. Cash from
operating plus cash from investing reads an acquisition as capital expenditure and an
asset sale as free cash flow, and both errors land hardest on exactly the names S1 exists
to find: an acquisitive small cap looks like it spends everything it earns, and one
selling a division looks like it generates cash it does not. `capital_expenditures`
arrives in `0004` with C03's parse widened to populate it from `Cash_Flow`'s
`capitalExpenditures`.

**OPEN, and 2.7 closes it: the sign convention is not known from anything in this
repository.** Providers differ on whether capital expenditure is reported as a negative
cash outflow or as a positive magnitude, and the phase P transcripts at
`docs/evidence/phase-P/` never printed the field, so there is no evidence here either
way. The subtraction above assumes a positive magnitude. **Before the formula is fixed,
2.7 reads real rows out of `fundamental_snapshot` and confirms the sign against
`cash_from_investing` on a name with material capex.** Getting it backwards doubles free
cash flow rather than halving it and nothing downstream errors.

Null when either TTM sum is incomplete or `market_cap` is null.

### `ev_ebit`

```
net_debt(D) = coalesce( net_debt,
                        short_long_term_debt_total
                          - (cash_and_equivalents + short_term_investments) )
EV          = market_cap(D) + net_debt(D)
ev_ebit     = EV / TTM ebit
```

Null when TTM `ebit` is at or below zero. A negative EV/EBIT is not a cheap company and
ranking it alongside positive ones puts loss-makers at the top of a value screen.

### `ev_ebit_vs_own_5y`

```
own(D)            = median of ev_ebit evaluated at each month-end trading date
                    in the 5 years before D
ev_ebit_vs_own_5y = ev_ebit(D) / own(D) - 1
```

Negative means cheaper than its own five-year history, which is the direction S1 wants.

**Computed rather than read back.** Each historical point is evaluated from
`fundamental_snapshot` and `price_daily` as they stood at that date, in the same
statement. C09 could instead read its own prior `valuation_daily` rows, which
`DeclaredAccess.CanRead` permits since a stage may read what it writes, and that would be
cheaper. It would also make the column a function of what previous runs happened to
write, so a backfill would have to run in date order to be correct and a gap in history
would propagate silently. Computed, it is a pure function of `D` and phase 3 can partition
however it likes.

**Month-end sampling rather than daily.** 60 points over five years rather than 1,250, for
2,840 names nightly. The median of a five-year distribution is not moved materially by the
sampling frequency, and the definition has to be cheap enough to run every night.

Null below `valuation.own_history_min_points` sampled points, default 24, which is two
years. On a cold database that is every name until phase 3 has backfilled.

### `roic`

```
tax_rate = income_tax_expense / income_before_tax, clamped to [0, 0.5]
NOPAT    = TTM ebit * (1 - tax_rate)
IC       = total_assets - total_current_liabilities - goodwill - intangible_assets
roic     = NOPAT / IC
```

Null when `income_before_tax` is at or below zero, when `IC` is at or below zero, or when
any input is absent.

**PROPOSAL, invested capital excludes goodwill and intangibles.** For a serial acquirer,
goodwill is the price paid for past acquisitions, so including it makes ROIC report what
was paid rather than what the operating assets earn, and a company that overpaid looks
worse at the same operating performance. Excluding it measures the return on the assets
actually deployed. Both are standard and the choice is a rule, so it is flagged. The
inputs for either are present.

The clamp exists because a quarter with a one-off tax item produces effective rates
outside any real range, and an unclamped rate can flip NOPAT's sign.

### `roic_4q_change`

```
roic(latest qualifying period) - roic(fourth qualifying period back)
```

A difference of fractions, so 180 basis points is `0.018`.
`WORKED_EXAMPLE.md:118` renders this as `droic4q +180bp` in the dossier.

Null when either endpoint is null. This is S1's ranking input rather than the level, per
D-11.

### `gross_margin_4q_change`

```
gm(p) = coalesce(gross_profit, total_revenue - cost_of_revenue) / total_revenue
gross_margin_4q_change = gm(latest) - gm(fourth back)
```

A difference of fractions. Null when `total_revenue` is at or below zero at either
endpoint.

### `net_debt_ebitda`

```
net_debt(D) / TTM ebitda
```

`net_debt(D)` as in `ev_ebit`. Null when TTM `ebitda` is at or below zero, since leverage
against negative earnings is not a multiple. S1 ranks on the inverse, which is a screen
config concern rather than a column.

### `accruals`

```
avg_assets = mean of total_assets over the latest and fourth-back periods
accruals   = (TTM net_income - TTM cash_from_operating) / avg_assets
```

Sloan's accrual ratio. Higher is worse, earnings not backed by cash, which is why S1 ranks
on the inverse and why §07's S1 rubric disqualifies the top quintile outright.

Null when `avg_assets` is at or below zero or either TTM sum is incomplete.

### `share_count_change`

```
shares_outstanding(latest) / shares_outstanding(fourth back) - 1
```

A fraction. Positive is dilution, which is why §07's S1 rubric disqualifies above five
percent a year.

### `revenue_growth_4q_trend`

```
g(p) = total_revenue(p) / total_revenue(p - 4 quarters) - 1
revenue_growth_4q_trend = OLS slope of g over the latest four qualifying periods,
                          against quarter index
```

**PROPOSAL.** The name says trend, not change. §07 explains the field as distinguishing
"improving margins on shrinking revenue" from "improving margins on growing revenue",
which is about the direction growth is moving rather than its level. The slope of the last
four year-over-year growth rates is that. It needs eight quarters of revenue.

The simpler reading, `g(latest) - g(fourth back)`, is a two-point estimate of the same
thing and is far noisier on quarterly data.

Null below eight qualifying periods.

### `cash_on_hand`

```
coalesce(cash_and_equivalents, 0) + coalesce(short_term_investments, 0)
```

falling back to `cash` when both are absent, and null when all three are absent.

**The coalesce to zero here is deliberate and is the one place in this document that
does it.** A company reporting cash and equivalents but no short-term investments line has
no short-term investments, which is zero rather than unknown, and treating it as unknown
would null the field for most of the universe. The fallback to `cash` covers the shape
where the provider sends the aggregate and not the parts. `numeric`.

Sent in the dossier for small and mid buckets only [§07].

### `quarterly_burn_rate`

```
q = cash_from_operating(latest) + cash_from_investing(latest)
quarterly_burn_rate = -q  when q < 0
                    =  0  when q >= 0
```

`numeric`. Zero rather than null when the company generated cash, because a company that
generates cash burns nothing and that is a fact rather than an absence [§1.3]. Null only
when either input is absent.

Latest quarter rather than TTM, because §07's purpose is runway against `cash_on_hand`
and runway is computed from the current rate.

### `last_two_earnings_surprises`

**Null through phase 2.** `fundamental_snapshot` carries no EPS actual and no estimate,
and `events` carries only `event_type`, `event_date` and `announced_date`. Nothing in any
document says where a surprise comes from. Recorded as a carried obligation rather than
proxied, because the field is bolded in §07 as one of the five that can flip a verdict and
a proxy would be worse than an absence.

---

## 4. `sentiment_derived_daily` — C35 SentimentEngine

Source: `sentiment_daily`, which holds `ticker`, `date`, `article_count` and
`sentiment_score`, and `security` for the universe iterated.

### 4.1 What an absent day means, which is different for the two source columns

`sentiment_daily` has a row only on days that carried news. Phase P measured 4, 7, 17, 34,
63 and 122 days with a row out of 180 across six small caps, and found days with a non-zero
count identical to days with a row on all seven names, so no empty rows are written
[`PROGRESS.md`, probe findings].

**An absent day is zero articles.** C04 covers the whole universe every night with no
pre-selection [§03], so within the ingested window an absent day means no news, not no
lookup. Zero-filling `article_count` is therefore reading the fact, not defaulting an
unknown.

**An absent day has no sentiment score.** There is no tone where there are no articles.
Zero-filling `sentiment_score` would say the coverage was exactly neutral, which is a
different claim from the coverage not existing, and it would pull every thinly covered
name's mean toward zero in proportion to how thinly covered it is. That is a size proxy
arriving through the back door and it is what D-12 exists to keep out.

So `article_count` zero-fills across the window and `sentiment_score` does not. These are
the same rule from `CLAUDE.md` §6 applied to two columns whose absences mean different
things.

**Before the ingest reached a ticker, absence means neither.** Guarded by requiring at
least `sentiment.min_baseline_days` distinct dates with a row inside the window before any
of the three metrics is computed. Default 20.

### `article_count_z_own_90d`

```
window   = the 90 calendar days ending D - 1, zero-filled
mu       = mean of article_count over the window
sigma    = population standard deviation over the window
article_count_z_own_90d = (article_count(D) - mu) / sigma
```

The baseline excludes `D` itself, so today's spike is measured against a history that does
not contain it. Including it damps the very signal the metric exists to catch, by a factor
of roughly 1/90 in the mean and more in the deviation.

**Against the ticker's own history and never cross-sectionally** [D-12]. A cross-sectional
article count measures analyst coverage, which is a size proxy; against its own baseline it
measures change in attention, which is the signal, and it is structurally anti-megacap.

Null when `sigma` is zero, which is a ticker with a flat count across the whole window and
therefore no scale to express a deviation in, and null below
`sentiment.min_baseline_days`.

90 is a constant rather than a config key, because it is named in the column
[`FlowEngine.cs:33` is the precedent: a tunable 90 beside a column called
`insider_net_90d_usd` is a second place for the number to live].

### `sentiment_delta_7v30`

```
mean of sentiment_score over the 7 calendar days ending D, rows only
  minus
mean of sentiment_score over the 30 calendar days ending D, rows only
```

Not zero-filled, per §4.1. Null when either window contains no row.

`WORKED_EXAMPLE.md:43` pins the shape: "Sentiment 7-day against 30-day, +0.05, at or
above 0, pass". S5's gate tests the sign; S3 ranks the value.

### `sentiment_7d_level`

```
mean of sentiment_score over the 7 calendar days ending D, rows only
```

Null when the window contains no row. S3's third ranking input, and the level against
which the delta is the change.

---

## 5. `market_context_daily` — C10 MarketContextEngine

One row per trading date. Source: `price_daily`, `security`, `indicator_daily`.

### `breadth`

```
numerator   = count of active security rows whose dist_200dma(D) > 0
denominator = count of active security rows with a non-null dist_200dma(D)
breadth     = numerator / denominator
```

A fraction of the market [`SCHEMA.md:490`]. Reads `indicator_daily`, which is why C10 runs
after C08 within the 18:05 slot.

The denominator excludes names with too little history rather than counting them as below
their average. Counting them would put every recent listing on the bearish side of the
measure permanently.

`market.breadth_ma_days` is 200 and is the same window `dist_200dma` uses. It is a key
rather than a constant because breadth's window is not named in any column.

### `sector_relative_strength`

A jsonb object, one entry per sector present in the active universe with at least
`market.sector_composite_min_members` members, value being that sector composite's 63
trading-date return minus the universe composite's over the same window.

```
universe composite = the same equal-weighted daily-rebalanced construction as §2's
                     sector composite, over every active member
```

**Keys are written in ordinal sort order.** A jsonb object built from a dictionary carries
that dictionary's enumeration order, which is unspecified and can differ between runs of
the same binary, and the column reaches the cached prefix where a byte difference breaks
the cache and roughly triples the input bill silently [INVARIANT 6, `CLAUDE.md` §6].

Values are fractions per §1.2, rounded to a fixed number of decimal places so the rendering
is stable.

### `regime_label`

**BLOCKED.** No document enumerates the labels or the rule that assigns one, and the field
is read by the cached prefix [§07], stored on every attribution row [`SCHEMA.md:268`],
shown on screen U1, and named as what makes S1's and S5's size tilt "regime dependent"
[§05].

Proposal offered for authoring: three labels from two facts C10 already computes.

```
risk-on  when breadth >= market.regime_breadth_high
             and the universe composite is above its own 200-day average
risk-off when breadth <= market.regime_breadth_low
             and the universe composite is below its own 200-day average
mixed    otherwise
```

Defaults 0.60 and 0.40. The virtue is that it adds no input and reuses numbers already
computed; the vice is that three labels may be too coarse for what "regime dependent"
implies in §05, which is a question for whoever authors it.

### `vix`

**Null through phase 2.** The bulk end-of-day US feed carries equities and the index is not
among them, and no other ingest path exists. Written null and recorded, rather than
proxied by a realised-volatility substitute that would carry the column's name without its
meaning.

---

## 6. Percentiles — C11 PercentileEngine

One `_pctile` per ranked metric, `real`, written onto the source table by an `Update` that
touches no other column.

### 6.1 The cell

```
cell = (security.size_bucket, security.sector)
```

Percentiles are computed within size bucket by sector cells, falling back to size bucket
alone when a cell has fewer than `percentile.cell_min_members` members [D-10, default 15].
A $1B name is scored against its peers, not against megacaps.

### 6.2 The population, and why the fallback counts per metric

**The population ranked for a metric is the cell's members that carry a non-null value for
that metric on `D`.** Null is not a value and is not ranked.

**The fifteen-member test counts that population, not the cell.** A cell with twenty
members of which five carry a value for one metric produces, if the test counts the cell, a
percentile computed over five while the fallback believes it had twenty. The number that
comes out looks exactly like a percentile over twenty and there is nothing downstream that
could tell the difference. D-10's fifteen is a test on the population being ranked, so it
is evaluated per metric.

The consequence is that one row can be ranked inside its sector cell for a metric with
broad coverage and inside its size bucket for a metric with thin coverage, on the same
night. That is correct and it is why the fallback cannot be recorded once per row.

### 6.3 The function

```
pctile = PERCENT_RANK() OVER (PARTITION BY <cell> ORDER BY <metric>) * 100
```

`PERCENT_RANK` rather than `ROW_NUMBER` over a count, because it gives tied values the
same rank. That removes the tie-break entirely, and with it the determinism hazard a
tie-break introduces: any ordering appended to break ties would have to be explicit and
stable or two runs of the same stage would disagree [`CLAUDE.md` §6].

Scaled 0 to 100 and stored as `real`. `WORKED_EXAMPLE.md:35-36` prints 84 and 12 and calls
them top and bottom quintile, so the scale is decided and top quintile is at or above 80.

**Ascending, always.** Higher raw value means higher percentile, for every metric. Where a
screen wants the inverse, as S1 does for `net_debt_ebitda`, `accruals` and
`share_count_change`, the direction belongs in that screen's `screens.<id>.metrics` config
row. The `_inv` suffixes in §05's Ranks-on column name a direction, not a column, and no
`_inv` column exists or is meant to.

### 6.4 The fallbacks, in order

1. Size bucket by sector, when the non-null population is at or above
   `percentile.cell_min_members`.
2. Size bucket alone, when it is not. A null `security.sector` goes straight here and does
   not form a cell of its own [`UniverseBuilder.cs:283-285` writes null rather than a guess
   on an HTTP failure and its comment already anticipates this].
3. **Null**, when the size bucket alone also has fewer than the minimum non-null members.
   Never ranked across buckets. Ranking a $1B name against megacaps is the exact thing
   D-10 exists to prevent, and a fallback that does it is worse than no value.

**A null `size_bucket` cannot arise.** A universe member cleared D-4's $300M market cap
floor, so it has a market cap, so C01 assigned it a bucket from `universe.bucket_large_floor`
and `universe.bucket_mid_floor` [`UniverseBuilder.cs:50-51`]. Stated rather than left out,
because the rule above closes the sector hole and a reader cannot otherwise tell whether
the bucket hole was reasoned about or missed. A test asserts `size_bucket` is non-null for
every active row, which is what makes the claim fail if C01 ever changes.

### 6.5 What is not percentiled

`base_breakout_flag`, because a percentile over a two-valued column collapses to two values
and carries nothing the boolean does not. How a boolean enters a screen score is phase 4's
to author.

`cash_on_hand` and `quarterly_burn_rate`, which are levels in dollars sent to the dossier
for magnitude, and `last_two_earnings_surprises`, which is an array.

Everything else with a named reader is percentiled, including `median_dollar_volume_20d`,
because §07 says every metric in the fixed core arrives twice and the core's identity group
names it.

### 6.6 Reporting the fallback

How many cells fell back, per metric, and how many rows carry a null percentile because
their bucket was thin too, go into `StageResult.Detail` and the run log, following
`FlowEngine.UnknownCountsAsync` [`FlowEngine.cs:181-219`]. A metric that is null for a
reason nobody counted is a metric nobody notices going null.

Not stored per row, because the fallback is per metric and a row spans metrics.

### 6.7 No narrowing

C11 ranks every active universe member with a row in the source table. It does not take a
top slice, apply a floor, or drop a bucket. Absolute filters live only in the universe
definition [INVARIANT 1, D-5], and a percentile engine that skipped part of the universe
would be one, applied after ranking at ingest had already decided what could be discovered.

---

## 7. Config keys this document introduces

Every one is a value that could plausibly be tuned and none is a literal at a call site
[`CLAUDE.md` §8]. Windows named inside a column stay constants.

| Key | Default | Consumer | Why it is a key |
|---|---|---|---|
| `indicator.wilder_warmup_bars` | 250 | IndicatorEngine | The seed date for ATR and ADX, without which the recursion is a function of stored history rather than of `D` |
| `indicator.base_lookback_days` | 60 | IndicatorEngine | The base window for `base_breakout_flag` |
| `indicator.base_max_range_pct` | 0.25 | IndicatorEngine | What makes a window a base rather than a trend |
| `valuation.own_history_min_points` | 24 | ValuationEngine | The floor below which `ev_ebit_vs_own_5y` is null rather than computed over less |
| `market.breadth_ma_days` | 200 | MarketContextEngine | Breadth's window, not named in any column |
| `market.sector_composite_min_members` | 5 | IndicatorEngine, MarketContextEngine | Below which a sector composite is one name's noise |
| `market.regime_breadth_high` | 0.60 | MarketContextEngine | Blocked with the regime label |
| `market.regime_breadth_low` | 0.40 | MarketContextEngine | Blocked with the regime label |
| `sentiment.min_baseline_days` | 20 | SentimentEngine | Below which an absent day cannot be read as zero, because the ticker may never have been ingested |

`percentile.cell_min_members` already exists at 15 and is `unverified` in
`CONFIG_REFERENCE.md`. Phase 2 is where it acquires a consumer and becomes verified.

---

## 8. What this document does not decide

Screen scores, floors, weights and the direction each screen reads a metric in. Those are
`screens.*` config rows and phase 4 authors them [D-6, and screens never read each other,
INVARIANT 2].

The screen-specific dossier blocks. §07 names fields with no column behind them, breakout
level and distance from the base for S2, peak-to-trough decline and days since peak for
S5. Phase 2 builds §05's ranking metrics and §07's fixed core; the screen blocks are the
dossier builder's, in phase 6.

Anything about `security` being point in time. `size_bucket` and `market_cap` are one row
per ticker, so a backfill of a 2021 date reads 2026's bucket for every name and §6's cells
are correct live and not as-of. That is carried from phase 1 to phase 3 and is not phase
2's to close.
