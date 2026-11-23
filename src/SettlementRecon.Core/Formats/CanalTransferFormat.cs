using System.Text;
using SettlementRecon.Domain;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;

namespace SettlementRecon.Formats;

/// <summary>
/// "Canal Transfers", a simulated European bank-transfer provider. Semicolon-separated CSV with comma decimals and
/// optional thousands separators (<c>1.234,56</c>), <c>dd.MM.yyyy</c> dates, a signed amount and separate costs.
/// Mass payouts to players settle as a single <c>PAYOUT_BATCH</c> line whose reference is the batch id.
/// Bank rails are known to mangle references (dropped dashes, appended text), which is what the
/// amount-and-date fallback rule exists for.
/// </summary>
/// <example>
/// value_date;reference;description;amount;costs;currency;kind
/// 03.11.2026;TX-00000002;Deposit;1.250,00;0,35;EUR;CREDIT
/// 03.11.2026;PO-20261103-01;Payout batch;-12.345,67;2,50;EUR;PAYOUT_BATCH
/// </example>
public sealed class CanalTransferFormat : ISettlementFormat
{
    public string Provider => "canal";

    public string DisplayName => "Canal Transfers (bank transfers)";

    public string Description => "Semicolon CSV, comma decimals with thousands separators, dd.MM.yyyy dates, batch payouts.";

    public IRecordParser<SettlementRow> CreateParser() => new Parser();

    private sealed class Parser() : CsvRecordParser<SettlementRow>((byte)';', Columns)
    {
        private const int ValueDate = 0;
        private const int ReferenceColumn = 1;
        private const int Amount = 2;
        private const int Costs = 3;
        private const int CurrencyColumn = 4;
        private const int Kind = 5;

        private static readonly CsvColumn[] Columns =
        [
            new("value_date"),
            new("reference"),
            new("amount"),
            new("costs"),
            new("currency"),
            new("kind"),
        ];

        protected override RecordParseStatus ParseRow(in CsvRow row, int lineNumber, Utf8Arena arena, out SettlementRow result, out string? error)
        {
            if (!Utf8Dates.TryParseDayMonthYear(row.Raw(ValueDate), (byte)'.', out var date))
            {
                return Reject($"value_date '{Show(row.Raw(ValueDate))}' is not a dd.MM.yyyy date.", out result, out error);
            }

            var kindText = row.Raw(Kind);
            SettlementKind kind;
            if (Ascii.EqualsIgnoreCase(kindText, "CREDIT"u8))
            {
                kind = SettlementKind.Payment;
            }
            else if (Ascii.EqualsIgnoreCase(kindText, "DEBIT"u8))
            {
                kind = SettlementKind.Payout;
            }
            else if (Ascii.EqualsIgnoreCase(kindText, "PAYOUT_BATCH"u8))
            {
                kind = SettlementKind.BatchPayout;
            }
            else if (Ascii.EqualsIgnoreCase(kindText, "RETURN"u8))
            {
                kind = SettlementKind.Refund;
            }
            else
            {
                return Reject($"kind '{Show(kindText)}' is not CREDIT, DEBIT, PAYOUT_BATCH or RETURN.", out result, out error);
            }

            if (!Currency.TryParse(row.Raw(CurrencyColumn), out var currency))
            {
                return Reject($"currency '{Show(row.Raw(CurrencyColumn))}' is not a supported currency code.", out result, out error);
            }

            if (!MinorUnits.TryParse(row.Raw(Amount), currency.Exponent, DecimalStyle.Comma, out var amount))
            {
                return Reject($"amount '{Show(row.Raw(Amount))}' is not a valid {currency} amount (expected comma decimals).", out result, out error);
            }

            if (!MinorUnits.TryParse(row.Raw(Costs), currency.Exponent, DecimalStyle.Comma, out var costs))
            {
                return Reject($"costs '{Show(row.Raw(Costs))}' is not a valid {currency} amount (expected comma decimals).", out result, out error);
            }

            if ((kind == SettlementKind.Payment && amount < 0) || (kind != SettlementKind.Payment && amount > 0))
            {
                return Reject($"{Show(kindText)} with amount {Show(row.Raw(Amount))} has the wrong sign.", out result, out error);
            }

            if (!TryStoreReference(row.Raw(ReferenceColumn), arena, out var reference))
            {
                return Reject("reference is empty or longer than 128 bytes.", out result, out error);
            }

            result = new SettlementRow(reference, kind, amount, costs, currency, date, lineNumber);
            error = null;
            return RecordParseStatus.Parsed;
        }
    }
}
