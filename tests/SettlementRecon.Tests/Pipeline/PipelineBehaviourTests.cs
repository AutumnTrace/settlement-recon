using System.Text;
using SettlementRecon.Formats;
using SettlementRecon.Ledger;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;

namespace SettlementRecon.Tests.Pipeline;

public class PipelineBehaviourTests
{
    [Test]
    public void A_settlement_file_with_a_bad_header_fails_the_run_with_that_error()
    {
        var request = EngineScenarioTests.Request(orbit: "settlement_date,merchant_ref\n2026-11-03,TX-1\n");

        Assert.That(
            () => new ReconciliationEngine(new ReconciliationOptions()).RunAsync(request),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("txn_type"));
    }

    [Test]
    public void A_ledger_with_a_bad_header_fails_the_run_with_that_error()
    {
        var request = EngineScenarioTests.Request(ledger: "reference,amount\nTX-1,1.00\n");

        Assert.That(
            () => new ReconciliationEngine(new ReconciliationOptions()).RunAsync(request),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("provider"));
    }

    [Test]
    public void Cancellation_stops_the_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            () => new ReconciliationEngine(new ReconciliationOptions()).RunAsync(EngineScenarioTests.Request(), cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void A_run_without_settlement_files_is_refused()
    {
        var request = new ReconciliationRequest(InputFile.FromBytes("ledger.csv", []), LedgerFileFormat.Csv, []);

        Assert.That(() => new ReconciliationEngine(new ReconciliationOptions()).RunAsync(request), Throws.ArgumentException);
    }

    [Test]
    public async Task Several_files_for_one_provider_share_the_ledger_index()
    {
        var ledger = "reference,provider,type,amount,currency,booked_at\n"
            + "TX-1,orbit,deposit,1.00,EUR,2026-11-02\n"
            + "TX-2,orbit,deposit,2.00,EUR,2026-11-02\n";
        const string header = "settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount\n";
        var request = new ReconciliationRequest(
            InputFile.FromBytes("ledger.csv", Encoding.UTF8.GetBytes(ledger)),
            LedgerFileFormat.Csv,
            [
                new SettlementInput(new OrbitCardFormat(), InputFile.FromBytes("day1.csv", Encoding.UTF8.GetBytes(header + "2026-11-03,TX-1,SALE,EUR,1.00,0.00\n"))),
                new SettlementInput(new OrbitCardFormat(), InputFile.FromBytes("day2.csv", Encoding.UTF8.GetBytes(header + "2026-11-04,TX-2,SALE,EUR,2.00,0.00\n2026-11-04,TX-1,SALE,EUR,1.00,0.00\n"))),
            ]);

        var report = await new ReconciliationEngine(new ReconciliationOptions()).RunAsync(request);

        var orbit = report.Providers.Single();
        Assert.That(orbit.Files.Select(f => f.Name), Is.EqualTo(new[] { "day1.csv", "day2.csv" }));
        Assert.That(orbit.Outcomes.Matched, Is.EqualTo(2));
        Assert.That(orbit.Outcomes.Duplicate, Is.EqualTo(1), "TX-1 settled on both days");
    }

    [Test]
    public async Task Item_lists_are_capped_but_counts_stay_exact()
    {
        var lines = new StringBuilder("settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount\n");
        for (var i = 0; i < 50; i++)
        {
            lines.Append($"2026-11-03,TX-{i},SALE,EUR,{i + 1}.00,0.00\n");
        }

        var request = new ReconciliationRequest(
            InputFile.FromBytes("ledger.csv", Encoding.UTF8.GetBytes("reference,provider,type,amount,currency,booked_at\n")),
            LedgerFileFormat.Csv,
            [new SettlementInput(new OrbitCardFormat(), InputFile.FromBytes("orbit.csv", Encoding.UTF8.GetBytes(lines.ToString())))]);

        var report = await new ReconciliationEngine(new ReconciliationOptions { MaxItemsPerProvider = 10 }).RunAsync(request);

        Assert.That(report.Summary.Outcomes.MissingInLedger, Is.EqualTo(50));
        Assert.That(report.Breaks, Has.Count.EqualTo(10));
        Assert.That(report.Providers.Single().ItemsTruncated, Is.True);
    }
}
