# 5. NUnit for the test suite

- **Status:** accepted
- **Date:** 2026-11-23

## Context

The test suite needs data-driven cases (dozens of malformed lines per format, tolerance boundaries, several generator seeds), expensive shared setup (a reconciled data set, a running worker, a MongoDB container) and a clean split between fast tests and Docker-dependent integration tests, so that a laptop without Docker can still run everything else.

The other services in this set use xUnit. Using NUnit here keeps both frameworks in practice across the set, so it should be a deliberate choice that fits this repository's tests.

## Decision

Use NUnit 5 with the NUnit analyzers:

- `[TestCase]` for table-driven parser and rule tests: each malformed line and each tolerance boundary is one readable row.
- `[OneTimeSetUp]` for expensive fixtures, such as reconciling one hand-written scenario once and asserting many facts about it, or starting the worker once per fixture.
- `[SetUpFixture]` for one MongoDB Testcontainers instance shared by the integration namespace and disposed at the end.
- `[Category("Integration")]` on everything that needs Docker. Locally: `dotnet test --filter "Category!=Integration"`. CI runs both steps.
- `Assert.That` with constraints (`Is.EqualTo`, `Does.Contain`, `Is.All.Matches`, `Has.Count`) and `Assert.Multiple` where one scenario has several independent facts.
- Property-based checks with FsCheck where a property is clearer than examples (amount formatting and parsing round-trip for every exponent and both decimal styles).

## Consequences

- NUnit's analyzers catch common mistakes at build time (wrong argument counts for `TestCase`, misused constraints), and the build treats warnings as errors.
- Shared `OneTimeSetUp` state means tests in a fixture must not mutate it. The fixtures here are read-only after setup.
- Contributors who know xUnit will find the same concepts under different names: `[TestCase]` instead of `[InlineData]`, `[OneTimeSetUp]` instead of `IClassFixture`, `[Category]` instead of `[Trait]`.

## Alternatives considered

- **xUnit**: equally capable. Fixtures through constructor injection and `IClassFixture`/`ICollectionFixture` work well, but table-driven tests with many cases read more naturally as `[TestCase]` rows.
- **MSTest**: fine, with a smaller ecosystem of analyzers and constraint-style assertions.
