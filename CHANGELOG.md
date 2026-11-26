# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-11-26

### Fixed

- `recon run` refuses tolerances outside the ranges the worker enforces on start, naming each option and its allowed range. A negative fee tolerance used to report every match as a fee difference, and a 20,000 bps amount tolerance hid real mismatches, both with exit code 0 (#9)

## [0.1.0] - 2026-11-23

First release.

### Added

- **Streaming parsers** on `System.IO.Pipelines` and UTF-8 spans: line framing with BOM, CRLF and segment-straddling lines; a quote-aware CSV tokenizer; amounts parsed straight into integer minor units (dot and comma decimal styles, strict digit grouping); rejected lines reported with a reason and line number (#1)
- Three simulated provider formats behind `ISettlementFormat`: **orbit** (comma CSV, gross/fee/net, negative refunds), **canal** (semicolon CSV, comma decimals, `dd.MM.yyyy`, payout batches) and **hashrail** (JSON Lines via `Utf8JsonReader`, direction plus unsigned amount, pending transfers skipped); ledger export as CSV or JSON Lines (#1)
- **Reconciliation engine**: bounded `System.Threading.Channels` between parse, index and match stages; per-provider hash index (open addressing, XxHash3) as the build side; parallel matching per provider; root-cause failure reporting (#2)
- **Explainable outcomes**: matched, matched with fee difference, amount mismatch, missing in ledger, missing at PSP, duplicate and currency mismatch, each break with a full-sentence reason; pairing rules for reference, within tolerance, batch payout (one-to-many) and amount-and-date fallback for mangled references (#2)
- JSON report (source-generated `System.Text.Json`) and Markdown summary (#2)
- **Sample data generator**: seeded, byte-identical across machines, with injected breaks and an `expected.json` (#2)
- **`recon` CLI** (System.CommandLine): `run`, `generate`, `formats`, `--fail-on-breaks` (#2)
- **Worker service**: inbox watcher with atomic claim, archive, failed folder with `error.txt`, re-queue after shutdown; MongoDB run store (run documents plus indexed break documents) with an in-memory fallback; REST API for runs, breaks, review matches and Markdown summaries with RFC 9457 problem details (#3)
- **Observability**: Serilog (text or compact JSON), OpenTelemetry metrics and traces with OTLP and Azure Monitor exporters switched on by configuration, `/health/live` and `/health/ready` (#3)
- Chiseled, non-root Docker image, `docker compose` stack with MongoDB, GHCR release workflow, CI with Testcontainers integration tests, coverage summary and an image smoke test (#3)
- **BenchmarkDotNet** suite comparing a naive LINQ/string reconciler with the pipeline at 100k and 1M transactions, plus a differential test that keeps both in agreement (#4)
- Summaries show a break matrix by provider and examples per break type; README with real CLI, Markdown and API output; architecture decision records; CodeQL, Dependabot, issue and pull request templates (#5)

### Changed

- The ledger index stores entries in fixed 1 MiB chunks, so growing never copies them; a 1M-transaction run allocates 113.76 MB, down from about 310 MB (#4)

[Unreleased]: https://github.com/AutumnTrace/settlement-recon/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/AutumnTrace/settlement-recon/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/AutumnTrace/settlement-recon/releases/tag/v0.1.0
