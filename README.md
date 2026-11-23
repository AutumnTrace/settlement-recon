# settlement-recon

**Reconciles PSP settlement files against the internal ledger at millions of rows per minute, and explains every mismatch.**

[![CI](https://github.com/AutumnTrace/settlement-recon/actions/workflows/ci.yml/badge.svg)](https://github.com/AutumnTrace/settlement-recon/actions/workflows/ci.yml)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> [!NOTE]
> Work in progress. The domain core (currencies, exact minor-unit parsing, matching rules) is in place;
> streaming parsers, the matching pipeline, the CLI and the worker service land next.

## Build and test

```bash
dotnet build
dotnet test --filter "Category!=Integration"
```

## License

[MIT](LICENSE) © Mina Rotari
