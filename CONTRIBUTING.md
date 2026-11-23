# Contributing

Thanks for taking a look. This is a small codebase with a few firm rules, mostly about money and performance.

## Getting started

```bash
dotnet build
dotnet test --filter "Category!=Integration"   # no Docker needed
dotnet test                                    # also runs the Testcontainers MongoDB tests (Docker required)
```

The .NET SDK version is pinned in `global.json` (10.0.100 or later in the 10.0 line). Package versions live in `Directory.Packages.props`, and warnings fail the build.

## Ground rules

- **Money is never a `double`.** Amounts are `long` minor units with a `Currency`. Parse text with `MinorUnits.TryParse`; do not round-trip through `decimal` on the hot path.
- **The hot path does not allocate per row.** Parsers work on `ReadOnlySpan<byte>` and write text into the batch's `Utf8Arena`. Strings are created only for breaks and leftovers. If you touch `Parsing/`, `Matching/LedgerIndex` or `Matching/ProviderMatcher`, run the benchmarks before and after.
- **Every break explains itself.** A new outcome or rule needs a sentence in `BreakReasons` and a scenario test that asserts that sentence.
- **Never guess a pairing.** Fallback rules pair only when exactly one candidate qualifies.
- **No real provider names or data.** Formats describe simulated providers. Test data is generated or hand-written, and never contains card numbers or real references.

## Adding a settlement format

1. Implement `ISettlementFormat` in `src/SettlementRecon.Core/Formats/`. For CSV, derive the parser from `CsvRecordParser<SettlementRow>` and declare columns by header name.
2. Normalise signs (money in is positive) and validate whatever the file lets you validate, such as net against gross minus fee.
3. Register it: add it to `SettlementFormatRegistry.Default` for the CLI and as an `ISettlementFormat` singleton in the worker.
4. Add parser tests for the format's edge cases and every rejection reason.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/SettlementRecon.Benchmarks -- --filter "*"
```

Close other heavy programs first. If a change moves the numbers, update `docs/benchmarks/` and the README table from the new BenchmarkDotNet output, together with its environment block. Never edit numbers by hand.

## Pull requests

- Use Conventional Commit messages (`feat:`, `fix:`, `perf:`, `docs:`, `test:`, `chore:`).
- Fill in the pull request template; CI must be green (build, unit and integration tests, Docker image).
- Add a line to `CHANGELOG.md` under **Unreleased**.
