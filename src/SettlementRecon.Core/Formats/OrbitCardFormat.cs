using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Formats;

/// <summary>
/// "Orbit Acquiring", a simulated card acquirer. Comma-separated CSV with a header row, dot decimals, ISO dates and
/// separate gross, fee and net columns. Refunds and chargebacks are negative. Free-text descriptions are quoted and
/// may contain commas and escaped quotes. Exports usually start with a UTF-8 byte-order mark.
/// </summary>
/// <example>
/// settlement_date,batch_ref,merchant_ref,description,txn_type,currency,gross_amount,fee_amount,net_amount
/// 2026-11-03,ORB-20261103,TX-00000001,"Deposit, card ****4242",SALE,EUR,25.00,0.55,24.45
/// </example>
public sealed class OrbitCardFormat : ISettlementFormat
{
    public string Provider => "orbit";

    public string DisplayName => "Orbit Acquiring (cards)";

    public string Description => "Comma CSV with header, dot decimals, ISO dates, gross/fee/net columns; refunds negative.";

    public IRecordParser<SettlementRow> CreateParser() => new Parser();

    private sealed class Parser() : CsvRecordParser<SettlementRow>((byte)',', Columns)
    {
        private const int SettlementDate = 0;
        private const int MerchantRef = 1;
        private const int TxnType = 2;
        private const int CurrencyColumn = 3;
        private const int GrossAmount = 4;
        private const int FeeAmount = 5;
        private const int NetAmount = 6;

        private static readonly CsvColumn[] Columns =
        [
            new("settlement_date"),
            new("merchant_ref"),
            new("txn_type"),
            new("currency"),
            new("gross_amount"),
            new("fee_amount"),
            new("net_amount", Required: false),
        ];

        protected override RecordParseStatus ParseRow(in CsvRow row, int lineNumber, Utf8Arena arena, out SettlementRow result, out string? error)
        {
            if (!Utf8Dates.TryParseIsoDate(row.Raw(SettlementDate), out var date))
            {
                return Reject($"settlement_date '{Show(row.Raw(SettlementDate))}' is not a yyyy-MM-dd date.", out result, out error);
            }

            var type = row.Raw(TxnType);
            SettlementKind kind;
            if (Ascii.EqualsIgnoreCase(type, "SALE"u8))
            {
                kind = SettlementKind.Payment;
            }
            else if (Ascii.EqualsIgnoreCase(type, "REFUND"u8))
            {
                kind = SettlementKind.Refund;
            }
            else if (Ascii.EqualsIgnoreCase(type, "CHARGEBACK"u8))
            {
                kind = SettlementKind.Chargeback;
            }
            else
            {
                return Reject($"txn_type '{Show(type)}' is not SALE, REFUND or CHARGEBACK.", out result, out error);
            }

            if (!Currency.TryParse(row.Raw(CurrencyColumn), out var currency))
            {
                return Reject($"currency '{Show(row.Raw(CurrencyColumn))}' is not a supported currency code.", out result, out error);
            }

            if (!MinorUnits.TryParse(row.Raw(GrossAmount), currency.Exponent, DecimalStyle.Dot, out var gross))
            {
                return Reject($"gross_amount '{Show(row.Raw(GrossAmount))}' is not a valid {currency} amount.", out result, out error);
            }

            if (!MinorUnits.TryParse(row.Raw(FeeAmount), currency.Exponent, DecimalStyle.Dot, out var fee))
            {
                return Reject($"fee_amount '{Show(row.Raw(FeeAmount))}' is not a valid {currency} amount.", out result, out error);
            }

            if (kind != SettlementKind.Payment && gross > 0)
            {
                return Reject($"{Show(type)} with positive gross_amount {Show(row.Raw(GrossAmount))}; refunds and chargebacks must be negative.", out result, out error);
            }

            if (row.Has(NetAmount))
            {
                if (!MinorUnits.TryParse(row.Raw(NetAmount), currency.Exponent, DecimalStyle.Dot, out var net))
                {
                    return Reject($"net_amount '{Show(row.Raw(NetAmount))}' is not a valid {currency} amount.", out result, out error);
                }

                if (net != gross - fee)
                {
                    return Reject(
                        $"net_amount {Show(row.Raw(NetAmount))} does not equal gross_amount minus fee_amount ({MinorUnits.Format(gross - fee, currency.Exponent)}).",
                        out result,
                        out error);
                }
            }

            if (!TryStoreReference(row.Raw(MerchantRef), arena, out var reference))
            {
                return Reject("merchant_ref is empty or longer than 128 bytes.", out result, out error);
            }

            result = new SettlementRow(reference, kind, gross, fee, currency, date, lineNumber);
            error = null;
            return RecordParseStatus.Parsed;
        }
    }
}
