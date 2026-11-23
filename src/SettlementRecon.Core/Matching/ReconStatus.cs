namespace SettlementRecon.Matching;

/// <summary>The outcome of reconciling one settlement line or ledger entry.</summary>
public enum ReconStatus
{
    /// <summary>Both sides agree (exactly, or within the configured tolerance).</summary>
    Matched,

    /// <summary>The amount agrees, but the PSP charged a different fee than the ledger expected.</summary>
    MatchedWithFeeDifference,

    /// <summary>Same reference, different amount, outside the tolerance.</summary>
    AmountMismatch,

    /// <summary>The PSP settled something the ledger has no record of.</summary>
    MissingInLedger,

    /// <summary>The ledger booked something the PSP never settled.</summary>
    MissingAtPsp,

    /// <summary>The same reference appears twice on one side.</summary>
    Duplicate,

    /// <summary>Same reference, but the two sides disagree on the currency.</summary>
    CurrencyMismatch,
}

/// <summary>Which rule produced a pairing.</summary>
public enum MatchRule
{
    /// <summary>No pairing was made.</summary>
    None,

    /// <summary>Reference matched and the amounts are identical.</summary>
    Reference,

    /// <summary>Reference matched and the amounts differ by no more than the FX/rounding tolerance.</summary>
    ReferenceWithinTolerance,

    /// <summary>One settlement line matched a payout batch of many ledger entries.</summary>
    BatchPayout,

    /// <summary>No reference match; paired by amount and value date within the window. Always worth a look.</summary>
    AmountAndDate,
}

public static class ReconStatusExtensions
{
    /// <summary>Everything except a clean match needs a person to look at it.</summary>
    public static bool IsBreak(this ReconStatus status) => status != ReconStatus.Matched;

    public static string ToDisplayName(this ReconStatus status) => status switch
    {
        ReconStatus.Matched => "Matched",
        ReconStatus.MatchedWithFeeDifference => "Matched with fee difference",
        ReconStatus.AmountMismatch => "Amount mismatch",
        ReconStatus.MissingInLedger => "Missing in ledger",
        ReconStatus.MissingAtPsp => "Missing at PSP",
        ReconStatus.Duplicate => "Duplicate",
        ReconStatus.CurrencyMismatch => "Currency mismatch",
        _ => status.ToString(),
    };

    public static string ToDisplayName(this MatchRule rule) => rule switch
    {
        MatchRule.None => "none",
        MatchRule.Reference => "reference",
        MatchRule.ReferenceWithinTolerance => "reference, within tolerance",
        MatchRule.BatchPayout => "batch payout",
        MatchRule.AmountAndDate => "amount and date",
        _ => rule.ToString(),
    };
}
