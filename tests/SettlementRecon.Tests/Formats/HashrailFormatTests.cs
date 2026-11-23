using SettlementRecon.Domain;
using SettlementRecon.Formats;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Formats;

public class HashrailFormatTests
{
    private static Task<ParsedFile<SettlementRecord>> Read(params string[] lines) =>
        RecordFiles.ReadSettlementAsync(TestStreams.FromText(string.Join("\n", lines)), new HashrailFormat());

    [Test]
    public async Task Confirmed_inbound_transfer_settles_gross_with_the_network_fee()
    {
        var file = await Read("""{"id":"hr_1","merchant_reference":"TX-3","direction":"in","asset":"USDT","network":"tron","amount":"125.500000","network_fee":"1.000000","status":"confirmed","confirmed_at":"2026-11-03T10:22:11Z"}""");

        var record = file.Items.Single();
        Assert.That(record.Kind, Is.EqualTo(SettlementKind.Payment));
        Assert.That(record.Gross, Is.EqualTo(new Money(125_500_000, Currency.USDT)));
        Assert.That(record.Fee, Is.EqualTo(new Money(1_000_000, Currency.USDT)));
        Assert.That(record.ValueDate, Is.EqualTo(new DateOnly(2026, 11, 3)));
    }

    [Test]
    public async Task Outbound_transfers_are_signed_negative()
    {
        var file = await Read("""{"merchant_reference":"WD-7","direction":"out","asset":"USDC","amount":50,"network_fee":0.25,"status":"confirmed","confirmed_at":"2026-11-03T08:00:00Z"}""");

        Assert.That(file.Items.Single().Gross.Minor, Is.EqualTo(-50_000_000));
        Assert.That(file.Items.Single().Kind, Is.EqualTo(SettlementKind.Payout));
    }

    [Test]
    public async Task Pending_and_failed_transfers_are_skipped_not_rejected()
    {
        var file = await Read(
            """{"merchant_reference":"TX-1","direction":"in","asset":"USDT","amount":"1","status":"pending","confirmed_at":null}""",
            """{"merchant_reference":"TX-2","direction":"in","asset":"USDT","amount":"1","status":"failed"}""");

        Assert.That(file.Items, Is.Empty);
        Assert.That(file.Stats.Skipped, Is.EqualTo(2));
        Assert.That(file.Stats.Rejected, Is.Zero);
    }

    [Test]
    public async Task Escaped_strings_nested_objects_and_offsets_are_handled()
    {
        var file = await Read("""{"meta":{"tags":["a","b"]},"merchant_reference":"TX-42","direction":"in","asset":"usdt","amount":"10","status":"confirmed","confirmed_at":"2026-11-03T00:30:00+02:00"}""");

        var record = file.Items.Single();
        Assert.That(record.Reference, Is.EqualTo("TX-42"));
        Assert.That(record.ValueDate, Is.EqualTo(new DateOnly(2026, 11, 2)));
    }

    [TestCase("""{"merchant_reference":"TX-1",""", "Invalid JSON")]
    [TestCase("""[1,2,3]""", "not a JSON object")]
    [TestCase("""{"merchant_reference":"TX-1","direction":"in","asset":"USDT","amount":"-5","status":"confirmed","confirmed_at":"2026-11-03"}""", "unsigned")]
    [TestCase("""{"merchant_reference":"TX-1","direction":"in","asset":"DOGE","amount":"5","status":"confirmed","confirmed_at":"2026-11-03"}""", "asset")]
    [TestCase("""{"merchant_reference":"TX-1","asset":"USDT","amount":"5","status":"confirmed","confirmed_at":"2026-11-03"}""", "direction")]
    [TestCase("""{"merchant_reference":"TX-1","direction":"in","asset":"USDT","amount":"5"}""", "status is missing")]
    public async Task Malformed_lines_are_rejected_with_a_reason(string line, string expected)
    {
        var file = await Read(line);

        Assert.That(file.Items, Is.Empty);
        Assert.That(file.Stats.Issues.Single().Message, Does.Contain(expected));
    }
}
