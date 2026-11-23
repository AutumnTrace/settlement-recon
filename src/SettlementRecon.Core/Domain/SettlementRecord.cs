using SettlementRecon.Monetary;

namespace SettlementRecon.Domain;

/// <summary>What a settlement line represents.</summary>
public enum SettlementKind
{
    Payment,
    Refund,
    Chargeback,
    Payout,

    /// <summary>One line that settles many ledger transactions at once (a mass payout).</summary>
    BatchPayout,
    Adjustment,
}

/// <summary>
/// One line of a PSP settlement report, normalised from whatever format the provider uses.
/// Amounts are signed from the operator's point of view: money in is positive, refunds and payouts are negative.
/// The fee is what the PSP charged and is normally positive; <see cref="Net"/> is what actually moved.
/// </summary>
public sealed record SettlementRecord(
    string Provider,
    string Reference,
    SettlementKind Kind,
    Money Gross,
    Money Fee,
    DateOnly ValueDate,
    int Line)
{
    public Money Net => Gross - Fee;
}
