# prompts/

Two different kinds of thing live here and they follow opposite rules.

## Runtime prompts — the `.md` files in this directory

`rubrics.md`, `prefix-template.md`, `candidate-block.md`,
`digest-instruction.md`.

These are **product**. Code reads them at execution time. They are versioned,
they are edited deliberately, and a change to any of them splits accumulated
history into halves that cannot be pooled, so a change carries a decision number
and a config version.

Treat them the way you would treat code.

## Spent prompts — `spent/`

Every prompt that has been issued, archived verbatim.

These are **records**. Nothing reads them at runtime and they are ~~never edited~~
[amended, D-63] never edited except to correct the archive to the text actually
issued. A spent prompt records what was asked, not what should have been asked. If a
later decision means the request was wrong, that belongs in `DECISIONS.md` and
in the next prompt. Correcting the archive destroys the only evidence of why the
code came out the way it did. The single exception is an archive that does not
match what was issued, where the correction restores the record rather than
damaging it and the header says that it happened and why [D-63].

The reconciliation question in `CLAUDE.md` §14 is the test:

> Does this decision change a phase prompt that has not been run yet?

If yes, fix the unrun prompt. If it changes one already run, leave it alone and
note the drift in `PROGRESS.md`.

### Naming

`phase-<n>-<slug>.md` for build prompts, matching `BUILD_PLAN.md`.
`design-<slug>-v<n>.md` for anything else.

Each file opens with a header giving target, issue date, status, and what it
produced. Status is `ISSUED`, `SPENT`, or `SUPERSEDED BY <file>`.

A prompt superseded before being run is still archived. Knowing that a version
existed and was replaced is part of the record.
