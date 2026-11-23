using System.Text;
using SettlementRecon.Monetary;

namespace SettlementRecon.Tests.Monetary;

public class CurrencyTests
{
    [TestCase("EUR", 2)]
    [TestCase("JPY", 0)]
    [TestCase("BHD", 3)]
    [TestCase("USDT", 6)]
    [TestCase("BTC", 8)]
    public void Known_codes_carry_their_minor_unit_exponent(string code, int exponent)
    {
        var currency = Currency.Parse(code);

        Assert.That(currency.Code, Is.EqualTo(code));
        Assert.That(currency.Exponent, Is.EqualTo(exponent));
    }

    [Test]
    public void Utf8_and_text_parsing_agree_and_ignore_case_and_padding()
    {
        Assert.That(Currency.TryParse(" eur "u8, out var fromBytes), Is.True);
        Assert.That(Currency.TryParse("Eur".AsSpan(), out var fromText), Is.True);

        Assert.That(fromBytes, Is.EqualTo(Currency.EUR));
        Assert.That(fromText, Is.EqualTo(Currency.EUR));
    }

    [TestCase("")]
    [TestCase("EU")]
    [TestCase("EURO1")]
    [TestCase("XYZ")]
    [TestCase("E1R")]
    [TestCase("€UR")]
    public void Unknown_or_malformed_codes_are_rejected(string code)
    {
        Assert.That(Currency.TryParse(Encoding.UTF8.GetBytes(code), out _), Is.False);
        Assert.That(Currency.TryParse(code.AsSpan(), out _), Is.False);
    }

    [Test]
    public void Equality_is_by_code()
    {
        Assert.That(Currency.Parse("GBP") == Currency.GBP, Is.True);
        Assert.That(Currency.EUR != Currency.USD, Is.True);
        Assert.That(default(Currency).IsDefault, Is.True);
    }
}
