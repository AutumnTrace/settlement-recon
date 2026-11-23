using System.Text;
using SettlementRecon.Formats;
using SettlementRecon.Ledger;
using SettlementRecon.Matching;
using SettlementRecon.Pipeline;
using SettlementRecon.Reporting;

namespace SettlementRecon.Tests.Pipeline;

/// <summary>
/// One small, hand-written data set that contains every outcome exactly where we put it, so each assertion
/// can name the transaction and the sentence a person would read.
/// </summary>
public class EngineScenarioTests
{
    private const string Ledger = """
        reference,provider,type,amount,currency,expected_fee,booked_at,batch_id
        TX-1,orbit,deposit,25.00,EUR,0.55,2026-11-02T10:00:00Z,
        TX-2,orbit,deposit,40.00,EUR,0.76,2026-11-02T10:05:00Z,
        TX-3,orbit,deposit,10.00,EUR,0.34,2026-11-02T10:10:00Z,
        TX-4,orbit,deposit,30.00,EUR,,2026-11-02T10:15:00Z,
        TX-5,orbit,deposit,12.00,EUR,,2026-11-02T10:20:00Z,
        TX-6,orbit,deposit,100.00,EUR,,2026-11-02T10:25:00Z,
        TX-7,orbit,deposit,15.00,EUR,,2026-11-02T10:30:00Z,
        RF-8,orbit,refund,-20.00,EUR,0.00,2026-11-02T10:35:00Z,
        TX-9,orbit,deposit,18.00,EUR,,2026-11-02T10:40:00Z,
        WD-10,canal,withdrawal,-50.00,EUR,,2026-11-02T11:00:00Z,PO-1
        WD-11,canal,withdrawal,-70.00,EUR,,2026-11-02T11:05:00Z,PO-1
        TX-12,canal,deposit,80.00,EUR,0.35,2026-11-02T11:10:00Z,
        WD-13,canal,withdrawal,-33.00,EUR,,2026-11-02T11:15:00Z,PO-2
        TX-14,hashrail,deposit,125.50,USDT,,2026-11-02T12:00:00Z,
        TX-15,elsewhere,deposit,1.00,EUR,,2026-11-02T12:00:00Z,
        TX-7,orbit,deposit,15.00,EUR,,2026-11-02T13:00:00Z,
        """;

    private const string Orbit = """
        settlement_date,merchant_ref,txn_type,currency,gross_amount,fee_amount,net_amount
        2026-11-03,TX-1,SALE,EUR,25.00,0.55,24.45
        2026-11-03,TX-2,SALE,EUR,39.00,0.75,38.25
        2026-11-03,TX-3,SALE,EUR,10.00,0.50,9.50
        2026-11-03,TX-4,SALE,GBP,26.00,0.56,25.44
        2026-11-03,TX-6,SALE,EUR,100.01,1.60,98.41
        2026-11-03,TX-7,SALE,EUR,15.00,0.41,14.59
        2026-11-03,RF-8,REFUND,EUR,-20.00,0.00,-20.00
        2026-11-03,TX-9,SALE,EUR,18.00,0.45,17.55
        2026-11-03,TX-99,SALE,EUR,64.00,1.10,62.90
        2026-11-04,TX-9,SALE,EUR,18.00,0.45,17.55
        """;

    private const string Canal = """
        value_date;reference;description;amount;costs;currency;kind
        03.11.2026;PO-1;Payout batch;-120,00;1,00;EUR;PAYOUT_BATCH
        03.11.2026;TX12 DEPOSIT;Deposit;80,00;0,35;EUR;CREDIT
        03.11.2026;PO-2;Payout batch;-30,00;0,50;EUR;PAYOUT_BATCH
        """;

    private const string Hashrail = """
        {"merchant_reference":"TX-14","direction":"in","asset":"USDT","amount":"125.5","network_fee":"1","status":"confirmed","confirmed_at":"2026-11-02T12:05:00Z"}
        {"merchant_reference":"TX-16","direction":"in","asset":"USDT","amount":"5","status":"pending"}
        """;

    private ReconciliationReport _report = null!;

    [OneTimeSetUp]
    public async Task Run()
    {
        _report = await new ReconciliationEngine(new ReconciliationOptions()).RunAsync(Request());
    }

    internal static ReconciliationRequest Request(string? orbit = null, string? ledger = null) => new(
        InputFile.FromBytes("ledger.csv", Encoding.UTF8.GetBytes(ledger ?? Ledger)),
        LedgerFileFormat.Csv,
        [
            new SettlementInput(new OrbitCardFormat(), InputFile.FromBytes("orbit.csv", Encoding.UTF8.GetBytes(orbit ?? Orbit))),
            new SettlementInput(new CanalTransferFormat(), InputFile.FromBytes("canal.csv", Encoding.UTF8.GetBytes(Canal))),
            new SettlementInput(new HashrailFormat(), InputFile.FromBytes("hashrail.jsonl", Encoding.UTF8.GetBytes(Hashrail))),
        ]);

    private ProviderReport Provider(string id) => _report.Providers.Single(p => p.Provider == id);

    private ReconItem Break(string reference, ReconStatus status) =>
        _report.Breaks.Single(b => b.Reference == reference && b.Status == status);

    [Test]
    public void Every_settlement_line_gets_exactly_one_outcome()
    {
        var orbit = Provider("orbit");

        Assert.That(orbit.SettlementLines, Is.EqualTo(10));
        Assert.That(orbit.LedgerEntries, Is.EqualTo(10));
        Assert.That(orbit.Outcomes, Is.EqualTo(new OutcomeCounts(
            Matched: 5, MatchedWithFeeDifference: 1, AmountMismatch: 1, MissingInLedger: 1, MissingAtPsp: 1, Duplicate: 2, CurrencyMismatch: 1)));
        Assert.That(orbit.MatchedByRule, Is.EqualTo(new RuleCounts(Reference: 5, ReferenceWithinTolerance: 1, BatchPayout: 0, AmountAndDate: 0)));
        Assert.That(orbit.Outcomes.Total, Is.EqualTo(orbit.SettlementLines + orbit.Outcomes.MissingAtPsp + 1), "plus one ledger duplicate");
    }

    [Test]
    public void Amount_mismatch_states_both_amounts_the_difference_and_the_tolerance()
    {
        var item = Break("TX-2", ReconStatus.AmountMismatch);

        Assert.That(item.Reason, Is.EqualTo(
            "PSP settled 39.00 EUR for TX-2 but the ledger booked 40.00 EUR: a difference of -1.00 EUR (-2.50%), outside the tolerance of 0.02 EUR."));
        Assert.That(item.Difference?.Minor, Is.EqualTo(-100));
        Assert.That((item.SettlementLine, item.LedgerLine), Is.EqualTo((3, 3)));
    }

    [Test]
    public void Fee_difference_names_the_charged_and_the_expected_fee()
    {
        Assert.That(Break("TX-3", ReconStatus.MatchedWithFeeDifference).Reason, Is.EqualTo(
            "Amount agrees at 10.00 EUR, but the PSP charged a fee of 0.50 EUR on TX-3 where the ledger expected 0.34 EUR (+0.16 EUR)."));
    }

    [Test]
    public void Currency_mismatch_is_not_compared_as_an_amount()
    {
        var item = Break("TX-4", ReconStatus.CurrencyMismatch);

        Assert.That(item.Reason, Does.StartWith("Ledger booked 30.00 EUR for TX-4 but the PSP settled it in GBP (26.00 GBP)"));
        Assert.That(item.Difference, Is.Null);
    }

    [Test]
    public void Missing_on_either_side_is_explained()
    {
        Assert.That(Break("TX-99", ReconStatus.MissingInLedger).Reason, Is.EqualTo(
            "PSP settled 64.00 EUR (payment, value date 2026-11-03) for TX-99, but no ledger entry has that reference and no unmatched ledger entry has the same amount within 3 days."));
        Assert.That(Break("TX-5", ReconStatus.MissingAtPsp).Reason, Is.EqualTo(
            "Ledger booked a deposit of 12.00 EUR on 2026-11-02 for TX-5, but the PSP settlement has no line for it."));
    }

    [Test]
    public void Duplicates_are_caught_on_both_sides()
    {
        var duplicates = _report.Breaks.Where(b => b.Status == ReconStatus.Duplicate).ToDictionary(b => b.Reference);

        Assert.That(duplicates["TX-9"].Reason, Is.EqualTo(
            "TX-9 is settled twice: line 9 already claimed ledger line 10, and line 11 settles another 18.00 EUR. Paying it again would double-count the transaction."));
        Assert.That(duplicates["TX-7"].Reason, Does.StartWith("The ledger contains TX-7 twice (lines 8 and 17)"));
    }

    [Test]
    public void Fx_rounding_inside_the_tolerance_is_a_match()
    {
        Assert.That(_report.Breaks.Any(b => b.Reference == "TX-6"), Is.False);
        Assert.That(Provider("orbit").MatchedByRule.ReferenceWithinTolerance, Is.EqualTo(1));
    }

    [Test]
    public void Batch_payouts_match_one_line_to_many_ledger_entries()
    {
        var canal = Provider("canal");

        Assert.That(canal.MatchedByRule.BatchPayout, Is.EqualTo(1));
        Assert.That(canal.LedgerEntriesSettledInBatches, Is.EqualTo(3));
        Assert.That(Break("PO-2", ReconStatus.AmountMismatch).Reason, Is.EqualTo(
            "Payout batch PO-2 covers 1 ledger entry totalling -33.00 EUR, but the PSP settled -30.00 EUR: a difference of +3.00 EUR, outside the tolerance of 0.01 EUR."));
    }

    [Test]
    public void A_mangled_bank_reference_is_paired_by_amount_and_date_and_flagged_for_review()
    {
        var review = _report.ReviewMatches.Single();

        Assert.That(review.Reference, Is.EqualTo("TX12 DEPOSIT"));
        Assert.That(review.LedgerReference, Is.EqualTo("TX-12"));
        Assert.That(review.Rule, Is.EqualTo(MatchRule.AmountAndDate));
        Assert.That(review.Reason, Does.Contain("paired with ledger entry TX-12 (line 13) on amount 80.00 EUR and dates 1 day apart"));
        Assert.That(Provider("canal").Outcomes.MissingInLedger, Is.Zero);
    }

    [Test]
    public void Pending_transfers_and_other_providers_stay_out_of_the_outcome()
    {
        var hashrail = Provider("hashrail");

        Assert.That(hashrail.Outcomes.Matched, Is.EqualTo(1));
        Assert.That(hashrail.Files.Single().Skipped, Is.EqualTo(1));
        Assert.That(_report.LedgerEntriesOutOfScope, Is.EqualTo(1));
    }

    [Test]
    public void Breaks_are_ordered_by_provider_then_type_then_line()
    {
        var keys = _report.Breaks.Select(b => (b.Provider, (int)b.Status)).ToArray();

        Assert.That(keys, Is.EqualTo(keys.OrderBy(k => k.Provider, StringComparer.Ordinal).ThenBy(k => k.Item2).ToArray()));
    }

    [Test]
    public void The_report_records_the_tolerances_it_used()
    {
        Assert.That(_report.Settings, Is.EqualTo(ReconSettings.From(new ReconciliationOptions())));
        Assert.That(_report.Summary.Breaks, Is.EqualTo(_report.Breaks.Count));
    }
}
