using System.Globalization;
using SettlementRecon.Matching;
using SettlementRecon.Reporting;

namespace SettlementRecon.Cli;

/// <summary>A compact, plain-text version of the summary for the terminal.</summary>
internal static class ConsoleSummary
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Write(TextWriter output, ReconciliationReport report)
    {
        var s = report.Summary;
        output.WriteLine(string.Create(Invariant,
            $"Reconciled {s.SettlementLines:#,0} settlement lines against {s.LedgerEntries:#,0} ledger entries in {report.DurationMs / 1000:0.00} s ({s.RowsPerSecond:#,0} rows/s)"));
        output.WriteLine();

        foreach (var status in Enum.GetValues<ReconStatus>())
        {
            output.WriteLine(string.Create(Invariant, $"  {status.ToDisplayName(),-30}{s.Outcomes[status],10:#,0}"));
        }

        var r = s.MatchedByRule;
        output.WriteLine();
        output.WriteLine(string.Create(Invariant,
            $"  Paired by: reference {r.Reference:#,0} · within tolerance {r.ReferenceWithinTolerance:#,0} · batch payout {r.BatchPayout:#,0} · amount and date {r.AmountAndDate:#,0}"));
        if (s.RejectedLines > 0)
        {
            output.WriteLine(string.Create(Invariant, $"  Rejected lines: {s.RejectedLines:#,0} (see summary.md)"));
        }

        output.WriteLine();
        output.WriteLine(string.Create(Invariant, $"  {"Provider",-10}{"Lines",10}{"Ledger",10}{"Matched",10}{"Breaks",8}"));
        foreach (var p in report.Providers)
        {
            output.WriteLine(string.Create(Invariant,
                $"  {p.Provider,-10}{p.SettlementLines,10:#,0}{p.LedgerEntries,10:#,0}{p.Outcomes.Matched,10:#,0}{p.Outcomes.Breaks,8:#,0}"));
        }

        var shown = report.Breaks.Take(5).ToArray();
        if (shown.Length > 0)
        {
            output.WriteLine();
            output.WriteLine(string.Create(Invariant, $"  First breaks ({shown.Length} of {s.Breaks:#,0}):"));
            foreach (var item in shown)
            {
                output.WriteLine($"  - [{item.Provider}] {item.Status.ToDisplayName()}: {item.Reason}");
            }
        }
    }
}
