using SettlementRecon.Matching;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Reporting;

/// <summary>
/// The result of one reconciliation run. Every settlement line gets exactly one outcome; ledger entries that no
/// line claimed (missing at PSP) and duplicate ledger entries get one too, so
/// <c>outcomes total = settlement lines + missing at PSP + ledger duplicates</c>.
/// </summary>
public sealed record ReconciliationReport(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double DurationMs,
    ReconSettings Settings,
    InputFileReport Ledger,
    long LedgerEntriesOutOfScope,
    ReconSummary Summary,
    IReadOnlyList<ProviderReport> Providers,
    IReadOnlyList<ReconItem> Breaks,
    IReadOnlyList<ReconItem> ReviewMatches);

/// <summary>Totals across all providers.</summary>
/// <param name="MatchRate">Clean matches divided by settlement lines, 0 to 1.</param>
/// <param name="RowsPerSecond">Ledger plus settlement rows parsed and matched per second of wall-clock time.</param>
public sealed record ReconSummary(
    long SettlementLines,
    long LedgerEntries,
    OutcomeCounts Outcomes,
    RuleCounts MatchedByRule,
    long Breaks,
    double MatchRate,
    double RowsPerSecond,
    long RejectedLines);

public sealed record ProviderReport(
    string Provider,
    string DisplayName,
    IReadOnlyList<InputFileReport> Files,
    long SettlementLines,
    long LedgerEntries,
    OutcomeCounts Outcomes,
    RuleCounts MatchedByRule,
    long LedgerEntriesSettledInBatches,
    IReadOnlyList<CurrencyTotals> Settled,
    bool ItemsTruncated);

/// <summary>What the PSP settled in one currency: gross, fees and net, signed from the operator's point of view.</summary>
public sealed record CurrencyTotals(string Currency, Money Gross, Money Fees, Money Net, long Lines);

public sealed record InputFileReport(
    string Name,
    string? Provider,
    long Lines,
    long Rows,
    long Skipped,
    long Rejected,
    long Bytes,
    IReadOnlyList<ParseIssue> Issues)
{
    public static InputFileReport From(ParseStats stats, string? provider) =>
        new(stats.Source, provider, stats.Lines, stats.Rows, stats.Skipped, stats.Rejected, stats.Bytes, stats.Issues);

    public static InputFileReport Combine(string name, string? provider, IReadOnlyList<ParseStats> files) => new(
        name,
        provider,
        files.Sum(f => (long)f.Lines),
        files.Sum(f => f.Rows),
        files.Sum(f => f.Skipped),
        files.Sum(f => f.Rejected),
        files.Sum(f => f.Bytes),
        [.. files.SelectMany(f => f.Issues).Take(ParseStats.MaxIssuesKept)]);
}

/// <summary>
/// One explained outcome: a break, or a match made without a reference that a person should glance at.
/// <see cref="Reason"/> is a complete sentence an operations analyst can act on without opening the files.
/// </summary>
public sealed record ReconItem(
    string Provider,
    ReconStatus Status,
    MatchRule Rule,
    string Reference,
    string? LedgerReference,
    Money? PspAmount,
    Money? PspFee,
    Money? LedgerAmount,
    Money? Difference,
    DateOnly? ValueDate,
    DateOnly? BookingDate,
    int? SettlementLine,
    int? LedgerLine,
    string Reason);

/// <summary>The tolerances a run used, recorded with the report for audit.</summary>
public sealed record ReconSettings(
    long AmountToleranceMinor,
    int AmountToleranceBps,
    long FeeToleranceMinor,
    int DateWindowDays,
    bool AmountAndDateFallback)
{
    public static ReconSettings From(ReconciliationOptions options) => new(
        options.AmountToleranceMinor,
        options.AmountToleranceBps,
        options.FeeToleranceMinor,
        options.DateWindowDays,
        options.AmountAndDateFallback);
}

/// <summary>Outcome counts with one named property per status, so the JSON and storage schema stay explicit.</summary>
public sealed record OutcomeCounts(
    long Matched,
    long MatchedWithFeeDifference,
    long AmountMismatch,
    long MissingInLedger,
    long MissingAtPsp,
    long Duplicate,
    long CurrencyMismatch)
{
    public static OutcomeCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public long Total => Matched + Breaks;

    public long Breaks => MatchedWithFeeDifference + AmountMismatch + MissingInLedger + MissingAtPsp + Duplicate + CurrencyMismatch;

    public long this[ReconStatus status] => status switch
    {
        ReconStatus.Matched => Matched,
        ReconStatus.MatchedWithFeeDifference => MatchedWithFeeDifference,
        ReconStatus.AmountMismatch => AmountMismatch,
        ReconStatus.MissingInLedger => MissingInLedger,
        ReconStatus.MissingAtPsp => MissingAtPsp,
        ReconStatus.Duplicate => Duplicate,
        ReconStatus.CurrencyMismatch => CurrencyMismatch,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static OutcomeCounts From(ReadOnlySpan<long> byStatus) => new(
        byStatus[(int)ReconStatus.Matched],
        byStatus[(int)ReconStatus.MatchedWithFeeDifference],
        byStatus[(int)ReconStatus.AmountMismatch],
        byStatus[(int)ReconStatus.MissingInLedger],
        byStatus[(int)ReconStatus.MissingAtPsp],
        byStatus[(int)ReconStatus.Duplicate],
        byStatus[(int)ReconStatus.CurrencyMismatch]);

    public static OutcomeCounts operator +(OutcomeCounts a, OutcomeCounts b) => new(
        a.Matched + b.Matched,
        a.MatchedWithFeeDifference + b.MatchedWithFeeDifference,
        a.AmountMismatch + b.AmountMismatch,
        a.MissingInLedger + b.MissingInLedger,
        a.MissingAtPsp + b.MissingAtPsp,
        a.Duplicate + b.Duplicate,
        a.CurrencyMismatch + b.CurrencyMismatch);
}

/// <summary>How the paired settlement lines were paired (clean matches and fee differences).</summary>
public sealed record RuleCounts(long Reference, long ReferenceWithinTolerance, long BatchPayout, long AmountAndDate)
{
    public static RuleCounts Empty { get; } = new(0, 0, 0, 0);

    public static RuleCounts From(ReadOnlySpan<long> byRule) => new(
        byRule[(int)MatchRule.Reference],
        byRule[(int)MatchRule.ReferenceWithinTolerance],
        byRule[(int)MatchRule.BatchPayout],
        byRule[(int)MatchRule.AmountAndDate]);

    public static RuleCounts operator +(RuleCounts a, RuleCounts b) => new(
        a.Reference + b.Reference,
        a.ReferenceWithinTolerance + b.ReferenceWithinTolerance,
        a.BatchPayout + b.BatchPayout,
        a.AmountAndDate + b.AmountAndDate);
}
