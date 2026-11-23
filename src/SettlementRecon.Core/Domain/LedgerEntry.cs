using SettlementRecon.Monetary;

namespace SettlementRecon.Domain;

public enum LedgerEntryType
{
    Deposit,
    Withdrawal,
    Refund,
    Chargeback,
    Adjustment,
}

/// <summary>
/// A transaction from the internal ledger export, as the platform recorded it when the payment happened.
/// </summary>
/// <param name="Reference">The PSP reference the platform sent with the payment (the join key).</param>
/// <param name="ExpectedFee">The fee the platform expects the PSP to charge, when it knows it.</param>
/// <param name="BatchId">Set when the PSP pays this transaction out as part of a mass payout batch.</param>
public sealed record LedgerEntry(
    string Reference,
    string Provider,
    LedgerEntryType Type,
    Money Amount,
    Money? ExpectedFee,
    DateOnly BookingDate,
    string? BatchId,
    int Line);
