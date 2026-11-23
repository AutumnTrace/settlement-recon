# 4. Store reconciliation runs in MongoDB

- **Status:** accepted
- **Date:** 2026-11-23

## Context

The worker stores every run so operations can browse runs and their breaks through the API. A run is naturally a document: a header with nested per-provider summaries, per-currency totals, input file statistics with parse issues and the tolerances used, followed by a list of breaks. The list ranges from none to tens of thousands, and its items have optional fields that depend on the break type.

Access patterns are simple and document-shaped:

- write a run once, never update it;
- list runs, newest first;
- read one run;
- page through its breaks, filtered by break type or provider, in file order;
- find earlier breaks for a given reference across runs.

There are no joins and no cross-run transactions, and the schema follows the report, which will keep evolving.

## Decision

Use MongoDB, with two collections:

- `runs`: one document per run. Its `report` field *is* the JSON report produced by the engine (source-generated `System.Text.Json`, converted with `BsonDocument.Parse`), minus the item lists. The stored shape, the API shape and `report.json` from the CLI are the same shape. An index on `startedAt` (descending) serves the run list.
- `items`: one small document per break or review match: `runId`, `kind`, `seq` (file order), `provider`, `status`, `reference` and the item itself. Compound indexes `(runId, kind, seq)`, `(runId, kind, status, seq)` and `(runId, kind, provider, seq)` serve every API query as an index scan, and `(reference)` serves cross-run lookups. Keeping items out of the run document keeps large runs well under MongoDB's 16 MB document limit and makes paging cheap.
- Saves are idempotent and ordered: the run's old items are deleted, the items are written, and the run header is written last. A run only appears in listings once it is complete, and a retried save replaces a partial one.
- An in-memory store implements the same `IRunStore` interface, so `dotnet run` works on a fresh clone without a database. Integration tests run the Mongo store against a real MongoDB 8 container (Testcontainers).

## Consequences

- No mapping layer between report and storage: adding a field to the report stores it.
- Queries that are not anticipated by an index (for example "all fee differences over 1 EUR this month") need a new index or the aggregation pipeline. Reporting at that level belongs in a warehouse fed from these documents.
- MongoDB is one more piece of infrastructure to run. Atlas or Azure Cosmos DB for MongoDB (vCore) are managed options, and the code only uses the standard driver.
- Money inside the stored report keeps the report's representation (`amount` string, `currency`, integer `minor`), so nothing is ever stored as a floating-point number.

## Alternatives considered

- **PostgreSQL with `jsonb`**: a strong option, and the right one if runs had to be joined with relational ledger data in the same database. Here the run is self-contained, and the other services in this set already cover the relational side.
- **PostgreSQL, fully normalised**: runs, providers, currency totals, files, issues and items as tables. That is six tables and a mapping layer for data that is written once and always read as a whole.
- **Files only** (keep `report.json` next to the archived folder): simple, but no querying across runs, no paging and no filtering without loading whole reports.
