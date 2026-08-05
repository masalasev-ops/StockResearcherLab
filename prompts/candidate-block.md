# candidate-block.md

The per-candidate block, roughly 800 tokens, fresh input on every call.

A candidate token costs around seven times a prefix token, because the prefix is
cached and read many times while each block is paid once per candidate at full
input rate. That single fact determines the format: **bare values with short keys,
no labels, no units, no sentences.** Everything explanatory lives in the prefix
field key.

`fcfy 6.2 p88` costs six tokens. "Free cash flow yield is 6.2 percent, which is in
the 88th percentile" costs sixteen and says the same thing.

---

## Structure

```
IDENTITY          ~40 tokens
CORE METRICS      ~310 tokens   identical for every candidate
NEWS DIGEST       ~150 tokens   from the digest chain
SCREEN BLOCK      ~300 tokens   only for the screens that surfaced this name
```

A name surfaced by two screens receives both screen blocks and runs longer. That is
accepted.

---

## Identity

`ticker`, `sector`, `size_bucket`, `market_cap`, `price`,
`median_dollar_volume_20d`, `screens_surfacing`, and the score within each.

For small and mid caps only, one line of business description, roughly 30 tokens,
written quarterly by the local model and stored. Omitted for large caps because the
model already knows them, and the tokens are wasted there while being genuinely
useful for a $500M industrial.

## Core metrics

Sent for every candidate regardless of screen, so that stated probabilities remain
comparable within a screen. Each as raw value followed by peer percentile.

| Group | Keys |
|---|---|
| Timing | `dte` |
| Valuation | `fcfy`, `evebit5` |
| Quality | `roic`, `droic4q`, `dgm4q`, `nde`, `accr`, `dshares`, `drev4q` |
| Earnings | `surp1`, `surp2` |
| Technical | `d200`, `d52wh`, `drs21`, `drs63`, `drssec`, `atrp`, `adx14`, `vol50` |
| Sentiment | `artz90`, `dsent730` (both nullable) |
| Flow | `insnet90`, `sipct`, `dsi` |
| Balance sheet | `cash`, `burn` (small and mid only) |

`atrp` is in the core rather than a screen block because the risk layer takes the
wider of the model's suggested stop and twice ATR. A model that cannot see
volatility suggests stops that are silently discarded every night.

`dte` is in the core because a 40 percent decline three days before a report is a
different proposition from the same decline three weeks after one, and nothing else
carries that.

## News digest

Roughly 150 tokens from the digest chain, with the provider recorded on the row but
not sent to the model. The model is not told which summariser wrote it, because that
should not influence how it reads it.

Absent only if the run reached here without a digest, which INVARIANT 15 prevents.

## Screen blocks

| Surfaced by | Additional content |
|---|---|
| S1 Quality | Sector-relative valuation detail, revenue growth trend detail |
| S2 Trend | Breakout level, moving average slopes, distance from the base |
| S3 Sentiment | Article count breakdown against baseline, three headlines |
| S4 Flow | Distinct buyer count, largest transaction, ownership change |
| S5 Mean reversion | Peak-to-trough decline, days since peak, all three stabilisation test results, three headlines |

---

## What never enters the block

- Raw price series [INVARIANT 9]
- Full article text, ever. Article text reaches the researcher only as a digest
- Business descriptions for large caps
- Any value the model would need to combine with another to be useful. Precompute it
- Explanatory text of any kind. It belongs in the prefix field key

## Consistency requirement

The key list and its order are generated from the same source the prefix field key
uses. A key present in one and absent from the other is a defect the model cannot
report, because it will simply interpret a positional value as something else.

Add a test asserting the two lists are identical and identically ordered.
