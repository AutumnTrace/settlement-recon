using System.Globalization;
using System.Text;
using SettlementRecon.Matching;
using SettlementRecon.Monetary;

namespace SettlementRecon.Reporting;

/// <summary>Renders a report as GitHub-flavoured Markdown: a summary a person can read in a pull request, chat or email.</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Render(ReconciliationReport report, int maxItems = 25)
    {
        var md = new StringBuilder();
        var s = report.Summary;

        md.AppendLine("# Settlement reconciliation");
        md.AppendLine();
        md.AppendLine(Invariant, $"Run `{report.RunId}` · {report.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC · {report.DurationMs / 1000:0.00} s · {s.RowsPerSecond:#,0} rows/s");
        md.AppendLine();
        md.AppendLine(Invariant, $"**{s.Outcomes.Matched:#,0}** of **{s.SettlementLines:#,0}** settlement lines matched cleanly ({s.MatchRate:0.00%}) against **{s.LedgerEntries:#,0}** ledger entries. **{s.Breaks:#,0}** {(s.Breaks == 1 ? "break needs" : "breaks need")} a look.");
        md.AppendLine();

        md.AppendLine("| Outcome | Count | Share |");
        md.AppendLine("|---|---:|---:|");
        var total = Math.Max(1, s.Outcomes.Total);
        foreach (var status in Enum.GetValues<ReconStatus>())
        {
            var count = s.Outcomes[status];
            md.AppendLine(Invariant, $"| {Icon(status)} {status.ToDisplayName()} | {count:#,0} | {(double)count / total:0.00%} |");
        }

        md.AppendLine();
        var r = s.MatchedByRule;
        var batchEntries = report.Providers.Sum(p => p.LedgerEntriesSettledInBatches);
        md.AppendLine(Invariant, $"Paired by rule: reference {r.Reference:#,0} · within tolerance {r.ReferenceWithinTolerance:#,0} · batch payout {r.BatchPayout:#,0} (covering {batchEntries:#,0} ledger entries) · amount and date {r.AmountAndDate:#,0}");
        md.AppendLine();

        md.AppendLine("## Providers");
        md.AppendLine();
        md.AppendLine("| Provider | Settlement lines | Ledger entries | Matched | Breaks | Rejected lines | Settled gross | Fees | Net |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var p in report.Providers)
        {
            var rejected = p.Files.Sum(f => f.Rejected);
            md.AppendLine(Invariant,
                $"| {Escape(p.DisplayName)} | {p.SettlementLines:#,0} | {p.LedgerEntries:#,0} | {p.Outcomes.Matched:#,0} | {p.Outcomes.Breaks:#,0} | {rejected:#,0} | {Totals(p, t => t.Gross)} | {Totals(p, t => t.Fees)} | {Totals(p, t => t.Net)} |");
        }

        md.AppendLine();
        if (report.LedgerEntriesOutOfScope > 0)
        {
            md.AppendLine(Invariant, $"{report.LedgerEntriesOutOfScope:#,0} ledger entries belong to providers without a settlement file in this run and were left out.");
            md.AppendLine();
        }

        AppendItems(md, "Breaks", report.Breaks, s.Breaks, maxItems);
        AppendItems(md, "Matched without a reference (please review)", report.ReviewMatches, report.ReviewMatches.Count, maxItems);
        AppendIssues(md, report, maxItems);

        var t = report.Settings;
        md.AppendLine(Invariant, $"<sub>Tolerances: amount max({t.AmountToleranceMinor} minor units, {t.AmountToleranceBps} bps) · fee {t.FeeToleranceMinor} minor units · date window {t.DateWindowDays} days · amount-and-date fallback {(t.AmountAndDateFallback ? "on" : "off")}</sub>");
        return md.ToString();
    }

    private static void AppendItems(StringBuilder md, string title, IReadOnlyList<ReconItem> items, long total, int maxItems)
    {
        if (items.Count == 0)
        {
            return;
        }

        var shown = Math.Min(items.Count, maxItems);
        md.AppendLine(Invariant, $"## {title}{(shown < total ? string.Create(Invariant, $" (first {shown} of {total:#,0})") : string.Empty)}");
        md.AppendLine();
        md.AppendLine("| Provider | Outcome | Reference | PSP | Ledger | Difference | Reason |");
        md.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var item in items.Take(shown))
        {
            md.AppendLine(Invariant,
                $"| {item.Provider} | {Icon(item.Status)} {item.Status.ToDisplayName()} | `{Escape(item.Reference)}` | {Amount(item.PspAmount)} | {Amount(item.LedgerAmount)} | {Signed(item.Difference)} | {Escape(item.Reason)} |");
        }

        md.AppendLine();
    }

    private static void AppendIssues(StringBuilder md, ReconciliationReport report, int maxItems)
    {
        var issues = report.Ledger.Issues.Concat(report.Providers.SelectMany(p => p.Files.SelectMany(f => f.Issues))).ToArray();
        if (issues.Length == 0)
        {
            return;
        }

        var total = report.Summary.RejectedLines;
        var shown = Math.Min(issues.Length, maxItems);
        md.AppendLine(Invariant, $"## Rejected lines{(shown < total ? string.Create(Invariant, $" (first {shown} of {total:#,0})") : string.Empty)}");
        md.AppendLine();
        md.AppendLine("| File | Line | Problem |");
        md.AppendLine("|---|---:|---|");
        foreach (var issue in issues.Take(shown))
        {
            md.AppendLine(Invariant, $"| {Escape(issue.Source)} | {issue.Line} | {Escape(issue.Message)} |");
        }

        md.AppendLine();
    }

    private static string Totals(ProviderReport provider, Func<CurrencyTotals, Money> pick) =>
        provider.Settled.Count == 0 ? "–" : string.Join("<br/>", provider.Settled.Select(t => pick(t).ToString()));

    private static string Amount(Money? money) => money?.ToString() ?? "–";

    private static string Signed(Money? money) => money?.ToSignedString() ?? "–";

    private static string Icon(ReconStatus status) => status switch
    {
        ReconStatus.Matched => "✅",
        ReconStatus.MatchedWithFeeDifference => "🟡",
        ReconStatus.MissingInLedger or ReconStatus.MissingAtPsp => "🟠",
        _ => "🔴",
    };

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
