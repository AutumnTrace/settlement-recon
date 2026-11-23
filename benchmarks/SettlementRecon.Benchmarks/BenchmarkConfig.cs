using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace SettlementRecon.Benchmarks;

/// <summary>Fixed warm-up and iteration counts: each operation takes from tens of milliseconds to seconds.</summary>
public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(10).WithLaunchCount(1));
        AddExporter(MarkdownExporter.GitHub);
        SummaryStyle = SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend);
    }
}

/// <summary>Adds the transactions-per-second column, which only makes sense for whole-run benchmarks.</summary>
public sealed class ReconciliationConfig : BenchmarkConfig
{
    public ReconciliationConfig() => AddColumn(new TransactionsPerSecondColumn());
}

/// <summary>
/// Ledger transactions handled per second of mean time. Each generated transaction is one ledger row and, unless a
/// break removed it, one settlement line, so input rows per second are close to twice this figure.
/// </summary>
public sealed class TransactionsPerSecondColumn : IColumn
{
    public string Id => nameof(TransactionsPerSecondColumn);

    public string ColumnName => "Txn/s";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => "Ledger transactions reconciled per second (Transactions / Mean)";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        var transactions = benchmarkCase.Parameters["Transactions"] as int?;
        return mean is > 0 && transactions is { } count
            ? (count / (mean.Value / 1e9)).ToString("N0", CultureInfo.InvariantCulture)
            : "NA";
    }

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
}
