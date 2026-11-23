using System.Security.Cryptography;
using SettlementRecon.Formats;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;
using SettlementRecon.SampleData;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.EndToEnd;

/// <summary>
/// Generator → engine → report. The generator records which problems it injected; the engine has to find
/// exactly those, per provider, per outcome and per matching rule, for several seeds.
/// </summary>
public class GeneratedDataTests
{
    private static async Task<(GeneratedDataSet Data, ReconciliationReport Report)> GenerateAndRun(
        TempDirectory temp, int seed, int transactions, double breakRate, ReconciliationOptions? options = null)
    {
        var data = await new SampleDataGenerator(new SampleDataOptions { Seed = seed, Transactions = transactions, BreakRate = breakRate })
            .WriteAsync(Path.Combine(temp.Path, $"seed-{seed}"));
        var request = data.Manifest.ToRequest(data.Directory, SettlementFormatRegistry.Default);
        var report = await new ReconciliationEngine(options ?? new ReconciliationOptions()).RunAsync(request);
        return (data, report);
    }

    [TestCase(1, 20_000, 0.01)]
    [TestCase(42, 20_000, 0.004)]
    [TestCase(2026, 5_000, 0.05)]
    public async Task Engine_finds_exactly_the_injected_outcomes(int seed, int transactions, double breakRate)
    {
        using var temp = new TempDirectory();

        var (data, report) = await GenerateAndRun(temp, seed, transactions, breakRate);

        Assert.That(report.Summary.RejectedLines, Is.Zero, "generated files must parse cleanly");
        foreach (var expected in data.Expected)
        {
            var actual = report.Providers.Single(p => p.Provider == expected.Provider);
            Assert.Multiple(() =>
            {
                Assert.That(actual.Outcomes, Is.EqualTo(expected.Outcomes), $"{expected.Provider} outcomes");
                Assert.That(actual.MatchedByRule, Is.EqualTo(expected.MatchedByRule), $"{expected.Provider} rules");
                Assert.That(actual.SettlementLines, Is.EqualTo(expected.SettlementLines), $"{expected.Provider} lines");
                Assert.That(actual.LedgerEntries, Is.EqualTo(expected.LedgerEntries), $"{expected.Provider} ledger entries");
                Assert.That(actual.Files.Sum(f => f.Skipped), Is.EqualTo(expected.SkippedLines), $"{expected.Provider} skipped");
            });
        }

        Assert.That(report.Breaks, Has.Count.EqualTo(report.Summary.Breaks));
        Assert.That(report.Breaks.Select(b => b.Reason), Is.All.Not.Empty);
        Assert.That(report.ReviewMatches, Has.Count.EqualTo(report.Summary.MatchedByRule.AmountAndDate));
    }

    [Test]
    public async Task A_high_break_rate_exercises_every_outcome_and_rule()
    {
        using var temp = new TempDirectory();

        var (_, report) = await GenerateAndRun(temp, 7, 10_000, 0.08);

        var outcomes = report.Summary.Outcomes;
        var rules = report.Summary.MatchedByRule;
        Assert.That(Enum.GetValues<ReconStatus>().Select(s => outcomes[s]), Is.All.GreaterThan(0));
        Assert.That(new[] { rules.Reference, rules.ReferenceWithinTolerance, rules.BatchPayout, rules.AmountAndDate }, Is.All.GreaterThan(0));
    }

    [Test]
    public async Task Small_batches_and_a_one_slot_channel_give_the_same_answer()
    {
        using var temp = new TempDirectory();

        var (_, defaults) = await GenerateAndRun(temp, 3, 8_000, 0.02);
        var (_, tight) = await GenerateAndRun(temp, 3, 8_000, 0.02, new ReconciliationOptions { BatchSize = 64, ChannelCapacity = 1 });

        Assert.That(tight.Summary with { RowsPerSecond = 0 }, Is.EqualTo(defaults.Summary with { RowsPerSecond = 0 }));
        Assert.That(tight.Breaks.Select(b => b.Reason), Is.EqualTo(defaults.Breaks.Select(b => b.Reason)));
    }

    [Test]
    public async Task The_same_seed_produces_byte_identical_files()
    {
        using var temp = new TempDirectory();
        var options = new SampleDataOptions { Seed = 11, Transactions = 3_000, BreakRate = 0.02 };

        var first = await new SampleDataGenerator(options).WriteAsync(Path.Combine(temp.Path, "a"));
        var second = await new SampleDataGenerator(options).WriteAsync(Path.Combine(temp.Path, "b"));
        var other = await new SampleDataGenerator(options with { Seed = 12 }).WriteAsync(Path.Combine(temp.Path, "c"));

        Assert.That(Hashes(second.Directory), Is.EqualTo(Hashes(first.Directory)));
        Assert.That(Hashes(other.Directory), Is.Not.EqualTo(Hashes(first.Directory)));
    }

    [Test]
    public async Task The_json_report_round_trips()
    {
        using var temp = new TempDirectory();
        var (_, report) = await GenerateAndRun(temp, 5, 2_000, 0.05);

        using var stream = new MemoryStream();
        await JsonReport.WriteAsync(report, stream);
        stream.Position = 0;
        var read = await JsonReport.ReadAsync(stream);

        Assert.That(JsonReport.Serialize(read), Is.EqualTo(JsonReport.Serialize(report)));
        Assert.That(read.Breaks, Has.Count.EqualTo(report.Breaks.Count));
    }

    [Test]
    public async Task The_markdown_summary_has_outcomes_providers_and_explained_breaks()
    {
        using var temp = new TempDirectory();
        var (_, report) = await GenerateAndRun(temp, 5, 2_000, 0.05);

        var markdown = MarkdownReport.Render(report, examplesPerType: 2);

        Assert.That(markdown, Does.StartWith("# Settlement reconciliation"));
        Assert.That(markdown, Does.Contain("| ✅ Matched |"));
        Assert.That(markdown, Does.Contain("## Providers"));
        Assert.That(markdown, Does.Contain("## Breaks by provider"));
        Assert.That(markdown, Does.Contain("## Break examples (up to 2 per type)"));
        var itemRows = markdown.Split('\n').Count(l => l.StartsWith("| orbit |", StringComparison.Ordinal)
            || l.StartsWith("| canal |", StringComparison.Ordinal)
            || l.StartsWith("| hashrail |", StringComparison.Ordinal));
        Assert.That(itemRows, Is.LessThanOrEqualTo(3 + (6 * 2) + 2), "matrix rows, at most two examples per type, at most two review matches");
        Assert.That(markdown, Does.Contain("Tolerances: amount max(1 minor unit, 5 bps) · fee 1 minor unit"));
    }

    private static Dictionary<string, string> Hashes(string directory) =>
        Directory.GetFiles(directory).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
}
