using SettlementRecon.Domain;
using SettlementRecon.Ledger;
using SettlementRecon.Monetary;
using SettlementRecon.Parsing;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Formats;

public class LedgerFormatsTests
{
    private const string Csv = """
        reference,provider,type,amount,currency,expected_fee,booked_at,batch_id
        TX-1,orbit,deposit,25.00,EUR,0.55,2026-11-02T10:15:00Z,
        WD-2,canal,withdrawal,-120.00,EUR,,2026-11-02T11:00:00Z,PO-20261103-01
        TX-3,hashrail,deposit,125.500000,USDT,,2026-11-02T12:00:00Z,
        """;

    private const string JsonLines = """
        {"reference":"TX-1","provider":"orbit","type":"deposit","amount":"25.00","currency":"EUR","expected_fee":"0.55","booked_at":"2026-11-02T10:15:00Z","batch_id":null}
        {"reference":"WD-2","provider":"canal","type":"withdrawal","amount":-120.00,"currency":"EUR","booked_at":"2026-11-02T11:00:00Z","batch_id":"PO-20261103-01"}
        {"reference":"TX-3","provider":"hashrail","type":"deposit","amount":"125.500000","currency":"USDT","expected_fee":null,"booked_at":"2026-11-02T12:00:00Z"}
        """;

    [Test]
    public async Task Csv_and_json_lines_exports_produce_the_same_entries()
    {
        var fromCsv = await RecordFiles.ReadLedgerAsync(TestStreams.FromText(Csv), LedgerFileFormat.Csv);
        var fromJson = await RecordFiles.ReadLedgerAsync(TestStreams.FromText(JsonLines), LedgerFileFormat.JsonLines);

        Assert.That(fromCsv.Stats.Rejected + fromJson.Stats.Rejected, Is.Zero,
            string.Join("; ", fromCsv.Stats.Issues.Concat(fromJson.Stats.Issues)));

        // Line numbers differ by one because the CSV has a header; everything else must agree.
        Assert.That(fromJson.Items, Is.EqualTo(fromCsv.Items.Select(e => e with { Line = e.Line - 1 })));
    }

    [Test]
    public async Task Entries_carry_expected_fees_and_batch_ids_when_present()
    {
        var file = await RecordFiles.ReadLedgerAsync(TestStreams.FromText(Csv), LedgerFileFormat.Csv);

        var deposit = file.Items[0];
        var withdrawal = file.Items[1];
        Assert.That(deposit.ExpectedFee, Is.EqualTo(new Money(55, Currency.EUR)));
        Assert.That(deposit.BatchId, Is.Null);
        Assert.That(withdrawal.Type, Is.EqualTo(LedgerEntryType.Withdrawal));
        Assert.That(withdrawal.Amount.Minor, Is.EqualTo(-12000));
        Assert.That(withdrawal.ExpectedFee, Is.Null);
        Assert.That(withdrawal.BatchId, Is.EqualTo("PO-20261103-01"));
        Assert.That(file.Items[2].Amount, Is.EqualTo(new Money(125_500_000, Currency.USDT)));
    }

    [TestCase("TX-9,orbit,deposit,-5.00,EUR,,2026-11-02,", "wrong sign")]
    [TestCase("TX-9,orbit,withdrawal,5.00,EUR,,2026-11-02,", "wrong sign")]
    [TestCase("TX-9,orbit,bonus,5.00,EUR,,2026-11-02,", "type 'bonus'")]
    [TestCase("TX-9,,deposit,5.00,EUR,,2026-11-02,", "provider is empty")]
    [TestCase("TX-9,orbit,deposit,5.00,EUR,,yesterday,", "booked_at")]
    public async Task Invalid_ledger_lines_are_rejected(string line, string expected)
    {
        var file = await RecordFiles.ReadLedgerAsync(
            TestStreams.FromText("reference,provider,type,amount,currency,expected_fee,booked_at,batch_id\n" + line),
            LedgerFileFormat.Csv);

        Assert.That(file.Stats.Issues.Single().Message, Does.Contain(expected));
    }

    [TestCase("ledger.csv", LedgerFileFormat.Csv)]
    [TestCase("LEDGER.JSONL", LedgerFileFormat.JsonLines)]
    [TestCase("export.ndjson", LedgerFileFormat.JsonLines)]
    public void Format_is_chosen_by_extension(string path, LedgerFileFormat expected)
    {
        Assert.That(LedgerFormats.FromPath(path), Is.EqualTo(expected));
    }

    [Test]
    public void Unknown_extensions_are_refused()
    {
        Assert.That(() => LedgerFormats.FromPath("ledger.xlsx"), Throws.TypeOf<NotSupportedException>());
    }
}
