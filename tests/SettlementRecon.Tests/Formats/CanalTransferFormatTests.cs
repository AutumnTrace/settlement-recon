using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Formats;

public class CanalTransferFormatTests
{
    private const string Header = "value_date;reference;description;amount;costs;currency;kind\n";

    private static Task<ParsedFile<SettlementRecord>> Read(string text) =>
        RecordFiles.ReadSettlementAsync(TestStreams.FromText(text), new CanalTransferFormat());

    [Test]
    public async Task Reads_comma_decimals_thousands_separators_and_european_dates()
    {
        var file = await Read(Header + "03.11.2026;TX-00000002;Deposit;1.250,00;0,35;EUR;CREDIT\n");

        var record = file.Items.Single();
        Assert.That(record.Gross, Is.EqualTo(new Money(125000, Currency.EUR)));
        Assert.That(record.Fee, Is.EqualTo(new Money(35, Currency.EUR)));
        Assert.That(record.ValueDate, Is.EqualTo(new DateOnly(2026, 11, 3)));
    }

    [Test]
    public async Task Reads_negative_payouts_returns_and_batch_payouts()
    {
        var file = await Read(Header
            + "03.11.2026;WD-1;Withdrawal;-120,00;0,50;EUR;DEBIT\n"
            + "03.11.2026;RT-1;Returned deposit;-35,10;0,00;EUR;RETURN\n"
            + "03.11.2026;PO-20261103-01;\"Payout batch; 42 items\";-12.345,67;2,50;EUR;PAYOUT_BATCH\n");

        Assert.That(file.Stats.Rejected, Is.Zero, string.Join("; ", file.Stats.Issues));
        Assert.That(file.Items.Select(r => (r.Kind, r.Gross.Minor)), Is.EqualTo(new[]
        {
            (SettlementKind.Payout, -12000L),
            (SettlementKind.Refund, -3510L),
            (SettlementKind.BatchPayout, -1234567L),
        }));
    }

    [TestCase("03.11.2026;TX-1;;12.50;0,35;EUR;CREDIT", "expected comma decimals")]
    [TestCase("03.11.2026;TX-1;;-12,50;0,35;EUR;CREDIT", "wrong sign")]
    [TestCase("03.11.2026;TX-1;;12,50;0,35;EUR;DEBIT", "wrong sign")]
    [TestCase("31.02.2026;TX-1;;12,50;0,35;EUR;CREDIT", "dd.MM.yyyy")]
    [TestCase("2026-11-03;TX-1;;12,50;0,35;EUR;CREDIT", "dd.MM.yyyy")]
    [TestCase("03.11.2026;TX-1;;12,50;0,35;EUR;REVERSAL", "kind 'REVERSAL'")]
    public async Task Rejects_lines_that_do_not_follow_the_format(string line, string expected)
    {
        var file = await Read(Header + line + "\n");

        Assert.That(file.Items, Is.Empty);
        Assert.That(file.Stats.Issues.Single().Message, Does.Contain(expected));
    }
}
