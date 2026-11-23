using System.Globalization;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;

namespace SettlementRecon.Matching;

/// <summary>
/// Builds the human-readable reason for every break. Each reason states what each side says, the difference,
/// and the rule or tolerance that decided it, so nobody has to re-derive the verdict from raw files.
/// </summary>
internal static class BreakReasons
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string AmountMismatch(string reference, Money psp, Money ledger, long tolerance)
    {
        var difference = psp - ledger;
        var percent = ledger.Minor == 0 ? string.Empty : string.Create(Invariant, $" ({(decimal)difference.Minor / ledger.Minor * 100:+0.00;-0.00}%)");
        return string.Create(Invariant,
            $"PSP settled {psp} for {reference} but the ledger booked {ledger}: a difference of {difference.ToSignedString()}{percent}, outside the tolerance of {new Money(tolerance, ledger.Currency)}.");
    }

    public static string FeeDifference(string reference, Money psp, Money pspFee, Money expectedFee, long amountDifference)
    {
        var feeDifference = pspFee - expectedFee;
        var amount = amountDifference == 0
            ? $"Amount agrees at {psp}"
            : $"Amount agrees within tolerance ({new Money(amountDifference, psp.Currency).ToSignedString()})";
        return $"{amount}, but the PSP charged a fee of {pspFee} on {reference} where the ledger expected {expectedFee} ({feeDifference.ToSignedString()}).";
    }

    public static string CurrencyMismatch(string reference, Money psp, Money ledger) =>
        $"Ledger booked {ledger} for {reference} but the PSP settled it in {psp.Currency.Code} ({psp}); the currencies must agree before amounts can be compared.";

    public static string SettledTwice(string reference, Money psp, int line, int firstLine, int ledgerLine) =>
        $"{reference} is settled twice: line {firstLine} already claimed ledger line {ledgerLine}, and line {line} settles another {psp}. Paying it again would double-count the transaction.";

    public static string SettledTwiceInBatch(string reference, Money psp, int line, string batchId) =>
        $"{reference} was already settled as part of payout batch {batchId}, and line {line} settles it again for {psp}.";

    public static string BatchSettledTwice(string batchId, Money psp, int line, int firstLine) =>
        $"Payout batch {batchId} is settled twice: line {firstLine} already matched it, and line {line} settles another {psp}.";

    public static string DuplicateInSettlement(string reference, Money psp, int line, int firstLine) =>
        $"{reference} appears twice in the settlement file (lines {firstLine} and {line}) and is not in the ledger; this second line settles another {psp}.";

    public static string DuplicateInLedger(string reference, Money amount, int line, int firstLine) =>
        $"The ledger contains {reference} twice (lines {firstLine} and {line}); the second entry of {amount} was excluded from matching. One of the two bookings is likely wrong.";

    public static string BatchMismatch(string batchId, int count, Money ledgerTotal, Money psp, long tolerance)
    {
        var difference = psp - ledgerTotal;
        return $"Payout batch {batchId} covers {count} ledger {Plural(count, "entry", "entries")} totalling {ledgerTotal}, but the PSP settled {psp}: a difference of {difference.ToSignedString()}, outside the tolerance of {new Money(tolerance, ledgerTotal.Currency)}.";
    }

    public static string BatchCurrencyMismatch(string batchId, int count, Money ledgerTotal, Money psp, bool mixed) => mixed
        ? $"Payout batch {batchId} groups {count} ledger entries in more than one currency, so it cannot be compared with the PSP line of {psp}."
        : $"Payout batch {batchId} covers {count} ledger {Plural(count, "entry", "entries")} in {ledgerTotal.Currency.Code} but the PSP settled it in {psp.Currency.Code} ({psp}).";

    public static string MissingInLedger(SettlementRecord line, int ambiguousCandidates, int windowDays, bool fallbackEnabled)
    {
        var what = $"PSP settled {line.Gross} ({Describe(line.Kind)}, value date {line.ValueDate:yyyy-MM-dd}) for {line.Reference}, but no ledger entry has that reference";
        if (!fallbackEnabled)
        {
            return what + ".";
        }

        return ambiguousCandidates > 1
            ? $"{what}. {ambiguousCandidates} unmatched ledger entries have the same amount within {windowDays} {Plural(windowDays, "day", "days")}, so none was paired automatically."
            : $"{what} and no unmatched ledger entry has the same amount within {windowDays} {Plural(windowDays, "day", "days")}.";
    }

    public static string MissingAtPsp(LedgerEntry entry, string? batchId) => batchId is null
        ? $"Ledger booked a {Describe(entry.Type)} of {entry.Amount} on {entry.BookingDate:yyyy-MM-dd} for {entry.Reference}, but the PSP settlement has no line for it."
        : $"Ledger booked a {Describe(entry.Type)} of {entry.Amount} on {entry.BookingDate:yyyy-MM-dd} for {entry.Reference} in payout batch {batchId}, but the PSP settled neither the entry nor the batch.";

    public static string AmountAndDate(SettlementRecord line, LedgerEntry entry, long amountDifference)
    {
        var days = Math.Abs(line.ValueDate.DayNumber - entry.BookingDate.DayNumber);
        var amount = amountDifference == 0
            ? $"amount {line.Gross}"
            : $"amount {line.Gross} (within tolerance, {new Money(amountDifference, line.Gross.Currency).ToSignedString()})";
        var dates = days == 0 ? "the same date" : $"dates {days} {Plural(days, "day", "days")} apart";
        return $"No ledger entry has reference {line.Reference}; paired with ledger entry {entry.Reference} (line {entry.Line}) on {amount} and {dates}. Check that the PSP truncated or reformatted the reference.";
    }

    private static string Describe(SettlementKind kind) => kind switch
    {
        SettlementKind.Payment => "payment",
        SettlementKind.Refund => "refund",
        SettlementKind.Chargeback => "chargeback",
        SettlementKind.Payout => "payout",
        SettlementKind.BatchPayout => "batch payout",
        _ => "adjustment",
    };

    private static string Describe(LedgerEntryType type) => type switch
    {
        LedgerEntryType.Deposit => "deposit",
        LedgerEntryType.Withdrawal => "withdrawal",
        LedgerEntryType.Refund => "refund",
        LedgerEntryType.Chargeback => "chargeback",
        _ => "adjustment",
    };

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
