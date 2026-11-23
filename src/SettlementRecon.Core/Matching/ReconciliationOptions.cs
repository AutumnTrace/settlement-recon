using System.ComponentModel.DataAnnotations;

namespace SettlementRecon.Matching;

/// <summary>Matching tolerances and pipeline sizing. Bound from configuration in the worker.</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>Absolute amount tolerance in minor units (FX rounding). The larger of this and the bps tolerance applies.</summary>
    [Range(0, 1_000_000)]
    public long AmountToleranceMinor { get; set; } = 1;

    /// <summary>Relative amount tolerance in basis points of the ledger amount (5 = 0.05%).</summary>
    [Range(0, 1_000)]
    public int AmountToleranceBps { get; set; } = 5;

    /// <summary>How far the charged fee may drift from the expected fee before it is flagged, in minor units.</summary>
    [Range(0, 1_000_000)]
    public long FeeToleranceMinor { get; set; } = 1;

    /// <summary>Maximum distance between booking date and value date for the amount-and-date fallback rule.</summary>
    [Range(0, 31)]
    public int DateWindowDays { get; set; } = 3;

    /// <summary>Pair leftovers by amount and date when the reference does not match (typical for mangled bank references).</summary>
    public bool AmountAndDateFallback { get; set; } = true;

    /// <summary>Rows per batch handed from a parser to a matcher through the channel.</summary>
    [Range(64, 65_536)]
    public int BatchSize { get; set; } = 4_096;

    /// <summary>Batches buffered per channel before the parser waits for the matcher (backpressure).</summary>
    [Range(1, 1_024)]
    public int ChannelCapacity { get; set; } = 8;

    /// <summary>The tolerance that applies to a given ledger amount.</summary>
    public long AmountToleranceFor(long amount)
    {
        var magnitude = amount < 0 ? -(Int128)amount : amount;
        var relative = (long)(magnitude * AmountToleranceBps / 10_000);
        return Math.Max(AmountToleranceMinor, relative);
    }
}
