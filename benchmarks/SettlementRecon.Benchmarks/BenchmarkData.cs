using SettlementRecon.Pipeline;
using SettlementRecon.SampleData;

namespace SettlementRecon.Benchmarks;

/// <summary>
/// Generated input shared by all benchmarks: seed 42 and the default 0.4% break rate. Each size is written once
/// into the temp folder and reused; the generator is deterministic, so a cached set is byte-identical.
/// </summary>
internal static class BenchmarkData
{
    public static (string Directory, RunManifest Manifest) Get(int transactions)
    {
        var directory = Path.Combine(Path.GetTempPath(), "settlement-recon-bench", $"seed42-{transactions}");
        if (!File.Exists(Path.Combine(directory, RunManifest.FileName)))
        {
            new SampleDataGenerator(new SampleDataOptions { Seed = 42, Transactions = transactions })
                .WriteAsync(directory)
                .GetAwaiter()
                .GetResult();
        }

        return (directory, RunManifest.ReadAsync(directory).GetAwaiter().GetResult());
    }

    public static string PathOf(this (string Directory, RunManifest Manifest) data, string provider) =>
        Path.Combine(data.Directory, data.Manifest.Settlements.Single(s => s.Provider == provider).File);
}
