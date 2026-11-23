using System.ComponentModel.DataAnnotations;

namespace SettlementRecon.SampleData;

/// <summary>What to generate. The same options and seed always produce byte-identical files.</summary>
public sealed record SampleDataOptions
{
    /// <summary>Ledger transactions to generate (settlement lines come out at roughly the same count).</summary>
    [Range(1, 50_000_000)]
    public int Transactions { get; init; } = 10_000;

    public int Seed { get; init; } = 42;

    /// <summary>The booking day. Settlement value dates fall on this day or up to two days later.</summary>
    public DateOnly BookingDate { get; init; } = new(2026, 11, 2);

    /// <summary>Share of transactions that get an injected problem (0.004 = 0.4%).</summary>
    [Range(0.0, 0.5)]
    public double BreakRate { get; init; } = 0.004;

    /// <summary>Player withdrawals per mass payout batch at the bank-transfer provider.</summary>
    [Range(2, 10_000)]
    public int PayoutBatchSize { get; init; } = 40;
}
