using System.Text;
using System.Text.Json;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Formats;

/// <summary>
/// "Hashrail", a simulated stablecoin processor. JSON Lines, one transfer per line, read with
/// <see cref="Utf8JsonReader"/> straight from the line span. Amounts are unsigned with a <c>direction</c> field,
/// so the parser applies the sign. The network fee is the fee. Only <c>confirmed</c> transfers settle;
/// pending and failed ones are skipped and counted.
/// </summary>
/// <example>
/// {"id":"hr_01","merchant_reference":"TX-00000003","direction":"in","asset":"USDT","network":"tron","amount":"125.500000","network_fee":"1.000000","status":"confirmed","confirmed_at":"2026-11-03T10:22:11Z"}
/// </example>
public sealed class HashrailFormat : ISettlementFormat
{
    public string Provider => "hashrail";

    public string DisplayName => "Hashrail (stablecoins)";

    public string Description => "JSON Lines with unsigned amounts plus direction, network fees, ISO timestamps; only confirmed transfers settle.";

    public IRecordParser<SettlementRow> CreateParser() => new Parser();

    private sealed class Parser : IRecordParser<SettlementRow>
    {
        public bool HasHeader => false;

        public void BindHeader(ReadOnlySpan<byte> header)
        {
        }

        public RecordParseStatus Parse(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out SettlementRow row, out string? error)
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

        private static RecordParseStatus ParseCore(ReadOnlySpan<byte> line, int lineNumber, Utf8Arena arena, out SettlementRow row, out string? error)
        {
            row = default;
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "Line is not a JSON object.";
                return RecordParseStatus.Rejected;
            }

            Span<byte> referenceBuffer = stackalloc byte[CsvRecordParser<SettlementRow>.MaxReferenceBytes];
            Span<byte> amountBuffer = stackalloc byte[48];
            Span<byte> feeBuffer = stackalloc byte[48];
            scoped ReadOnlySpan<byte> reference = default;
            scoped ReadOnlySpan<byte> amount = default;
            scoped ReadOnlySpan<byte> fee = default;
            Currency currency = default;
            DateOnly date = default;
            bool hasDate = false, hasStatus = false, confirmed = false, outbound = false, hasDirection = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    error = "Unexpected JSON token.";
                    return RecordParseStatus.Rejected;
                }

                if (reader.ValueTextEquals("merchant_reference"u8))
                {
                    reader.Read();
                    if (!JsonValues.TryRead(in reader, referenceBuffer, out reference))
                    {
                        error = "merchant_reference is missing or longer than 128 bytes.";
                        return RecordParseStatus.Rejected;
                    }
                }
                else if (reader.ValueTextEquals("amount"u8))
                {
                    reader.Read();
                    if (!JsonValues.TryRead(in reader, amountBuffer, out amount))
                    {
                        error = "amount must be a string or number.";
                        return RecordParseStatus.Rejected;
                    }
                }
                else if (reader.ValueTextEquals("network_fee"u8))
                {
                    reader.Read();
                    if (!JsonValues.TryRead(in reader, feeBuffer, out fee))
                    {
                        error = "network_fee must be a string or number.";
                        return RecordParseStatus.Rejected;
                    }
                }
                else if (reader.ValueTextEquals("asset"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String || !Currency.TryParse(reader.ValueSpan, out currency))
                    {
                        error = "asset is not a supported currency code.";
                        return RecordParseStatus.Rejected;
                    }
                }
                else if (reader.ValueTextEquals("status"u8))
                {
                    reader.Read();
                    hasStatus = true;
                    confirmed = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("confirmed"u8);
                }
                else if (reader.ValueTextEquals("direction"u8))
                {
                    reader.Read();
                    hasDirection = reader.TokenType == JsonTokenType.String && (reader.ValueTextEquals("in"u8) || reader.ValueTextEquals("out"u8));
                    outbound = hasDirection && reader.ValueTextEquals("out"u8);
                }
                else if (reader.ValueTextEquals("confirmed_at"u8))
                {
                    reader.Read();
                    hasDate = reader.TokenType == JsonTokenType.String && Utf8Dates.TryParseUtcDate(reader.ValueSpan, out date);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (!hasStatus)
            {
                error = "status is missing.";
                return RecordParseStatus.Rejected;
            }

            if (!confirmed)
            {
                error = null;
                return RecordParseStatus.Skipped;
            }

            if (reference.IsEmpty || currency.IsDefault || amount.IsEmpty || !hasDate || !hasDirection)
            {
                error = "A confirmed transfer needs merchant_reference, asset, amount, direction (in/out) and a valid confirmed_at.";
                return RecordParseStatus.Rejected;
            }

            if (!MinorUnits.TryParse(amount, currency.Exponent, DecimalStyle.Dot, out var gross) || gross < 0)
            {
                error = $"amount '{Encoding.UTF8.GetString(amount)}' is not a valid unsigned {currency} amount.";
                return RecordParseStatus.Rejected;
            }

            long networkFee = 0;
            if (!fee.IsEmpty && !MinorUnits.TryParse(fee, currency.Exponent, DecimalStyle.Dot, out networkFee))
            {
                error = $"network_fee '{Encoding.UTF8.GetString(fee)}' is not a valid {currency} amount.";
                return RecordParseStatus.Rejected;
            }

            var kind = outbound ? SettlementKind.Payout : SettlementKind.Payment;
            row = new SettlementRow(arena.Append(reference), kind, outbound ? -gross : gross, networkFee, currency, date, lineNumber);
            error = null;
            return RecordParseStatus.Parsed;
        }
    }
}
