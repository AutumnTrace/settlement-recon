using SettlementRecon.Domain;
using SettlementRecon.Matching;
using SettlementRecon.Monetary;

namespace SettlementRecon.Tests.Matching;

public class AmountAndDateMatcherTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly ReconciliationOptions Options = new() { DateWindowDays = 3, AmountToleranceMinor = 1, AmountToleranceBps = 0 };

    private static SettlementRecord Line(string reference, long gross, int dayOffset = 1, Currency? currency = null) =>
        new("canal", reference, SettlementKind.Payment, new Money(gross, currency ?? Currency.EUR), new Money(35, currency ?? Currency.EUR), Day.AddDays(dayOffset), 10);

    private static LedgerEntry Entry(string reference, long amount, Currency? currency = null) =>
        new(reference, "canal", LedgerEntryType.Deposit, new Money(amount, currency ?? Currency.EUR), null, Day, null, 5);

    [Test]
    public void Pairs_a_line_with_the_only_entry_of_the_same_amount_inside_the_window()
    {
        var decisions = AmountAndDateMatcher.Match(
            [Line("TX00000012", 8_000)],
            [Entry("TX-00000011", 7_000), Entry("TX-00000012", 8_000), Entry("TX-00000013", 9_000)],
            Options);

        Assert.That(decisions[0], Is.EqualTo(new AmountAndDateMatcher.Decision(1, 1)));
    }

    [Test]
    public void Amounts_within_tolerance_still_pair()
    {
        var decisions = AmountAndDateMatcher.Match([Line("X", 8_001)], [Entry("TX-1", 8_000)], Options);

        Assert.That(decisions[0].LedgerIndex, Is.Zero);
    }

    [Test]
    public void Two_equally_good_candidates_are_reported_as_ambiguous_and_not_paired()
    {
        var decisions = AmountAndDateMatcher.Match([Line("X", 8_000)], [Entry("TX-1", 8_000), Entry("TX-2", 8_000)], Options);

        Assert.That(decisions[0], Is.EqualTo(new AmountAndDateMatcher.Decision(2, -1)));
    }

    [Test]
    public void Outside_the_window_or_in_another_currency_nothing_pairs()
    {
        var decisions = AmountAndDateMatcher.Match(
            [Line("A", 8_000, dayOffset: 4), Line("B", 9_000, currency: Currency.GBP)],
            [Entry("TX-1", 8_000), Entry("TX-2", 9_000)],
            Options);

        Assert.That(decisions.Select(d => d.LedgerIndex), Is.All.EqualTo(-1));
    }

    [Test]
    public void An_entry_is_only_used_once()
    {
        var decisions = AmountAndDateMatcher.Match([Line("A", 8_000), Line("B", 8_000)], [Entry("TX-1", 8_000)], Options);

        Assert.That(decisions[0].LedgerIndex, Is.Zero);
        Assert.That(decisions[1], Is.EqualTo(new AmountAndDateMatcher.Decision(0, -1)));
    }

    [Test]
    public void The_fallback_can_be_switched_off()
    {
        var off = new ReconciliationOptions { AmountAndDateFallback = false };

        var decisions = AmountAndDateMatcher.Match([Line("A", 8_000)], [Entry("TX-1", 8_000)], off);

        Assert.That(decisions[0].LedgerIndex, Is.EqualTo(-1));
    }
}
