using System.Text.Json;
using System.Text.Json.Serialization;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;

namespace SettlementRecon.SampleData;

/// <summary>What a correct engine must report for one provider of a generated data set.</summary>
public sealed record ExpectedProvider(
    string Provider,
    long SettlementLines,
    long LedgerEntries,
    long SkippedLines,
    OutcomeCounts Outcomes,
    RuleCounts MatchedByRule);

/// <summary>The files of a generated data set plus the outcomes that were injected into them.</summary>
public sealed record GeneratedDataSet(
    string Directory,
    RunManifest Manifest,
    int Seed,
    int Transactions,
    long LedgerRows,
    long SettlementRows,
    IReadOnlyList<ExpectedProvider> Expected)
{
    public const string ExpectedFileName = "expected.json";

    public OutcomeCounts ExpectedOutcomes => Expected.Aggregate(OutcomeCounts.Empty, (sum, p) => sum + p.Outcomes);

    public string LedgerPath => Path.Combine(Directory, Manifest.Ledger);

    public IEnumerable<(string Provider, string Path)> SettlementPaths =>
        Manifest.Settlements.Select(s => (s.Provider, Path.Combine(Directory, s.File)));

    public async Task WriteExpectedAsync(CancellationToken cancellationToken = default)
    {
        var stream = File.Create(Path.Combine(Directory, ExpectedFileName));
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, Expected, SampleJsonContext.Default.IReadOnlyListExpectedProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<ExpectedProvider>))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
