using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;

namespace SettlementRecon.Parsing;

/// <summary>
/// Hot-path form of a settlement line: amounts in minor units, text as arena slices. It holds no object references,
/// so a batch of these is one contiguous array the GC never has to scan.
/// </summary>
public readonly record struct SettlementRow(
    Utf8Slice Reference,
    SettlementKind Kind,
    long Gross,
    long Fee,
    Currency Currency,
    DateOnly ValueDate,
    int Line)
{
    public SettlementRecord ToRecord(Utf8Arena arena, string provider) => new(
        provider,
        Encoding.UTF8.GetString(arena[Reference]),
        Kind,
        new Money(Gross, Currency),
        new Money(Fee, Currency),
        ValueDate,
        Line);
}

/// <summary>Hot-path form of a ledger entry. <see cref="ExpectedFee"/> is only meaningful when <see cref="HasExpectedFee"/>.</summary>
public readonly record struct LedgerRow(
    Utf8Slice Reference,
    Utf8Slice Provider,
    LedgerEntryType Type,
    long Amount,
    long ExpectedFee,
    bool HasExpectedFee,
    Currency Currency,
    DateOnly BookingDate,
    Utf8Slice BatchId,
    int Line)
{
    public LedgerEntry ToEntry(Utf8Arena arena) => new(
        Encoding.UTF8.GetString(arena[Reference]),
        Encoding.UTF8.GetString(arena[Provider]),
        Type,
        new Money(Amount, Currency),
        HasExpectedFee ? new Money(ExpectedFee, Currency) : null,
        BookingDate,
        BatchId.IsEmpty ? null : Encoding.UTF8.GetString(arena[BatchId]),
        Line);
}
