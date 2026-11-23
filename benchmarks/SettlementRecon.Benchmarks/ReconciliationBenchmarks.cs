using BenchmarkDotNet.Attributes;
using SettlementRecon.Baseline;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;

namespace SettlementRecon.Benchmarks;

/// <summary>
/// End to end: read the ledger and three settlement files from disk, match, classify, count. Both sides produce
/// identical outcome counts (a test in the suite checks this); the pipeline also builds the full report with
/// reasons, the baseline does not.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ReconciliationConfig))]
public class ReconciliationBenchmarks
{
    private (string Directory, RunManifest Manifest) _data;
    private ReconciliationRequest _request = null!;
    private (string Provider, string Path)[] _settlements = null!;
    private readonly ReconciliationOptions _options = new();

    [Params(100_000, 1_000_000)]
    public int Transactions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = BenchmarkData.Get(Transactions);
        _request = _data.Manifest.ToRequest(_data.Directory, SettlementFormatRegistry.Default);
        _settlements = [.. _data.Manifest.Settlements.Select(s => (s.Provider, Path.Combine(_data.Directory, s.File)))];
    }

    [Benchmark(Baseline = true, Description = "Naive (ReadAllLines + Split + LINQ)")]
    public long Naive() =>
        NaiveReconciler.Run(Path.Combine(_data.Directory, _data.Manifest.Ledger), _settlements, _options).Values.Sum(v => v.Outcomes.Breaks);

    [Benchmark(Description = "Pipeline (Pipelines + spans + channels)")]
    public async Task<long> Pipeline() =>
        (await new ReconciliationEngine(_options).RunAsync(_request)).Summary.Breaks;
}
