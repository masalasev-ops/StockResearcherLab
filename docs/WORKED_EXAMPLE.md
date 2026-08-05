# WORKED_EXAMPLE.md

One candidate traced from universe membership to attribution, with the arithmetic
shown at every step where a number changes.

Placeholder ticker `MRDN`, a $3.2B industrial. All figures are illustrative and
internally consistent, not measured.

Read this when a component's behaviour is unclear, or before changing a rule, to see
what else the change touches.

---

## The night: 2027-03-15, 17:30 ET onward

### 1. Universe — passes

| Criterion | Threshold | MRDN | Result |
|---|---|---|---|
| Instrument | common stock | common stock | pass |
| Market cap | ≥ $300M | $3.2B | pass |
| Price | ≥ $5 | $42.10 | pass |
| 20-day median dollar volume | ≥ $2M | $18.0M | pass |
| Trading history | ≥ 250 days | ~1,500 days | pass |

Size bucket: **mid**, since $2B ≤ $3.2B < $10B.

### 2. Gates — passes

Next earnings 24 days away, so outside the blackout. No overnight gap above
threshold. Not currently held in any portfolio. Not in post-exit cooldown.

### 3. Screens — surfaced by mean reversion only

Quality composite percentile **84**, which is top quintile. Technical composite
percentile **12**, which is bottom quintile. Both conditions met, so the stabilisation
gate is evaluated.

| Stabilisation test | Value | Threshold | Result |
|---|---|---|---|
| Close above 20-day average | $42.10 vs $41.60 | above | pass |
| Article count z against own 90-day baseline | 0.4 | < 1.0 | pass |
| Sentiment 7-day against 30-day | +0.05 | ≥ 0 | pass |

Ranking metric is distance below the 200-day average:

```
200-day average          $58.20
price                    $42.10
distance                 (42.10 - 58.20) / 58.20  =  -27.7%
```

Screen score **0.79**. Tonight's floor for this screen is **0.74**, being the 98th
percentile of its own trailing 250-day score distribution. Clears it.

Not surfaced by any other screen, so no deduplication applies tonight.

### 4. Allocation — takes a mid slot

Mean reversion has three mid slots. Eleven mid-cap names cleared its floor tonight;
MRDN ranks second among them. Takes mid slot 2.

Tonight's candidate set: 27 names. Mean reversion contributed 4 of its possible 8,
because only one small-cap name cleared the floor and two small slots stayed empty.
**They were not backfilled from the mid bucket.**

**The attribution row is written now**, with every screen score frozen as it stands
tonight, `config_version` v7, and all nine return columns empty.

### 5. Digest — local model

Four articles fetched. The local model returns 138 tokens:

> Guidance cut 2027-02-04 after losing a single large contract; shares fell 31% over
> the following six sessions. Sector peers derated 9% over the same window on the same
> macro news. Management addressed the contract loss on the 2027-05-12 call and
> reiterated full-year margin targets. No negative coverage since 2027-02-11.

Recorded with `provider = local`, `model_name = qwen-3.5-32b`, `was_rotation = false`.

### 6. Dossier

Prefix 4,612 tokens, cached, hash matching every other call tonight. Candidate block
782 tokens: 350 core, 138 digest, 294 mean-reversion block.

### 7. Judgment — Opus 5 says BUY

```
verdict                 BUY
p_target_before_stop    58
stop_pct                9
target_pct              22
thesis                  Quality intact through a single contract loss now 13 months
                        past. Sector derating accounts for a third of the decline.
                        Margins reiterated, ROIC still improving, no fresh negative
                        coverage in six weeks. Stabilised above the 20-day.
counter_argument        Trailing fundamentals predate the contract loss flowing
                        through revenue.
primary_driver          digest cause classification
```

**Does it clear the bar?**

```
breakeven  =  stop / (stop + target)  =  9 / (9 + 22)  =  29.0%
required   =  breakeven + 8pp margin                    =  37.0%
stated                                                  =  58.0%
58.0 > 37.0                                             →  BUY is valid
```

### 8. Validator — accepted

| Check | Result |
|---|---|
| Structural | Valid JSON, all fields present, probability in range, thesis 41 words |
| Figures cited | "ROIC still improving" maps to `droic4q +180bp` in the block. Match |
| Internal consistency | 58 exceeds the 37 implied by its own stop and target. Consistent |

### 9. The other model disagrees

V4 Pro received the identical dossier and returned **PASS at 34**, below its own
breakeven bar, citing the same trailing-fundamentals concern that Opus 5 put in its
counter-argument.

This is the observation the second research portfolio exists to accumulate. Neither
is right yet. The attribution row will carry both.

### 10. Arbitration — inside Research: Opus 5

Holdings before tonight: 2 large, 2 mid, 1 small. Five open, daily entry cap is 2.

Five BUYs returned. Step by step:

```
STEP 1  drop what the caps would block
        one BUY sits in a sector already at 30%           →  removed
        large bucket is at 2 of 4, so no bucket removals
        4 survive

STEP 2  sort by stated probability
        71,  58 (MRDN),  55,  44

STEP 3  fill 2 entry slots from the top
        71 takes slot 1
        58 takes slot 2                                    →  MRDN in

STEP 4  coin flip
        not needed, no tie at the cutoff
```

Ties at the cutoff are uncommon under a probability scale. Under the integer
conviction scale this replaced, this step would have fired most nights.

### 11. Sizing

```
equity                  $100,000
risk per trade  0.5%  =  $500

stop selection
  model suggestion                       9.0%
  2 x ATR(14), ATR = 2.8% of price       5.6%
  wider of the two                       9.0%
  cap                                   12.0%   not binding
  stop used                              9.0%

position value  =  $500 / 0.09          =  $5,556
shares          =  $5,556 / $42.10      =  131  (rounded down)
position value  =  131 x $42.10         =  $5,515
actual risk     =  $5,515 x 0.09        =  $496
```

| Cap | Limit | This order | Result |
|---|---|---|---|
| Single position | $20,000 | $5,515 | pass |
| Participation | 1% of $18.0M = $180,000 | $5,515 | pass |
| Sector | 30% of equity | within | pass |
| Cash floor | $10,000 retained | within | pass |
| Large bucket | 4 of 8 | mid, not applicable | pass |

### 12. Fill, next morning

```
next open                                $42.55
participation  =  $5,515 / $18,000,000 =  0.031%
slippage       =  10bp (mid) + 25 x 0.031%
               =  10bp + 0.77bp         =  10.77bp  =  0.108%
fill price     =  $42.55 x 1.00108      =  $42.60
filled         =  131 shares            =  $5,580

stop level     =  $42.60 x 0.91         =  $38.77
target level   =  $42.60 x 1.22         =  $51.97
time stop      =  40 trading days
```

### 13. Exit, day 38

Target reached intraday. Exits at $51.97.

```
gross P&L  =  131 x ($51.97 - $42.60)  =  131 x $9.37  =  $1,227
return     =  $1,227 / $5,580                          =  22.0%

SPY over the same 38 days                              =  +2.1%
alpha vs SPY                                           =  19.9%

mid x industrials peer cell median, same 38 days       =  +3.4%
alpha vs peers                                         =  18.6%
```

`trade_outcome` records `exit_reason = target`.

### 14. Attribution, filled as dates mature

**The attribution row follows the candidate, not the trade.** MRDN's position closed
on day 38, but the 63-day return is still computed from the price series on day 63.
This is deliberate and is easy to get wrong: attribution answers what happened to a
name that was surfaced, which must be measurable whether or not it was ever bought.

```
day  5    return_5d_raw    +1.8    vs_spy  +1.1    vs_peers  +0.9
day 21    return_21d_raw  +11.4    vs_spy  +9.8    vs_peers  +8.7
day 63    return_63d_raw  +18.2    vs_spy +15.1    vs_peers +14.0
```

The learning loops read the peer column and never the SPY column.

### 15. The same night in the other three portfolios

| Portfolio | Took | Why |
|---|---|---|
| Research: Opus 5 | MRDN and one other | Arbitration above |
| Research: V4 Pro | Two other names | Passed on MRDN at 34 |
| Screens | Quality screen's top-ranked name, plus trend's | Fixed rotation, no model |
| Random | Two of the 27, date-seeded | Chance |

All four entered exactly two positions, because Screens and Random match the primary
research portfolio's entry count. Had Opus 5 returned no valid BUYs, all four would
have entered nothing.

---

## What this example does not show

A night where the digest chain falls through to the secondary provider. A night where
no provider is healthy and the run halts before any researcher call. A validator
rejection. A stop-out. A time stop. A candidate surfaced by two screens receiving two
screen blocks. An arbitration tie requiring the coin flip.

Each of those is a fixture worth building. They are registered in `FIXTURES.md` as
they are written.
