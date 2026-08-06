# StockResearcherLab

A personal paper-trading research lab. It asks one question: **does an AI researcher
add selection judgment beyond mechanical screens?**

Every evening it screens the entire US stock market through five independent
screens, condenses each candidate's news to a digest on a local model, and has two
AI researchers judge every candidate independently against an absolute bar. Four
paper portfolios then run off the identical candidate set with identical risk rules,
so any difference between them is selection rather than luck, timing or sizing.

Nothing here trades real money.

## The four portfolios

| Portfolio | Selects by |
|---|---|
| Research: Opus 5 | Frontier model judging each candidate |
| Research: V4 Pro | A model roughly nine times cheaper, judging identically |
| Screens | Each screen's top-ranked name in fixed rotation, no AI |
| Random | Drawn at random from the candidate set |

Screens and Random match their entry count to the primary research portfolio, so
nobody wins on market exposure. SPY is a reference line, not a portfolio.

## Documents

`PROGRESS.md` carries the present build state. `CHANGELOG.md` is a closed record
of corpus versioning, which stopped at D-67. This file states no version, and
there is no longer one to state.

Read these three, in order, to understand the lab:

1. `docs/ARCHITECTURE.html` — the design, with the figures. Start here.
2. `docs/WORKED_EXAMPLE.md` — one candidate traced end to end with arithmetic.
3. `docs/VALIDITY.md` — what is claimed, what would falsify it, what it cannot test.

Read these when you need them:

| File | What it is | Author |
|---|---|---|
| `CLAUDE.md` | Rules for agents. Section 2 is the invariants | Human only |
| `docs/DECISIONS.md` | The numbered register. Looked up, not read through | Human only |
| `docs/BUILD_PLAN.md` | Phases, definitions of done, carried obligations | Human only |
| `docs/SCHEMA.md` | Tables, grain, and write ownership per operation | Human only |
| `docs/CONFIG_REFERENCE.md` | Every config key and its verified consumer | Mixed |
| `docs/GLOSSARY.md` | Terms with a specific meaning here | Human only |
| `docs/FIXTURES.md` | The single registry of test fixtures | Mixed |
| `docs/RUNBOOK.md` | How to run it and what failures mean | Mixed |
| `prompts/` | Rubrics, prefix, candidate block, digest instruction | Human only |
| `prompts/spent/` | Every prompt issued, archived verbatim | Never edited |

These track state and are appended to, never rewritten:

| File | What it is |
|---|---|
| `docs/PROGRESS.md` | What has actually been built |
| `docs/CHANGELOG.md` | Corpus versions, a closed record since D-67 |

The architecture and the invariants exist to constrain the code. If the code
disagrees with them, the code is wrong.

## Where these files live

The repository root holds `README.md` and `CLAUDE.md` only. Every other document
lives in `docs/`. One rule, so anything resolving a document path needs to know one
thing rather than a list of exceptions.

Code lives in `src/`. Runtime prompts and spent prompts live in
`prompts/`, which has its own README explaining why those two things sit side by
side under opposite rules. Neither is a document and neither belongs in `docs/`.

## Stack

.NET 10, Postgres 18, Blazor front end over a read-only API. Solution in `.slnx`
format with central package management via `Directory.Build.props` and
`Directory.Packages.props`. Configuration in append-only versioned rows where
current is `MAX(version)`. Secrets in `appsettings.Secrets.json`, never committed.
Tickers use the EODHD dash form, for example `BRK-B`.

A local model runs on the machine's GPU via an OpenAI-compatible endpoint, with
Haiku 4.5 as the secondary link in the digest chain.

Postgres throughout [D-49]. Money is native `numeric`, never `float` or `double` in
any monetary path.

## Cost

Roughly $50 a year against a $100 budget, split about $45 for the batched frontier
researcher and $5 for the cheaper one. The remaining headroom is deliberately
unspent until the calibration report and the model comparison have real data.

## Where to start

`docs/PROGRESS.md`, for which phase is current and what is built, then that phase
in `docs/BUILD_PLAN.md` [D-66].
