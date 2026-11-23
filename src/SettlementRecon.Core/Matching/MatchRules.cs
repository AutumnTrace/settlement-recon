using SettlementRecon.Monetary;

namespace SettlementRecon.Matching;

/// <summary>The result of comparing a settlement line with the ledger entry that has the same reference.</summary>
/// <param name="AmountDifference">PSP gross minus ledger amount, in minor units.</param>
/// <param name="FeeDifference">PSP fee minus expected fee, in minor units (0 when no fee was expected).</param>
/// <param name="Tolerance">The amount tolerance that applied, in minor units.</param>
public readonly record struct AmountVerdict(
    ReconStatus Status,
    MatchRule Rule,
    long AmountDifference,
    long FeeDifference,
    long Tolerance);

/// <summary>
/// Pure comparison rules shared by every matcher. They work on primitives so the hot path can call
/// them without materialising records, and they decide the status only. The wording lives in <c>BreakReasons</c>.
/// </summary>
public static class MatchRules
{
    public static AmountVerdict Compare(
        long ledgerAmount,
        long? expectedFee,
        Currency ledgerCurrency,
        long pspGross,
        long pspFee,
        Currency pspCurrency,
        ReconciliationOptions options)
    {
        if (ledgerCurrency != pspCurrency)
        {
            return new AmountVerdict(ReconStatus.CurrencyMismatch, MatchRule.None, 0, 0, 0);
        }

        var difference = pspGross - ledgerAmount;
        var tolerance = options.AmountToleranceFor(ledgerAmount);
        if (Math.Abs(difference) > tolerance)
        {
            return new AmountVerdict(ReconStatus.AmountMismatch, MatchRule.None, difference, 0, tolerance);
        }

        var rule = difference == 0 ? MatchRule.Reference : MatchRule.ReferenceWithinTolerance;
        if (expectedFee is { } fee)
        {
            var feeDifference = pspFee - fee;
            if (Math.Abs(feeDifference) > options.FeeToleranceMinor)
            {
                return new AmountVerdict(ReconStatus.MatchedWithFeeDifference, rule, difference, feeDifference, tolerance);
            }
        }

        return new AmountVerdict(ReconStatus.Matched, rule, difference, 0, tolerance);
    }

    /// <summary>
    /// Compares a batch payout line with the ledger entries that carry its batch id.
    /// The fee is not checked: payout fees are per batch and the ledger does not book them per entry.
    /// </summary>
    public static AmountVerdict CompareBatch(
        long ledgerTotal,
        Currency ledgerCurrency,
        bool ledgerHasMixedCurrencies,
        long pspGross,
        Currency pspCurrency,
        ReconciliationOptions options)
    {
        if (ledgerHasMixedCurrencies || ledgerCurrency != pspCurrency)
        {
            return new AmountVerdict(ReconStatus.CurrencyMismatch, MatchRule.None, 0, 0, 0);
        }

        var difference = pspGross - ledgerTotal;
        var tolerance = options.AmountToleranceFor(ledgerTotal);
        return Math.Abs(difference) > tolerance
            ? new AmountVerdict(ReconStatus.AmountMismatch, MatchRule.None, difference, 0, tolerance)
            : new AmountVerdict(ReconStatus.Matched, MatchRule.BatchPayout, difference, 0, tolerance);
    }

    /// <summary>Whether a leftover settlement line and ledger entry are close enough to pair without a reference.</summary>
    public static bool IsAmountAndDateCandidate(
        Money pspGross,
        DateOnly valueDate,
        Money ledgerAmount,
        DateOnly bookingDate,
        ReconciliationOptions options)
    {
        if (pspGross.Currency != ledgerAmount.Currency)
        {
            return false;
        }

        var days = Math.Abs(valueDate.DayNumber - bookingDate.DayNumber);
        return days <= options.DateWindowDays
            && Math.Abs(pspGross.Minor - ledgerAmount.Minor) <= options.AmountToleranceFor(ledgerAmount.Minor);
    }
}
