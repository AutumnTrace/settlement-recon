using SettlementRecon.Matching;
using SettlementRecon.Monetary;

namespace SettlementRecon.Tests.Matching;

public class MatchRulesTests
{
    private static readonly ReconciliationOptions Options = new()
    {
        AmountToleranceMinor = 2,
        AmountToleranceBps = 10,
        FeeToleranceMinor = 1,
        DateWindowDays = 3,
    };

    [Test]
    public void Identical_amounts_and_fees_match_on_reference()
    {
        var verdict = MatchRules.Compare(2500, 55, Currency.EUR, 2500, 55, Currency.EUR, Options);

        Assert.That(verdict.Status, Is.EqualTo(ReconStatus.Matched));
        Assert.That(verdict.Rule, Is.EqualTo(MatchRule.Reference));
    }

    [Test]
    public void Small_fx_rounding_difference_matches_within_tolerance()
    {
        var verdict = MatchRules.Compare(2500, null, Currency.EUR, 2502, 0, Currency.EUR, Options);

        Assert.That(verdict.Status, Is.EqualTo(ReconStatus.Matched));
        Assert.That(verdict.Rule, Is.EqualTo(MatchRule.ReferenceWithinTolerance));
        Assert.That(verdict.AmountDifference, Is.EqualTo(2));
    }

    [TestCase(100_000, 100, ReconStatus.Matched)] // 10 bps of 1,000.00 = 1.00
    [TestCase(100_000, 101, ReconStatus.AmountMismatch)]
    [TestCase(1_000, 2, ReconStatus.Matched)] // absolute floor of 2 minor units wins on small amounts
    [TestCase(1_000, 3, ReconStatus.AmountMismatch)]
    [TestCase(-100_000, -100, ReconStatus.Matched)] // refunds use the magnitude
    public void Tolerance_is_the_larger_of_absolute_and_relative(long ledger, long difference, ReconStatus expected)
    {
        var verdict = MatchRules.Compare(ledger, null, Currency.EUR, ledger + difference, 0, Currency.EUR, Options);

        Assert.That(verdict.Status, Is.EqualTo(expected));
    }

    [Test]
    public void Fee_drift_beyond_tolerance_is_flagged_even_when_the_amount_agrees()
    {
        var verdict = MatchRules.Compare(2500, 55, Currency.EUR, 2500, 75, Currency.EUR, Options);

        Assert.That(verdict.Status, Is.EqualTo(ReconStatus.MatchedWithFeeDifference));
        Assert.That(verdict.FeeDifference, Is.EqualTo(20));
    }

    [Test]
    public void Fee_is_not_checked_when_the_ledger_did_not_expect_one()
    {
        var verdict = MatchRules.Compare(2500, null, Currency.EUR, 2500, 999, Currency.EUR, Options);

        Assert.That(verdict.Status, Is.EqualTo(ReconStatus.Matched));
    }

    [Test]
    public void Currency_mismatch_wins_over_amount_checks()
    {
        var verdict = MatchRules.Compare(2500, 55, Currency.EUR, 2500, 55, Currency.GBP, Options);

        Assert.That(verdict.Status, Is.EqualTo(ReconStatus.CurrencyMismatch));
    }

    [Test]
    public void Batch_total_is_compared_with_the_payout_line()
    {
        var matched = MatchRules.CompareBatch(-450_000, Currency.EUR, false, -450_000, Currency.EUR, Options);
        var off = MatchRules.CompareBatch(-450_000, Currency.EUR, false, -448_000, Currency.EUR, Options);
        var mixed = MatchRules.CompareBatch(-450_000, Currency.EUR, true, -450_000, Currency.EUR, Options);

        Assert.That(matched.Rule, Is.EqualTo(MatchRule.BatchPayout));
        Assert.That(off.Status, Is.EqualTo(ReconStatus.AmountMismatch));
        Assert.That(off.AmountDifference, Is.EqualTo(2_000));
        Assert.That(mixed.Status, Is.EqualTo(ReconStatus.CurrencyMismatch));
    }

    [TestCase(0, true)]
    [TestCase(3, true)]
    [TestCase(-3, true)]
    [TestCase(4, false)]
    public void Amount_and_date_candidates_respect_the_date_window(int dayOffset, bool expected)
    {
        var booked = new DateOnly(2026, 11, 2);

        var candidate = MatchRules.IsAmountAndDateCandidate(
            new Money(5000, Currency.EUR), booked.AddDays(dayOffset), new Money(5000, Currency.EUR), booked, Options);

        Assert.That(candidate, Is.EqualTo(expected));
    }

    [Test]
    public void Amount_and_date_candidates_need_the_same_currency()
    {
        var day = new DateOnly(2026, 11, 2);

        Assert.That(
            MatchRules.IsAmountAndDateCandidate(new Money(5000, Currency.EUR), day, new Money(5000, Currency.USD), day, Options),
            Is.False);
    }
}
