using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Formats;

public class OrbitCardFormatTests
{
    private const string Header = "settlement_date,batch_ref,merchant_ref,description,txn_type,currency,gross_amount,fee_amount,net_amount\n";

    private static Task<ParsedFile<SettlementRecord>> Read(string text, bool withBom = false) =>
        RecordFiles.ReadSettlementAsync(TestStreams.FromText(text, withBom), new OrbitCardFormat());

    [Test]
    public async Task Parses_sales_with_a_bom_and_quoted_descriptions()
    {
        var file = await Read(
            Header + "2026-11-03,ORB-1,TX-00000001,\"Deposit, card ****4242, \"\"VIP\"\"\",SALE,EUR,25.00,0.55,24.45\n",
            withBom: true);

        Assert.That(file.Stats.Rejected, Is.Zero, string.Join("; ", file.Stats.Issues));
        var record = file.Items.Single();
        Assert.That(record.Reference, Is.EqualTo("TX-00000001"));
        Assert.That(record.Kind, Is.EqualTo(SettlementKind.Payment));
        Assert.That(record.Gross, Is.EqualTo(new Money(2500, Currency.EUR)));
        Assert.That(record.Fee, Is.EqualTo(new Money(55, Currency.EUR)));
        Assert.That(record.Net, Is.EqualTo(new Money(2445, Currency.EUR)));
        Assert.That(record.ValueDate, Is.EqualTo(new DateOnly(2026, 11, 3)));
        Assert.That(record.Line, Is.EqualTo(2));
    }

    [Test]
    public async Task Refunds_and_chargebacks_are_negative()
    {
        var file = await Read(Header
            + "2026-11-03,ORB-1,RF-1,Refund,REFUND,EUR,-10.00,0.00,-10.00\n"
            + "2026-11-03,ORB-1,CB-1,Chargeback,chargeback,GBP,-49.99,15.00,-64.99\n");

        Assert.That(file.Items.Select(r => (r.Kind, r.Gross.Minor, r.Net.Minor)), Is.EqualTo(new[]
        {
            (SettlementKind.Refund, -1000L, -1000L),
            (SettlementKind.Chargeback, -4999L, -6499L),
        }));
    }

    [Test]
    public async Task A_refund_with_a_positive_amount_is_rejected_with_a_reason()
    {
        var file = await Read(Header + "2026-11-03,ORB-1,RF-1,Refund,REFUND,EUR,10.00,0.00,10.00\n");

        Assert.That(file.Items, Is.Empty);
        Assert.That(file.Stats.Issues.Single().Message, Does.Contain("must be negative"));
        Assert.That(file.Stats.Issues.Single().Line, Is.EqualTo(2));
    }

    [Test]
    public async Task Net_must_equal_gross_minus_fee()
    {
        var file = await Read(Header + "2026-11-03,ORB-1,TX-1,,SALE,EUR,25.00,0.55,24.50\n");

        Assert.That(file.Stats.Rejected, Is.EqualTo(1));
        Assert.That(file.Stats.Issues.Single().Message, Does.Contain("does not equal gross_amount minus fee_amount (24.45)"));
    }

    [Test]
    public async Task Column_order_comes_from_the_header_and_optional_columns_may_be_absent()
    {
        var file = await Read(
            "currency,gross_amount,fee_amount,merchant_ref,txn_type,settlement_date\r\n"
            + "SEK,1250.00,12.50,TX-9,SALE,2026-11-04\r\n");

        var record = file.Items.Single();
        Assert.That(record.Gross, Is.EqualTo(new Money(125000, Currency.SEK)));
        Assert.That(record.Reference, Is.EqualTo("TX-9"));
    }

    [Test]
    public void A_header_without_required_columns_fails_the_file()
    {
        Assert.That(
            () => Read("settlement_date,merchant_ref\n2026-11-03,TX-1\n"),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("txn_type"));
    }

    [TestCase("2026-11-03,ORB-1,TX-1,,SALE,XXX,25.00,0.55,24.45", "currency 'XXX'")]
    [TestCase("2026-11-03,ORB-1,TX-1,,SALE,EUR,25.001,0.55,24.45", "gross_amount '25.001'")]
    [TestCase("03/11/2026,ORB-1,TX-1,,SALE,EUR,25.00,0.55,24.45", "settlement_date")]
    [TestCase("2026-11-03,ORB-1,TX-1,,VOID,EUR,25.00,0.55,24.45", "txn_type 'VOID'")]
    [TestCase("2026-11-03,ORB-1,,,SALE,EUR,25.00,0.55,24.45", "merchant_ref is empty")]
    [TestCase("2026-11-03,ORB-1,TX-1", "Expected at least")]
    public async Task Malformed_lines_are_rejected_and_the_rest_of_the_file_still_parses(string badLine, string expected)
    {
        var file = await Read(Header + badLine + "\n2026-11-03,ORB-1,TX-2,,SALE,EUR,1.00,0.00,1.00\n");

        Assert.That(file.Items.Single().Reference, Is.EqualTo("TX-2"));
        Assert.That(file.Stats.Issues.Single().Message, Does.Contain(expected));
    }
}
