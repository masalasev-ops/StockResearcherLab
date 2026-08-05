# VALIDITY.md

What this lab claims, what would falsify each claim, and what it cannot test.

This document is written before any data exists, and that is the point. Once numbers
arrive, every threshold in here becomes negotiable in the direction that makes the
result look better, and the only defence against that is having written the
thresholds down first [CLAUDE.md §11].

**Nothing in here is a measurement.** Every figure is a design estimate. When real
numbers arrive they go in `PROGRESS.md`, not here.

---

## 1. What the lab claims

### Primary claim

**An AI researcher applying a rubric to precomputed evidence selects better than the
screens that surfaced the candidates.**

Operationally: `Research: Opus 5` outperforms `Screens` on risk-adjusted return over
a matched exposure period, and its BUY decisions predict forward returns better than
chance within the candidate set.

Everything else in this system exists to make that question answerable. The trading
is the measuring instrument, not the objective.

### Secondary claims

**S-1.** The judgment has to come from a frontier model. `Research: Opus 5` beats
`Research: V4 Pro` by enough to justify nine times the cost.

**S-2.** Ranking within a screen carries information. `Screens` beats `Random`.

**S-3.** The generator beats plain market exposure. `Random` beats SPY.

**S-4.** Stated probabilities are calibrated. A reliability diagram tracks the
45-degree line and the Brier score beats a constant-rate baseline.

**S-5.** The learning loops improve selection. Screen slot allocation after a year of
tuning outperforms the uniform allocation it started from, evaluated on held-out
attribution.

---

## 2. What would falsify each

| Claim | Falsified by |
|---|---|
| Primary | `Research: Opus 5` and `Screens` indistinguishable after the sample size in §3, or BUY and PASS candidates showing no separation in peer-relative forward return |
| S-1 | The two research portfolios indistinguishable. This is a real possibility and the honest outcome is to retire the expensive one |
| S-2 | `Screens` and `Random` indistinguishable, meaning the membership test does all the work and the ordering is noise |
| S-3 | `Random` at or below SPY, which would mean the universe filters and screens together destroy value. Nothing downstream matters if this happens |
| S-4 | A flat reliability profile, or a Brier score no better than always stating the base rate |
| S-5 | Tuned allocation performing no better than uniform on held-out data |

**A falsified primary claim is a result, not a failure.** The system was built to
find out. The failure mode is deciding, after seeing a null result, that the test was
wrong.

---

## 3. Statistical power, and the uncomfortable part

The trade-level comparison is badly underpowered and will stay that way for years.

At two entries a day maximum and realistic abstention, each portfolio produces
roughly 125 to 250 closed trades a year. Distinguishing a 55 percent hit rate from a
45 percent one at conventional significance needs on the order of 400 observations
per arm. **That is two to three years before the headline portfolio comparison says
anything.**

This is why the attribution table exists and why it records every candidate rather
than every trade.

| Comparison | Observations per year | Time to a usable answer |
|---|---|---|
| Portfolio equity curves | ~125-250 trades each | 2-3 years |
| BUY against PASS on forward returns | ~7,000 candidates | 1 year |
| Per screen | ~1,400 candidates | 1 year |
| Calibration by probability bucket | ~7,000 proposals per model | 6-9 months |
| Validator rejection rate per model | ~7,000 proposals per model | 4-6 weeks |

**Read the candidate-level evidence first and the equity curves last.** The equity
curves are what the project looks like; the attribution table is what it knows. Any
conclusion drawn from portfolio returns inside the first two years is drawn from
noise, however convincing the chart.

The rejection-rate comparison arriving in weeks is worth noting: it is the earliest
signal that distinguishes the two models, and it measures something real, namely how
often each invents a figure it was not given.

---

## 4. Pre-registered thresholds

Committed before data exists. Changing any of these after seeing numbers requires a
decision entry stating what was seen and why the change is not result-shopping.

- **Primary claim considered supported** when peer-relative forward return at 21 days
  separates BUY from PASS candidates by at least 1.5 percentage points, with at least
  1,000 observations on each side, and the same sign holds at 5 and 63 days.
- **Primary claim considered falsified** when that separation is under 0.3 percentage
  points with the same sample.
- **The band between those is inconclusive** and stays inconclusive. It does not
  become supported by adding horizons, splitting by regime, or excluding a bad month.
- **S-1 considered settled** when the two research portfolios' BUY sets diverge on at
  least 20 percent of candidates and the peer-relative outcome difference exceeds 1
  percentage point at 21 days over at least 1,000 paired observations.
- **S-4 considered supported** when the Brier score beats the base-rate baseline by at
  least 5 percent over at least 2,000 proposals.
- **Minimum evaluation period** is 12 months regardless of what the numbers do
  earlier. Nothing is concluded from a quarter.

---

## 5. What the lab cannot test

Stated plainly so no result is over-read.

**Whether any of this works with real money.** Fills are modelled at the next open
with an assumed slippage function. That function is a guess. Nothing here measures
market impact, partial fills, or the difference between a paper stop and a real one.

**Whether it generalises beyond the observed regime.** Five years of backfill plus a
year of live running is one macro environment with one interest rate path. A screen
that works here may be a bet on that environment.

**Whether the model would still behave this way later.** Opus 5 and V4 Pro will both
be deprecated during the life of this lab. A model swap breaks comparability with
everything before it, and there is no way around that.

**Whether the researcher is reasoning or pattern-matching.** The lab measures
outcomes, not mechanism. A model that has memorised which 2023 setups worked would
score well on backfilled evidence for reasons that will not persist.

**Anything about taxes, borrow, dividends in kind, or corporate action handling
beyond splits.**

**Whether a different candidate generator would do better.** The researcher is only
ever judged on candidates these five screens surfaced. If the screens miss a whole
category of opportunity, nothing in this design will reveal it.

---

## 6. Known threats to validity

| Threat | Mechanism | Mitigation |
|---|---|---|
| Lookahead on fundamentals | Reading a value before its filing date makes quality screens look excellent in backfill | INVARIANT 12, plus a test |
| Survivorship in backfill | Building the historical universe from names listed today deletes the losers | D-48, reconstruct per date including delisted |
| Survivorship in attribution | Dropping acquired or delisted candidates removes both tails | Explicit handling rules in the filler |
| Multiple comparisons | Four portfolios, five screens, three horizons, three benchmarks is a large surface to find something in | §4 pre-registration; primary claim named in advance |
| Benchmark mismatch | Measuring small caps against SPY makes every small-cap candidate look bad | D-42, INVARIANT 5, learning loops read peer-relative |
| Model training contamination | Both models have read about the backfill period | Backfill is never used to evaluate the researcher, only the screens |
| Tuner overfitting | Slot reallocation chasing whatever worked recently | Shrinkage 0.8/0.2, floor of 4 slots, cap of 12 |
| Screens tuned on returns before judgment | Optimises toward whatever captured beta in the window | CLAUDE.md §11 prohibition |
| Config drift splitting history | A screen definition change makes two halves incomparable | `config_version` on every attribution row |
| Digest heterogeneity | Two different summarisers producing different evidence | `digest_provider` recorded, rotation gives a paired sample |
| Regime confounding | One drawdown in the window carries all the stress-test weight | Stated as a limit rather than mitigated |

---

## 7. What a negative result should produce

If the primary claim is falsified, the useful output is not "AI does not work". It is
a specific answer to which layer failed, and the system is instrumented to say which.

- Candidates went nowhere → the screens are the problem, not the researcher
- Candidates moved but BUY and PASS did not separate → the researcher is the problem
- BUY and PASS separated but the portfolio did not → the risk layer or execution is
  eating the edge, and nothing in the learning loops will find that
- Probabilities are uncalibrated but the direction is right → the rubric and the
  evidence are fine and the scale is wrong

Those are four different projects to fix and the attribution table distinguishes
them. That is the actual value of the design, independent of whether the primary
claim survives.
