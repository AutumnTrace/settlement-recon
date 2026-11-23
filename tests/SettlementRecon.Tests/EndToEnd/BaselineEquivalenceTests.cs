using SettlementRecon.Baseline;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.EndToEnd;

/// <summary>
/// Differential test: the naive LINQ/string implementation used as the benchmark baseline must reach exactly the
/// same outcome and rule counts as the streaming pipeline. Otherwise the benchmark would compare different work.
/// </summary>
public class BaselineEquivalenceTests
{
    [TestCase(42, 20_000, 0.004)]
    [TestCase(99, 10_000, 0.05)]
    public async Task Naive_baseline_and_pipeline_agree_on_every_count(int seed, int transactions, double breakRate)
    {
        using var temp = new TempDirectory();
        var data = await new SampleDataGenerator(new SampleDataOptions { Seed = seed, Transactions = transactions, BreakRate = breakRate })
            .WriteAsync(temp.Path);
        var options = new ReconciliationOptions();

        var pipeline = await new ReconciliationEngine(options).RunAsync(data.Manifest.ToRequest(data.Directory, SettlementFormatRegistry.Default));
        var naive = NaiveReconciler.Run(data.LedgerPath, data.SettlementPaths, options);

        foreach (var provider in pipeline.Providers)
        {
            Assert.That(naive[provider.Provider].Outcomes, Is.EqualTo(provider.Outcomes), $"{provider.Provider} outcomes");
            Assert.That(naive[provider.Provider].Rules, Is.EqualTo(provider.MatchedByRule), $"{provider.Provider} rules");
        }
    }
}
