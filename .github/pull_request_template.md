## Summary

<!-- What does this change and why? -->

## Changes

-

## Testing

<!-- How was it verified? Paste the relevant `dotnet test` / CLI output. -->

## Checklist

- [ ] `dotnet test --filter "Category!=Integration"` passes locally (CI also runs the Testcontainers integration tests)
- [ ] New matching rules come with a scenario test that asserts the reason text
- [ ] New settlement formats have parser tests for their edge cases (BOM, quoting, decimals, signs)
- [ ] Performance-sensitive changes were checked with `benchmarks/SettlementRecon.Benchmarks`, and the README numbers were updated only from a real run
- [ ] `CHANGELOG.md` updated under **Unreleased**
