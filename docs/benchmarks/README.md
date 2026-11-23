# Benchmark results

Raw BenchmarkDotNet GitHub-markdown output, copied unedited from `BenchmarkDotNet.Artifacts/results/` after a local run:

- [`reconciliation.md`](reconciliation.md): whole run from files on disk to outcome counts, naive baseline against the pipeline, for 100k and 1M generated transactions.
- [`parsing.md`](parsing.md): parsing alone, on the card provider's CSV of the 1M-transaction set (550,791 lines, 48 MB).

Reproduce:

```bash
dotnet run -c Release --project benchmarks/SettlementRecon.Benchmarks -- --filter "*"
```

The input is generated with seed 42 at the default 0.4% break rate and cached under the system temp folder (`settlement-recon-bench/`). Numbers depend on the machine: the environment block at the top of each file says which one.
