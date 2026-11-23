using System.Text;
using System.Text.Json;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Ledger;

public enum LedgerFileFormat
{
    Csv,
    JsonLines,
}

/// <summary>
/// The internal ledger export: one line per transaction with the PSP reference, provider id, signed amount,
/// optional expected fee, booking timestamp and optional payout batch id. Available as CSV or JSON Lines with
/// the same field names.
/// </summary>
/// <example>
/// reference,provider,type,amount,currency,expected_fee,booked_at,batch_id
/// TX-00000001,orbit,deposit,25.00,EUR,0.55,2026-11-02T10:15:00Z,
/// </example>
public static class LedgerFormats
{
    public static LedgerFileFormat FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => LedgerFileFormat.Csv,
        ".jsonl" or ".ndjson" => LedgerFileFormat.JsonLines,
        var other => throw new NotSupportedException($"Ledger file '{Path.GetFileName(path)}' has extension '{other}'; use .csv or .jsonl."),
    };

    public static IRecordParser<LedgerRow> CreateParser(LedgerFileFormat format) => format switch
    {
        LedgerFileFormat.Csv => new LedgerCsvParser(),
        LedgerFileFormat.JsonLines => new LedgerJsonLinesParser(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static bool TryParseType(ReadOnlySpan<byte> text, out LedgerEntryType type)
    {
        type = default;
        if (Ascii.EqualsIgnoreCase(text, "deposit"u8))
        {
            type = LedgerEntryType.Deposit;
        }
        else if (Ascii.EqualsIgnoreCase(text, "withdrawal"u8))
        {
            type = LedgerEntryType.Withdrawal;
        }
        else if (Ascii.EqualsIgnoreCase(text, "refund"u8))
        {
            type = LedgerEntryType.Refund;
        }
        else if (Ascii.EqualsIgnoreCase(text, "chargeback"u8))
        {
            type = LedgerEntryType.Chargeback;
        }
        else if (Ascii.EqualsIgnoreCase(text, "adjustment"u8))
        {
            type = LedgerEntryType.Adjustment;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>Money in is positive, money out negative; adjustments may go either way.</summary>
    internal static bool HasValidSign(LedgerEntryType type, long amount) => type switch
    {
        LedgerEntryType.Deposit => amount >= 0,
        LedgerEntryType.Adjustment => true,
        _ => amount <= 0,
    };
}

internal sealed class LedgerCsvParser() : CsvRecordParser<LedgerRow>((byte)',', Columns)
{
    private const int ReferenceColumn = 0;
    private const int ProviderColumn = 1;
    private const int TypeColumn = 2;
    private const int AmountColumn = 3;
    private const int CurrencyColumn = 4;
    private const int ExpectedFeeColumn = 5;
    private const int BookedAtColumn = 6;
    private const int BatchIdColumn = 7;

    private static readonly CsvColumn[] Columns =
    [
        new("reference"),
        new("provider"),
        new("type"),
        new("amount"),
        new("currency"),
        new("expected_fee", Required: false),
        new("booked_at"),
        new("batch_id", Required: false),
    ];

    protected override RecordParseStatus ParseRow(in CsvRow row, int lineNumber, Utf8Arena arena, out LedgerRow result, out string? error)
    {
        if (!LedgerFormats.TryParseType(row.Raw(TypeColumn), out var type))
        {
            return Reject($"type '{Show(row.Raw(TypeColumn))}' is not deposit, withdrawal, refund, chargeback or adjustment.", out result, out error);
        }

        if (!Currency.TryParse(row.Raw(CurrencyColumn), out var currency))
        {
            return Reject($"currency '{Show(row.Raw(CurrencyColumn))}' is not a supported currency code.", out result, out error);
        }

        if (!MinorUnits.TryParse(row.Raw(AmountColumn), currency.Exponent, DecimalStyle.Dot, out var amount))
        {
            return Reject($"amount '{Show(row.Raw(AmountColumn))}' is not a valid {currency} amount.", out result, out error);
        }

        if (!LedgerFormats.HasValidSign(type, amount))
        {
            return Reject($"{Show(row.Raw(TypeColumn))} with amount {Show(row.Raw(AmountColumn))} has the wrong sign.", out result, out error);
        }

        var feeText = row.Raw(ExpectedFeeColumn);
        long fee = 0;
        if (!feeText.IsEmpty && !MinorUnits.TryParse(feeText, currency.Exponent, DecimalStyle.Dot, out fee))
        {
            return Reject($"expected_fee '{Show(feeText)}' is not a valid {currency} amount.", out result, out error);
        }

        if (!Utf8Dates.TryParseUtcDate(row.Raw(BookedAtColumn), out var booked))
        {
            return Reject($"booked_at '{Show(row.Raw(BookedAtColumn))}' is not an ISO-8601 date or timestamp.", out result, out error);
        }

        var providerText = row.Raw(ProviderColumn);
        if (providerText.IsEmpty)
        {
            return Reject("provider is empty.", out result, out error);
        }

        if (!TryStoreReference(row.Raw(ReferenceColumn), arena, out var reference))
        {
            return Reject("reference is empty or longer than 128 bytes.", out result, out error);
        }

        var batchText = row.Raw(BatchIdColumn);
        if (batchText.Length > MaxReferenceBytes)
        {
            return Reject("batch_id is longer than 128 bytes.", out result, out error);
        }

        result = new LedgerRow(
            reference,
            arena.Append(providerText),
            type,
            amount,
            fee,
            !feeText.IsEmpty,
            currency,
            booked,
            arena.Append(batchText),
            lineNumber);
        error = null;
        return RecordParseStatus.Parsed;
    }
}

internal sealed class LedgerJsonLinesParser : IRecordParser<LedgerRow>
{
    private const int MaxText = CsvRecordParser<LedgerRow>.MaxReferenceBytes;

    public bool HasHeader => false;

    public void BindHeader(ReadOnlySpan<byte> header)
    {
    }

    public RecordParseStatus Parse(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out LedgerRow row, out string? error)
    {
        row = default;
        try
        {
            return ParseCore(line, lineNumber, arena, out row, out error);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return RecordParseStatus.Rejected;
        }
    }

    private static RecordParseStatus ParseCore(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out LedgerRow row, out string? error)
    {
        row = default;
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "Line is not a JSON object.";
            return RecordParseStatus.Rejected;
        }

        Span<byte> referenceBuffer = stackalloc byte[MaxText];
        Span<byte> providerBuffer = stackalloc byte[MaxText];
        Span<byte> batchBuffer = stackalloc byte[MaxText];
        Span<byte> amountBuffer = stackalloc byte[48];
        Span<byte> feeBuffer = stackalloc byte[48];
        scoped ReadOnlySpan<byte> reference = default;
        scoped ReadOnlySpan<byte> provider = default;
        scoped ReadOnlySpan<byte> batch = default;
        scoped ReadOnlySpan<byte> amount = default;
        scoped ReadOnlySpan<byte> fee = default;
        LedgerEntryType type = default;
        Currency currency = default;
        DateOnly booked = default;
        bool hasType = false, hasBooked = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("reference"u8))
            {
                reader.Read();
                JsonValues.TryRead(in reader, referenceBuffer, out reference);
            }
            else if (reader.ValueTextEquals("provider"u8))
            {
                reader.Read();
                JsonValues.TryRead(in reader, providerBuffer, out provider);
            }
            else if (reader.ValueTextEquals("batch_id"u8))
            {
                reader.Read();
                JsonValues.TryRead(in reader, batchBuffer, out batch);
            }
            else if (reader.ValueTextEquals("amount"u8))
            {
                reader.Read();
                JsonValues.TryRead(in reader, amountBuffer, out amount);
            }
            else if (reader.ValueTextEquals("expected_fee"u8))
            {
                reader.Read();
                JsonValues.TryRead(in reader, feeBuffer, out fee);
            }
            else if (reader.ValueTextEquals("currency"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String || !Currency.TryParse(reader.ValueSpan, out currency))
                {
                    error = "currency is not a supported currency code.";
                    return RecordParseStatus.Rejected;
                }
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                hasType = reader.TokenType == JsonTokenType.String && LedgerFormats.TryParseType(reader.ValueSpan, out type);
            }
            else if (reader.ValueTextEquals("booked_at"u8))
            {
                reader.Read();
                hasBooked = reader.TokenType == JsonTokenType.String && Utf8Dates.TryParseUtcDate(reader.ValueSpan, out booked);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (reference.IsEmpty || provider.IsEmpty || amount.IsEmpty || currency.IsDefault || !hasType || !hasBooked)
        {
            error = "A ledger line needs reference, provider, a known type, amount, currency and a valid booked_at.";
            return RecordParseStatus.Rejected;
        }

        if (!MinorUnits.TryParse(amount, currency.Exponent, DecimalStyle.Dot, out var minor) || !LedgerFormats.HasValidSign(type, minor))
        {
            error = $"amount '{Encoding.UTF8.GetString(amount)}' is not a valid {currency} amount for a {type}.";
            return RecordParseStatus.Rejected;
        }

        long expectedFee = 0;
        if (!fee.IsEmpty && !MinorUnits.TryParse(fee, currency.Exponent, DecimalStyle.Dot, out expectedFee))
        {
            error = $"expected_fee '{Encoding.UTF8.GetString(fee)}' is not a valid {currency} amount.";
            return RecordParseStatus.Rejected;
        }

        row = new LedgerRow(
            arena.Append(reference),
            arena.Append(provider),
            type,
            minor,
            expectedFee,
            !fee.IsEmpty,
            currency,
            booked,
            arena.Append(batch),
            lineNumber);
        error = null;
        return RecordParseStatus.Parsed;
    }
}
