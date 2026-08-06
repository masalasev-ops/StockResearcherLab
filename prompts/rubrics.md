# rubrics.md

The five screen rubrics. These go into the cached prefix, byte-identical across
every call within a night.

These are product, not documentation. They are the place the strategy actually
lives: everything else in the system decides what the model sees, and these decide
what it does with it. Change them the way you would change code, with a version and
a reason, because a change here splits accumulated history into halves that cannot
be pooled.

Prefix tokens cost roughly a seventh of candidate-block tokens, so explanation
belongs here and never in the block.

---

## Shared instruction, applied to every candidate

You are judging one company for a possible long paper trade over a horizon of weeks
to months. You will be told which screen surfaced it. Apply that screen's rubric.

Every number you need has been computed for you. Do not calculate ratios, do not
estimate figures that are absent, and do not refer to anything not present in the
block below. If a figure you would want is missing, say so in the thesis rather than
supplying it from memory.

Each metric appears twice: the raw value, and its percentile within that company's
size-and-sector peer group. The percentile is what makes it comparable. The raw value
is what tells you the magnitude. Use both.

Two principles apply to all five screens.

**Change beats level.** A company at 12 percent return on capital rising from 8 is
more interesting than one flat at 15. Levels favour incumbents permanently; changes
are earned.

**Cause quality dominates magnitude wherever a digest is present.** A coverage spike
of six standard deviations driven by stock promotion is worth nothing. One of two and
a half driven by a named contract win is real.

### What to return

- `verdict`: BUY or PASS
- `p_target_before_stop`: your probability, 0 to 100, that the price reaches your
  stated target before it reaches your stated stop
- `thesis`: 60 words maximum
- `counter_argument`: 20 words maximum, the strongest case against your own verdict
- `primary_driver`: the single field that most moved your judgement
- `stop_pct`, `target_pct`, `horizon_days`

### The bar

Your stop and target imply a breakeven probability: `stop / (stop + target)`. A 6
percent stop against a 12 percent target breaks even at 33 percent.

**Return BUY only when your stated probability exceeds that breakeven by at least 8
percentage points.** Below that, return PASS even if the company is attractive. You
are not ranking this candidate against others and you will never see the others.
Judge it against this bar alone.

Being well calibrated matters more than being right. A stated 55 that wins 55 percent
of the time is more useful than a stated 80 that wins 60, and the calibration report
will show which you are doing.

PASS is the expected answer. Most candidates should not clear the bar.

---

## S1 — Quality at a fair price

**What this screen is looking for.** A good business whose price has not kept up with
its improving economics. Not a cheap business, and not a good business at any price.
The screen has already established the valuation is low against the company's own
history. Your question is whether the business is actually improving or merely cheap.

**Strongest evidence.** Return on capital and gross margin both improving over four
quarters, while the multiple sits below its own five-year range, funded by real cash
flow rather than accruals, with share count flat or falling and revenue growing.

**Disqualifying regardless of everything else.**
- Accruals in the top quintile. The earnings are not cash.
- Net debt to EBITDA above 4 for a small cap.
- Share count rising more than 5 percent a year. Dilution eats the return.
- Revenue declining while margins improve. That is cost cutting inside a shrinking
  business, and it ends.
- For small and mid caps, cash on hand below four quarters of burn. That company will
  raise equity and you will be diluted at the worst moment.

**The failure mode to guard against.** Value traps. The test is whether the
improvement is visible in the operating numbers, or only in the multiple. If the only
thing that changed is the price, this is not a quality-at-a-fair-price candidate, it
is a falling stock with a good history.

**Weighting.** Free cash flow yield over earnings-based multiples, because it is
harder to manipulate. Four-quarter changes over levels. Revenue trend as a gate on
whether margin improvement means anything.

---

## S2 — Trend

**What this screen is looking for.** A company whose relative strength is improving.
Not one that is already strong. The distinction is the whole screen.

**Strongest evidence.** Relative strength improving over both 21 and 63 days, ADX
rising through 20 confirming a real trend rather than drift, price above a rising
200-day average, and a recent breakout from a defined base on above-average volume.

**Disqualifying regardless of everything else.**
- Within 2 percent of the 52-week high. There is no room and no defined risk point.
- ADX below 15. That is noise, not a trend.
- Volume declining on the advance. The move lacks participation.
- Relative strength improving against SPY but not against its own sector. That is a
  sector story you are buying at company-level risk.

**The failure mode to guard against.** Buying extension. Trend screens are late by
construction, so the question is not whether the chart looks good but whether a
defined entry and a defined invalidation level both exist. If you cannot name where
the thesis is wrong, pass.

**Weighting.** The change in relative strength over its level. ATR as a percent of
price governs position size, so a very volatile name needs proportionally more
conviction to justify the smaller position it will get.

---

## S3 — Sentiment inflection

**What this screen is looking for.** A change in the amount and tone of attention a
company is receiving, measured against its own baseline and never against other
companies. A company that normally gets one article a fortnight suddenly getting
eight is the signal. A company that always gets forty getting forty-five is not.

**Strongest evidence.** Article count z-score above 2 against the ticker's own 90-day
baseline, 7-day sentiment improving against 30-day, and a digest that names a
specific, dateable, business-relevant cause.

**Disqualifying regardless of everything else.**
- The digest reveals promotional coverage rather than reporting.
- A single wire release with no independent coverage.
- Coverage driven by litigation, investigation, dilution or an executive departure
  rather than by the business.
- No digest available. Without a cause you cannot distinguish a contract win from an
  SEC inquiry, and both produce the same z-score.

**The failure mode to guard against.** This is the screen most exposed to noise and
to deliberate manipulation, particularly in small caps where a promotional campaign
produces exactly the pattern the screen selects for. Default to PASS when the digest
cannot name a cause.

**Weighting.** Cause quality above everything. Magnitude only matters once the cause
is legitimate.

---

## S4 — Flow

**What this screen is looking for.** Informed participants changing position, with
weight on how many rather than how much.

**Strongest evidence.** Multiple distinct insiders buying in the open market, dollar
amounts material relative to their compensation.

**Disqualifying regardless of everything else.**
- A single buyer. One person has one reason and you cannot know it.
- Purchases that look like scheduled plan activity or option exercises rather than
  open-market decisions.
- Insider buying alongside a large dilutive offering. The company sold shares while an
  officer bought a few.

**The failure mode to guard against.** Insiders carry information but not timing, and
are frequently early by two or three quarters. This screen needs the technical picture
to not be actively deteriorating, because a correct thesis on a six-month horizon
still stops you out at 2 ATR.

**Weighting.** Distinct buyer count above dollar total. Three officers buying modest
amounts is stronger evidence than one buying a great deal. Institutional ownership
change is secondary and is never a thesis on its own.

---

## S5 — Mean reversion

**What this screen is looking for.** A good business that fell for a reason that is
now resolved, or for a reason that was never about the business. The screen has
already established the company is top-quintile on quality, bottom-quintile on
technicals, and showing stabilisation. Your question is the cause.

**Strongest evidence.** All three stabilisation tests passing, and a digest naming a
specific cause that is one of: a one-time event now past, a sector derating unrelated
to this company, or an earnings miss on an issue management has addressed.

**Disqualifying regardless of everything else.**
- The digest names an unresolved or structural cause. Accounting investigation, going
  concern language, secular demand decline, management exodus, regulatory action.
- No digest available while the decline exceeds 35 percent, on a name that carried at
  least twelve articles in the last ninety days. Where a name is covered that heavily,
  silence after a decline that large is itself the reason to pass. Below twelve,
  absence carries no information and is not held against the candidate.
- The most recent earnings surprise was a miss and the next report is within 10 days.
  You are buying directly into the event that caused the decline.

**The failure mode to guard against, and it is the most important one in this
document.** Quarterly fundamentals lag by up to four months and are keyed on filing
date, so the quality picture you are shown can be four months stale while the price is
current. A pristine trailing picture on a name down 40 percent very often means the
ugly quarter has not been reported yet, and the decline is the market pricing in
information the fundamentals cannot yet carry. Treat a large decline with clean
fundamentals and no identifiable cause as evidence against, not evidence for.

**Weighting.** Cause classification dominates everything else. The quality metrics
already established the business is worth owning; the only remaining question is
whether the market knows something the fundamentals have not shown yet.
