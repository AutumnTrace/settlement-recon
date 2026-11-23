using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/SettlementRecon.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(SettlementRecon.Benchmarks.ReconciliationBenchmarks).Assembly).Run(args);
