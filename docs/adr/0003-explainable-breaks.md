# 3. Every break carries a complete, human-readable reason

- **Status:** accepted
- **Date:** 2026-11-23

## Context

The people who act on reconciliation results are payment operations and finance analysts, not the engineers who wrote the matcher. A typical tool flags a line as `MISMATCH` and leaves the analyst to open two files, find both rows, compute the difference and guess which rule fired. That is slow, and it is the main reason reconciliation tools end up with spreadsheets next to them.

Some matches are also not certain. A bank rail that strips dashes from a reference still lets you pair a line with a ledger entry by amount and date, but a person should see that the pairing happened.

## Decision

- Outcomes are a closed set: `Matched`, `MatchedWithFeeDifference`, `AmountMismatch`, `MissingInLedger`, `MissingAtPsp`, `Duplicate` and `CurrencyMismatch`. The pairing rule is recorded separately: `Reference`, `ReferenceWithinTolerance`, `BatchPayout` or `AmountAndDate`.
- Every outcome other than a clean match becomes a `ReconItem` with both sides' amounts, the signed difference, dates, line numbers in both files and a `Reason`. The reason is one or two complete sentences that state what each side says, the difference and the tolerance or rule that decided it.
- Matches made without a reference (amount and date) are listed separately as review matches, with the same kind of reason.
- The fallback never guesses. If more than one unclaimed ledger entry qualifies, nothing is paired, and the break says how many candidates there were.
- The decision (`MatchRules`) and the wording (`BreakReasons`) are separate. Rules work on primitives on the hot path, and wording is only built for the small fraction of rows that need it.
- The report records the tolerances that applied, so a reason can be checked against the settings later.

## Consequences

- An analyst can act on a break from the Markdown summary or the API alone, without opening the input files.
- Tests assert the exact reason sentences, which makes them part of the contract. Changing the wording means changing tests, and that is intended.
- Reasons are English text. Localisation would need message templates with the structured fields, which `ReconItem` already carries.
- There is a cap on items kept per provider (100,000 by default) so a wrong input file (last month's ledger, say) cannot produce a gigabyte report. Counts stay exact and the provider is marked as truncated.

## Alternatives considered

- **Status codes only**: cheapest, but it moves the work to the analyst.
- **Structured fields only, rendered by the UI**: cleaner for localisation, but every consumer (CLI, Markdown, API, logs) would need its own renderer. The structured fields are kept anyway, so this remains possible.
